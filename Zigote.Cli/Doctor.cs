using System.Diagnostics;

namespace Zigote.Cli;

/// <summary>
///     <c>zigote doctor</c> — check the machine for everything Zigote development needs, and say
///     exactly how to fix what is missing.
///     <para>
///         Every check here earned its place by wasting someone's afternoon: the .NET SDK missing
///         from PATH, no checkout found, a stale native engine, the Android workload absent, a JDK
///         that is the wrong major version or a headless build without <c>jar</c> (both fail the
///         Android build with errors that do not name the cure), no Android SDK, and iOS attempted
///         off-macOS. The fix column is the point — a red mark without the command to run is just
///         a longer error message.
///     </para>
/// </summary>
public static class Doctor
{
    private enum Status
    {
        Ok,
        Info,
        Warn,
        Fail,
    }

    private readonly record struct Check(Status Status, string Title, string? Fix = null);

    public static int Run(DoctorVerb options)
    {
        Console.WriteLine("Checking the Zigote development environment…");
        Console.WriteLine();

        // The three process-based probes dominate the wall clock (dotnet workload list alone
        // takes seconds), and none depends on another — so they run while the file-system checks
        // print, um, instantly.
        var dotnetSdk = Task.Run(CheckDotnetSdk);
        var workloads = Task.Run(CheckWorkloads);
        var zig = Task.Run(CheckZig);

        string? engineRoot = CommonVerb.FindEngineRoot(start: options.Directory, explicitPath: options.Engine);

        var checks = new List<Check> {
            dotnetSdk.Result,
            CheckCheckout(engineRoot),
            CheckNativeEngine(engineRoot),
            zig.Result,
        };
        checks.AddRange(workloads.Result);
        checks.Add(CheckJdk());
        checks.Add(CheckAndroidSdk());
        checks.Add(CheckIos());

        foreach (var check in checks) Print(check);

        int failed = checks.Count(c => c.Status == Status.Fail);
        Console.WriteLine();
        Console.WriteLine(
            failed == 0
                ? "• No issues found."
                : $"• {failed} issue{(failed == 1 ? "" : "s")} found — fixes are listed above."
        );
        return failed == 0 ? 0 : 1;
    }

    private static void Print(Check check)
    {
        (string mark, var color) = check.Status switch {
            Status.Ok => ("✓", ConsoleColor.Green),
            Status.Info => ("•", ConsoleColor.Cyan),
            Status.Warn => ("!", ConsoleColor.Yellow),
            _ => ("✗", ConsoleColor.Red),
        };
        Console.Write("[");
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(mark);
        Console.ForegroundColor = previous;
        Console.WriteLine($"] {check.Title}");
        if (check.Fix is not null) Console.WriteLine($"      → {check.Fix}");
    }

    // ── individual checks ─────────────────────────────────────────────────────

    private static Check CheckDotnetSdk()
    {
        string? version = Capture(file: "dotnet", arguments: ["--version"]);
        return version is null
            ? new Check(
                Status: Status.Fail,
                Title: ".NET SDK: not on PATH",
                Fix: "Install from https://dotnet.microsoft.com/download — 10.0 or later."
            )
            : new Check(Status: Status.Ok, Title: $".NET SDK ({version})");
    }

    private static Check CheckCheckout(string? engineRoot) =>
        engineRoot is null
            ? new Check(
                Status: Status.Fail,
                Title: "Zigote checkout: not found",
                Fix: "Clone it next to your projects, set $ZIGOTE_ROOT, or pass --engine <path>."
            )
            : new Check(Status: Status.Ok, Title: $"Zigote checkout ({engineRoot})");

    /// <summary>
    ///     A missing engine binary is a Warn, not a Fail: the very first `dotnet build` of any
    ///     generated project builds it. It is listed because a stale or absent zig-out is the
    ///     difference between a 5-second and a 10-minute first build, and people deserve to know
    ///     which one they are about to get.
    /// </summary>
    private static Check CheckNativeEngine(string? engineRoot)
    {
        if (engineRoot is null)
            return new Check(Status: Status.Info, Title: "Native engine: skipped (no checkout)");

        string lib = Path.Combine(path1: engineRoot, path2: "Zigote.Engine", path3: "zig-out", path4: "lib");
        string name = OperatingSystem.IsWindows() ? "zigote.dll"
            : OperatingSystem.IsMacOS() ? "libzigote.dylib"
            : "libzigote.so";
        return File.Exists(Path.Combine(path1: lib, path2: name))
            ? new Check(Status: Status.Ok, Title: $"Native engine built ({name})")
            : new Check(
                Status: Status.Warn,
                Title: "Native engine: not built yet",
                Fix: "The first `dotnet build` of any app builds it — expect that build to take a while."
            );
    }

    private static Check CheckZig()
    {
        string? version = Capture(file: "zig", arguments: ["version"]);
        return version is null
            ? new Check(
                Status: Status.Info,
                Title: "Zig toolchain: not on PATH (only needed to rebuild the engine by hand; dotnet build drives it otherwise)"
            )
            : new Check(Status: Status.Ok, Title: $"Zig toolchain ({version})");
    }

    private static List<Check> CheckWorkloads()
    {
        string? list = Capture(file: "dotnet", arguments: ["workload", "list"]);
        var checks = new List<Check>();
        if (list is null)
        {
            checks.Add(
                new Check(
                    Status: Status.Fail,
                    Title: "Workloads: `dotnet workload list` failed",
                    Fix: "Fix the .NET SDK installation first."
                )
            );
            return checks;
        }

        checks.Add(
            list.Contains("android")
                ? new Check(Status: Status.Ok, Title: "Android workload")
                : new Check(
                    Status: Status.Fail,
                    Title: "Android workload: not installed",
                    Fix: "dotnet workload install android"
                )
        );

        // iOS tooling only exists on macOS, so its absence elsewhere is a fact, not a failure.
        if (OperatingSystem.IsMacOS())
        {
            checks.Add(
                list.Contains("ios")
                    ? new Check(Status: Status.Ok, Title: "iOS workload")
                    : new Check(
                        Status: Status.Fail,
                        Title: "iOS workload: not installed",
                        Fix: "dotnet workload install ios"
                    )
            );
        }

        return checks;
    }

    /// <summary>
    ///     The JDK check is the fussiest on purpose. .NET for Android requires major version 21
    ///     exactly (25 fails with XA0030), and distro packages split the JDK so that a "jdk" without
    ///     <c>jar</c> exists (Fedora's headless package) — which passes every naive check and then
    ///     fails deep inside the build. So: enumerate every candidate, demand <c>jar</c>, read the
    ///     version from the JDK's own release file, and prefer an exact 21.
    /// </summary>
    private static Check CheckJdk()
    {
        (string Home, int Major)? fallback = null;
        string? best = FindJdk21(out fallback);

        if (best is { } jdk)
            return new Check(Status: Status.Ok, Title: $"JDK 21 ({jdk})");

        string install = OperatingSystem.IsWindows()
            ? "winget install Microsoft.OpenJDK.21"
            : OperatingSystem.IsMacOS()
                ? "brew install --cask microsoft-openjdk@21"
                : "sudo dnf install java-21-openjdk-devel   (or your distro's equivalent — the -devel/full JDK, not headless)";
        return fallback is { } other
            ? new Check(
                Status: Status.Fail,
                Title: $"JDK 21: found only version {(other.Major > 0 ? other.Major : "?")} at {other.Home} — .NET for Android needs 21",
                Fix: $"{install}; then point $JAVA_HOME at it."
            )
            : new Check(
                Status: Status.Fail,
                Title: "JDK 21: no full JDK found (a JRE or headless package without `jar` does not count)",
                Fix: install
            );
    }

    /// <summary>
    ///     The JDK 21 home .NET for Android needs, or null. Shared with <c>zigote device</c>, which
    ///     hands it to the build as <c>JavaSdkDirectory</c> — a JDK that only `doctor` can find is a
    ///     JDK the build still fails without.
    /// </summary>
    internal static string? FindJdk21() => FindJdk21(out _);

    private static string? FindJdk21(out (string Home, int Major)? fallback)
    {
        fallback = null;
        foreach (string home in JdkCandidates().Distinct())
        {
            if (!IsFullJdk(home)) continue;
            int major = ReadJavaMajor(home);
            if (major == 21) return home;
            fallback ??= (home, major);
        }

        return null;
    }

    private static IEnumerable<string> JdkCandidates()
    {
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome)
            yield return Path.GetFullPath(javaHome);

        foreach (string root in (string[]) [
                     // JetBrains installs JDKs here (Toolbox, and Rider's own "Download JDK"), which on a
                     // machine whose distro ships only a headless package is the one full JDK present.
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"),
                     "/usr/lib/jvm",
                     "/Library/Java/JavaVirtualMachines",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                 ])
        {
            if (!Directory.Exists(root)) continue;
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                // macOS bundles nest the real home one level down.
                string nested = Path.Combine(path1: dir, path2: "Contents", path3: "Home");
                yield return Directory.Exists(nested) ? nested : dir;
            }
        }
    }

    /// <summary>`jar` is the canary: JREs and headless builds lack it, full JDKs always have it.</summary>
    private static bool IsFullJdk(string home) =>
        File.Exists(Path.Combine(path1: home, path2: "bin", path3: OperatingSystem.IsWindows() ? "jar.exe" : "jar"));

    /// <summary>
    ///     Major version from the JDK's own <c>release</c> file (<c>JAVA_VERSION="21.0.5"</c>) —
    ///     present in every modern JDK and free, where spawning `java -version` costs a JVM start
    ///     per candidate. 0 when unreadable.
    /// </summary>
    private static int ReadJavaMajor(string home)
    {
        try
        {
            string release = Path.Combine(path1: home, path2: "release");
            if (!File.Exists(release)) return 0;
            foreach (string line in File.ReadLines(release))
            {
                if (!line.StartsWith(value: "JAVA_VERSION=", comparisonType: StringComparison.Ordinal)) continue;
                var version = line.AsSpan("JAVA_VERSION=".Length).Trim('"');
                // "1.8.0" is how the 8-era wrote itself; everything current leads with the major.
                if (version.StartsWith("1.")) version = version[2..];
                int dot = version.IndexOf('.');
                return int.TryParse(dot < 0 ? version : version[..dot], out int major) ? major : 0;
            }
        }
        catch (IOException)
        {
            // An unreadable candidate is just not a usable JDK.
        }

        return 0;
    }

    private static Check CheckAndroidSdk()
    {
        foreach (string? candidate in (ReadOnlySpan<string?>) [
                     Environment.GetEnvironmentVariable("ANDROID_HOME"),
                     Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Android", "Sdk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Android", "sdk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
                 ])
        {
            if (candidate is { Length: > 0 } &&
                Directory.Exists(Path.Combine(path1: candidate, path2: "platforms")))
                return new Check(Status: Status.Ok, Title: $"Android SDK ({candidate})");
        }

        return new Check(
            Status: Status.Fail,
            Title: "Android SDK: not found",
            Fix: "Install via Android Studio or `sdkmanager`, then set $ANDROID_HOME."
        );
    }

    private static Check CheckIos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new Check(
                Status: Status.Info,
                Title: "iOS: requires macOS with Xcode — this machine targets desktop and Android"
            );
        }

        return Capture(file: "xcode-select", arguments: ["-p"]) is { } path
            ? new Check(Status: Status.Ok, Title: $"Xcode ({path})")
            : new Check(
                Status: Status.Fail,
                Title: "Xcode: command-line tools not configured",
                Fix: "xcode-select --install"
            );
    }

    /// <summary>
    ///     Trimmed stdout of a tool, or null when it is absent, fails, or hangs. The timeout is
    ///     what keeps a wedged tool from wedging the doctor — a diagnostic that hangs is worse
    ///     than the problems it diagnoses.
    /// </summary>
    private static string? Capture(string file, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file) {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in arguments) start.ArgumentList.Add(a);

            using var process = Process.Start(start);
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 && output.Length > 0 ? output.Trim() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // not installed, not on PATH
        }
    }
}
