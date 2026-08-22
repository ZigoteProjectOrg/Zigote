using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Zigote.Cli;

/// <summary>
///     <c>zigote device</c> — the loop on a real phone: build for the device that is plugged in,
///     deploy, run, and reload edits into the running app without reinstalling it.
///     <para>
///         Everything here is a wrapper, deliberately. .NET 10's Android SDK already ships the whole
///         hot-reload path — <c>dotnet watch</c> compiles a metadata delta, hands the app a startup
///         hook and a websocket endpoint, and the SDK runs <c>adb reverse</c> so the device can dial
///         back to the host. Zigote's side already exists too: the runtime calls
///         <c>ZigoteHotReloadHandler</c>, and <c>App.Frame</c> re-runs every <c>Build()</c> on the
///         next frame. The two halves simply never met on a device, because of three things a person
///         had to know: the delta is ignored unless the head is built with
///         <c>-p:ZigoteHotReload=true</c> (see build/Zigote.Android.targets), the native engine needs
///         the RID that matches the device's ABI, and neither is discoverable from the error you get
///         when you forget. This verb knows all three.
///     </para>
/// </summary>
public static class Device
{
    public static int Run(DeviceVerb options)
    {
        string adb = FindAdb();
        return options.Action switch {
            "list" => List(adb),
            "run" => Launch(options: options, adb: adb),
            "logs" => Logs(options: options, adb: adb),
            _ => throw new CliError(
                $"unknown action '{options.Action}'. Use: list, run, logs."
            ),
        };
    }

    // ── devices ───────────────────────────────────────────────────────────────

    /// <summary>One attached device, as <c>adb devices -l</c> reports it.</summary>
    internal readonly record struct Attached(string Serial, string State, string Model)
    {
        public bool Ready => State == "device";

        public override string ToString() =>
            Ready ? $"{Serial}  {Model}" : $"{Serial}  {Model} ({State})";
    }

    private static int List(string adb)
    {
        var devices = Parse(Capture(file: adb, arguments: ["devices", "-l"]));
        if (devices.Count == 0)
        {
            Console.WriteLine("No devices. Plug one in with USB debugging on, or start an emulator.");
            return 1;
        }

        foreach (var device in devices) Console.WriteLine($"  {device}");
        return 0;
    }

    /// <summary>
    ///     Read <c>adb devices -l</c>. Lines are "serial state key:value key:value"; the header and
    ///     the daemon's own chatter ("* daemon started successfully") have no tab-separated shape and
    ///     fall out here rather than becoming a device with a nonsense serial.
    /// </summary>
    internal static List<Attached> Parse(string? output)
    {
        var devices = new List<Attached>();
        foreach (string line in (output ?? "").Split('\n'))
        {
            string[] parts = line.Trim()
                .Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] == "List" || parts[0].StartsWith('*')) continue;

            string model = parts
                                .FirstOrDefault(p => p.StartsWith(
                                        value: "model:",
                                        comparisonType: StringComparison.Ordinal
                                    )
                                )?["model:".Length..]
                            ?? parts[0];
            devices.Add(new Attached(Serial: parts[0], State: parts[1], Model: model));
        }

        return devices;
    }

    /// <summary>
    ///     The device to work on: the one that is attached, or the one <c>--serial</c> named. Two
    ///     devices and no choice is an error rather than a guess — deploying to the wrong phone is
    ///     silent, and the fix (naming one) is a word.
    /// </summary>
    private static Attached Select(string adb, string? serial)
    {
        var devices = Parse(Capture(file: adb, arguments: ["devices", "-l"]));
        var ready = devices.Where(d => d.Ready).ToList();

        if (serial is not null)
        {
            foreach (var candidate in ready)
                if (candidate.Serial == serial)
                    return candidate;
            throw new CliError(
                $"no ready device with serial '{serial}'. `zigote device list` shows what is attached."
            );
        }

        return ready.Count switch {
            1 => ready[0],
            0 when devices.Count > 0 => throw new CliError(
                $"the attached device is not ready: {devices[0]}. " +
                "Unlock it and accept the USB-debugging prompt."
            ),
            0 => throw new CliError(
                "no device attached. Plug one in with USB debugging on, or start an emulator."
            ),
            _ => throw new CliError(
                "more than one device: " + string.Join(", ", ready.Select(d => d.Serial)) +
                ". Pick one with --serial."
            ),
        };
    }

    /// <summary>
    ///     The RID for a device's primary ABI. This is the value the whole build hangs off — it
    ///     selects the managed RID AND cross-compiles the native engine — and it is exactly the thing
    ///     a person gets wrong by hand, because an arm64 lib on an x86_64 emulator installs fine and
    ///     dies at the first dlopen.
    /// </summary>
    internal static string Rid(string? abi) =>
        abi?.Trim() switch {
            "arm64-v8a" => "android-arm64",
            "x86_64" => "android-x64",
            null or "" => throw new CliError("the device did not report an ABI (ro.product.cpu.abi)."),
            var other => throw new CliError(
                $"unsupported device ABI '{other}'. The engine ships arm64-v8a and x86_64."
            ),
        };

    // ── run ───────────────────────────────────────────────────────────────────

    private static int Launch(DeviceVerb options, string adb)
    {
        var device = Select(adb: adb, serial: options.Serial);
        string head = HeadProject(options.Directory);
        string rid = Rid(
            Capture(file: adb, arguments: ["-s", device.Serial, "shell", "getprop", "ro.product.cpu.abi"])
        );

        bool sdkCanReload = SupportsDeviceHotReload(out string sdk);
        bool reload = !options.NoReload && !options.Release && sdkCanReload;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        // `dotnet watch` is what produces the deltas; without it this is a plain deploy-and-run.
        if (reload)
        {
            start.ArgumentList.Add("watch");
            start.ArgumentList.Add("--non-interactive");
        }

        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(head);
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(options.Release ? "Release" : "Debug");
        // --property (not -p): under `dotnet run`, -p is --project, and the resulting error names
        // neither. These have to be global properties anyway — ZigTargetRid is read during the
        // referenced Zigote.Core's evaluation, which a project-local property never reaches.
        start.ArgumentList.Add($"--property:ZigTargetRid={rid}");
        if (reload) start.ArgumentList.Add("--property:ZigoteHotReload=true");
        // Which device the SDK's own adb calls (install, reverse, launch) talk to.
        start.ArgumentList.Add($"--property:AdbTarget=-s {device.Serial}");
        // And WHICH adb — as the private property, deliberately. On SDK 10.0.111 the Android
        // targets only add _AndroidAdbToolPath (the target that computes it) to the run-arguments
        // dependencies when the SDK is 10.0.300 or newer, so on this band $(_AdbToolPath) is never
        // set and the launcher is handed `--adb ""`. It then fails with "Could not locate adb.
        // Please specify --adb" — after a full build, which is a long way to walk for that. Setting
        // it as a global property is what survives: a target's own PropertyGroup cannot overwrite
        // one. Harmless on newer SDKs, where it is the same value the target would compute.
        start.ArgumentList.Add($"--property:_AdbToolPath={adb}");
        start.ArgumentList.Add($"--property:AdbToolPath={Path.GetDirectoryName(adb)}");
        // The Android SDK only looks for a JDK on PATH and in $JAVA_HOME, and fails with XA5300 when
        // neither has one — while a perfectly good JDK 21 sits in ~/.jdks or /usr/lib/jvm, which is
        // the normal state of a machine with Rider installed and no `javac` on PATH. `doctor` already
        // knows how to find it; passing what it finds turns that error into a non-event.
        if (!IsUsableJavaHome(Environment.GetEnvironmentVariable("JAVA_HOME")) &&
            Doctor.FindJdk21() is { } jdk)
            start.ArgumentList.Add($"--property:JavaSdkDirectory={jdk}");
        if (options.Debug)
        {
            // The SDK forwards the soft-debugger port and launches the app suspended on it, so a
            // debugger has something to attach to before any of the app's code has run.
            start.ArgumentList.Add("--property:AndroidAttachDebugger=True");
            start.ArgumentList.Add($"--property:AndroidSdbTargetPort={options.DebugPort}");
            start.ArgumentList.Add($"--property:AndroidSdbHostPort={options.DebugPort}");
        }

        Console.WriteLine($"  {device.Model} ({device.Serial})  →  {rid}");
        if (reload)
        {
            Console.WriteLine("  hot reload on: edit a Build() and save — the tree re-runs in place.");
        }
        else if (!options.NoReload && !options.Release)
        {
            // Said out loud rather than silently degraded: an app that quietly ignores every delta
            // is indistinguishable from a broken framework, and that is the whole failure mode this
            // verb exists to prevent.
            Console.WriteLine(
                $"  hot reload off: shipping deltas to a device needs .NET SDK {MinHotReloadSdk} " +
                $"or newer (this is {sdk}). Deploying and running instead."
            );
        }
        else
        {
            Console.WriteLine("  hot reload off: edits need another `zigote device run`.");
        }
        if (options.Debug)
        {
            Console.WriteLine(
                $"  waiting for a debugger on localhost:{options.DebugPort} before the app starts."
            );
        }

        Console.WriteLine($"  logs: zigote device logs --serial {device.Serial}");
        Console.WriteLine();

        using var process = Process.Start(start)
                            ?? throw new CliError("could not start dotnet — is the .NET SDK on PATH?");
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    ///     The first SDK band whose <c>dotnet watch</c> can reach an app on a phone.
    ///     <para>
    ///         Both halves of device hot reload have to be present. The Android workload has shipped
    ///         its half for a while (Microsoft.Android.Sdk.HotReload.targets: startup hook, websocket
    ///         endpoint, <c>adb reverse</c>), but it is driven entirely by
    ///         <c>@(RuntimeEnvironmentVariable)</c> items that only a newer dotnet-watch writes — see
    ///         dotnet/sdk#52581. On an older SDK `dotnet watch` still builds, deploys and runs; it
    ///         simply never sends a delta, and nothing anywhere says so.
    ///     </para>
    /// </summary>
    private const string MinHotReloadSdk = "10.0.300";

    private static bool SupportsDeviceHotReload(out string version)
    {
        version = Capture(file: "dotnet", arguments: ["--version"])?.Trim() ?? "";
        // Compare the release band only (major.minor.feature); the patch digits are not ordered
        // usefully across bands, and "10.0.111 < 10.0.300" is the whole question.
        static int Band(string v)
        {
            string[] parts = v.Split('.');
            return parts.Length < 3 || !int.TryParse(parts[0], out int major) ||
                   !int.TryParse(parts[1], out int minor) ||
                   !int.TryParse(new string(parts[2].TakeWhile(char.IsDigit).ToArray()), out int patch)
                ? 0
                : major * 1_000_000 + minor * 10_000 + patch / 100 * 100;
        }

        return Band(version) >= Band(MinHotReloadSdk);
    }

    /// <summary>
    ///     Whether $JAVA_HOME is a full JDK — `jar` is the canary, as in <see cref="Doctor" />: a JRE
    ///     or a headless package passes every naive check and then fails deep inside the build.
    /// </summary>
    private static bool IsUsableJavaHome(string? home) =>
        home is { Length: > 0 } && File.Exists(Path.Combine(
                path1: home,
                path2: "bin",
                path3: OperatingSystem.IsWindows() ? "jar.exe" : "jar"
            )
        );

    // ── logs ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The app's logcat and nothing else. Unfiltered logcat on a phone is thousands of lines a
    ///     minute of other people's software, which is why "it crashed and I saw nothing" is the
    ///     normal experience of debugging a first Android build. Filtering by pid catches everything
    ///     the app emits — managed stdout, the engine's own writes (which root.zig drains onto
    ///     logcat), and the Java-side stack trace of a crash — with no tag list to keep in sync.
    /// </summary>
    private static int Logs(DeviceVerb options, string adb)
    {
        var device = Select(adb: adb, serial: options.Serial);
        string appId = ApplicationId(HeadProject(options.Directory));
        string? pid = Capture(
            file: adb,
            arguments: ["-s", device.Serial, "shell", "pidof", "-s", appId]
        )?.Trim();

        var start = new ProcessStartInfo(adb) { UseShellExecute = false };
        foreach (string a in (ReadOnlySpan<string>) ["-s", device.Serial, "logcat", "-v", "time"])
            start.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(pid))
        {
            start.ArgumentList.Add($"--pid={pid}");
        }
        else
        {
            // Not running yet: fall back to the tags the app and engine write under, so `logs` can
            // be started first and catch a crash that happens during startup.
            Console.WriteLine($"  {appId} is not running — showing zigote/runtime tags until it is.");
            foreach (string a in (ReadOnlySpan<string>) [
                         "zigote:V", "DOTNET:V", "monodroid:V", "AndroidRuntime:E", "*:S",
                     ])
                start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start)
                            ?? throw new CliError($"could not start adb ({adb}).");
        process.WaitForExit();
        return process.ExitCode;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     The Android head to deploy: the single <c>*.Android</c> project in the tree. Found rather
    ///     than asked for, to match every other verb — the app is the directory you are standing in.
    /// </summary>
    private static string HeadProject(string root)
    {
        var heads = Directory
            .EnumerateFiles(path: root, searchPattern: "*.csproj", searchOption: SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(p => Path.GetFileNameWithoutExtension(p)
                .EndsWith(value: ".Android", comparisonType: StringComparison.Ordinal)
            )
            .ToList();

        return heads.Count switch {
            1 => heads[0],
            0 => throw new CliError(
                $"no Android head under '{root}'. Run `zigote add android` from inside the app first."
            ),
            _ => throw new CliError(
                "more than one Android head here: " +
                string.Join(separator: ", ", values: heads.Select(Path.GetFileName)) +
                ". Run from inside one of them, or pass --dir."
            ),
        };
    }

    /// <summary>The head's application id, read from the csproj — it is the package name on device.</summary>
    private static string ApplicationId(string head)
    {
        var match = Regex.Match(
            input: File.ReadAllText(head),
            pattern: @"<ApplicationId>\s*([^<\s]+)\s*</ApplicationId>"
        );
        return match.Success
            ? match.Groups[1].Value
            : throw new CliError($"{Path.GetFileName(head)} declares no <ApplicationId>.");
    }

    /// <summary>
    ///     adb, from PATH or the standard SDK location. The SDK path matters more than it looks:
    ///     installing the Android SDK does not put platform-tools on PATH on any of the three hosts.
    /// </summary>
    private static string FindAdb()
    {
        string exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator))
        {
            if (dir.Length > 0 && File.Exists(Path.Combine(path1: dir, path2: exe)))
                return Path.Combine(path1: dir, path2: exe);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string root in (ReadOnlySpan<string>) [
                     Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "",
                     Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? "",
                     Path.Combine(path1: home, path2: "Android", path3: "Sdk"),
                     Path.Combine(path1: home, path2: "Library", path3: "Android", path4: "sdk"),
                     Path.Combine(home, "AppData", "Local", "Android", "Sdk"),
                 ])
        {
            if (root.Length == 0) continue;
            string candidate = Path.Combine(path1: root, path2: "platform-tools", path3: exe);
            if (File.Exists(candidate)) return candidate;
        }

        throw new CliError(
            "adb not found. Install the Android SDK platform-tools, or set ANDROID_HOME. " +
            "`zigote doctor` checks the rest of the toolchain too."
        );
    }

    /// <summary>stdout of a short-lived tool, or null if it could not be run.</summary>
    private static string? Capture(string file, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (string a in arguments) start.ArgumentList.Add(a);
            using var process = Process.Start(start);
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
