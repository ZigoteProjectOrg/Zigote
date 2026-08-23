using System.Text.RegularExpressions;

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

    /// <summary>
    ///     One finding. <paramref name="Fix" /> is the human sentence; <paramref name="FixCommand" />
    ///     is that same cure as something this tool can run under <c>--fix</c>. Most checks have
    ///     only the sentence — "install a JDK" is not a command anyone should hand to a script —
    ///     and a check with no command is simply never offered.
    /// </summary>
    private readonly record struct Check(
        Status Status,
        string Title,
        string? Fix = null,
        string[]? FixCommand = null
    );

    public static int Run(DoctorVerb options)
    {
        if (!options.Json)
        {
            Console.WriteLine("Checking the Zigote development environment…");
            Console.WriteLine();
        }

        // The three process-based probes dominate the wall clock (dotnet workload list alone
        // takes seconds), and none depends on another — so they run while the file-system checks
        // print, um, instantly.
        var dotnetSdk = Task.Run(CheckDotnetSdk);
        var workloads = Task.Run(CheckWorkloads);
        var zig = Task.Run(CheckZig);
        var rust = Task.Run(CheckRust);

        string? engineRoot = CommonVerb.FindEngineRoot(start: options.Directory, explicitPath: options.Engine);
        // Zig and Rust build the engine's native halves, and an app that restores
        // Zigote.Engine.runtime.<rid> never builds them — that is the whole point of shipping the
        // SDK as packages. So they are checked where they are needed (inside a checkout) and
        // mentioned, not demanded, everywhere else.
        bool engineDeveloper = engineRoot is not null;

        var checks = new List<Check> {
            dotnetSdk.Result,
            CheckCheckout(engineRoot),
        };
        if (engineDeveloper)
        {
            checks.Add(CheckNativeEngine(engineRoot));
            checks.Add(zig.Result);
            checks.Add(rust.Result);
        }
        else
        {
            checks.Add(new Check(
                Status: Status.Info,
                Title: "Native toolchains: not needed — the engine arrives as a package (Zigote.Engine.runtime.<rid>)"
            ));
        }

        checks.Add(CheckSdkVersions(options.Directory));
        checks.Add(CheckEngineIntegrity(options.Directory));
        checks.AddRange(workloads.Result);
        checks.Add(CheckJdk());
        checks.Add(CheckAndroidSdk());
        checks.Add(CheckIos());

        int failed = checks.Count(c => c.Status == Status.Fail);
        if (options.Json)
        {
            foreach (var check in checks) Console.WriteLine(AsJson(check));
            return failed == 0 ? 0 : 1;
        }

        foreach (var check in checks) Print(check);

        Console.WriteLine();
        Console.WriteLine(
            failed == 0
                ? "• No issues found."
                : $"• {failed} issue{(failed == 1 ? "" : "s")} found — fixes are listed above."
        );
        return options.Fix ? ApplyFixes(checks) : failed == 0 ? 0 : 1;
    }

    /// <summary>
    ///     <c>--fix</c>: run the cures this tool already knows, after showing them.
    ///     <para>
    ///         The command famous for telling people to accept Android licences instead of accepting
    ///         them is the bar here. Two rules keep it honest: never without <c>--fix</c>, and never
    ///         without printing the exact commands first — these install SDK workloads, which is not
    ///         a thing to do behind someone's back.
    ///     </para>
    /// </summary>
    private static int ApplyFixes(List<Check> checks)
    {
        var fixable = checks.Where(c => c.FixCommand is { Length: > 0 }).ToList();
        var manual = checks.Where(c => c.Status == Status.Fail && c.FixCommand is null).ToList();
        if (fixable.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                manual.Count == 0
                    ? "--fix: nothing to do."
                    : "--fix: nothing here can be fixed automatically — the cures above need you."
            );
            return manual.Count == 0 ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine("--fix will run:");
        foreach (var check in fixable)
            Console.WriteLine($"  {string.Join(separator: ' ', values: check.FixCommand!)}");
        Console.Write("Go ahead? [y/N] ");
        string? answer = Console.IsInputRedirected ? "n" : Console.ReadLine();
        if (!string.Equals(a: answer?.Trim(), b: "y", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Nothing run.");
            return 1;
        }

        foreach (var check in fixable)
        {
            string[] command = check.FixCommand!;
            Console.WriteLine();
            Console.WriteLine($"  → {string.Join(separator: ' ', values: command)}");
            if (Shell.Wait(Shell.Command(file: command[0], arguments: command.Skip(1))) != 0)
            {
                Console.Error.WriteLine("    failed — run it yourself to see why.");
                return 1;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Fixed what could be fixed. Run `zigote doctor` again to confirm.");
        return manual.Count == 0 ? 0 : 1;
    }

    /// <summary>
    ///     One finding as one line of JSON — the shape `tools/rider` should read instead of parsing
    ///     prose, so a rewording here stops being an IDE-plugin bug.
    /// </summary>
    private static string AsJson(Check check) =>
        System.Text.Json.JsonSerializer.Serialize(
            new {
                status = check.Status.ToString().ToLowerInvariant(),
                title = check.Title,
                fix = check.Fix,
                fixCommand = check.FixCommand,
            }
        );

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
        string? version = Probe(file: "dotnet", arguments: ["--version"]);
        return version is null
            ? new Check(
                Status: Status.Fail,
                Title: ".NET SDK: not on PATH",
                Fix: "Install from https://dotnet.microsoft.com/download — 10.0 or later."
            )
            : new Check(Status: Status.Ok, Title: $".NET SDK ({version})");
    }

    /// <summary>
    ///     Having a checkout is a MODE, not a requirement. An app built against the packages needs
    ///     none, and reporting its absence as a failure is how a healthy machine got told it was
    ///     broken. It stays listed because which mode you are in explains everything below it.
    /// </summary>
    private static Check CheckCheckout(string? engineRoot) =>
        engineRoot is null
            ? new Check(
                Status: Status.Info,
                Title: "Zigote SDK: packages (no checkout here — apps reference Zigote.UI by version)"
            )
            : new Check(Status: Status.Ok, Title: $"Zigote checkout ({engineRoot})");

    /// <summary>
    ///     Which SDK the project here is built against, and whether its packages agree with each
    ///     other. Disagreement is the one version bug that survives a clean build: managed and
    ///     native halves ship as one version, so a Zigote.Engine.runtime package a release behind
    ///     restores happily and then throws an ABI mismatch at the first frame — far from the csproj
    ///     line that caused it.
    /// </summary>
    private static Check CheckSdkVersions(string directory)
    {
        var versions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string project in ProjectFiles(directory))
        {
            string text;
            try
            {
                text = File.ReadAllText(project);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (Match match in Regex.Matches(
                         input: text,
                         pattern: """<PackageReference\s+Include="Zigote[^"]*"\s+Version="([^"]+)"""
                     ))
            {
                string version = match.Groups[1].Value;
                // The property indirection is the healthy case — one string for every package.
                if (!version.Contains('$')) versions.Add(version);
            }

            foreach (Match match in Regex.Matches(
                         input: text,
                         pattern: @"<ZigoteSdkVersion[^>]*>([^<$]+)</ZigoteSdkVersion>"
                     ))
                versions.Add(match.Groups[1].Value.Trim());
        }

        return versions.Count switch {
            0 => new Check(Status: Status.Info, Title: "Zigote SDK version: no app project here to read"),
            1 => new Check(Status: Status.Ok, Title: $"Zigote SDK {versions.Min} (this project)"),
            _ => new Check(
                Status: Status.Fail,
                Title: "Zigote packages disagree on a version: " + string.Join(", ", versions),
                Fix: "The managed packages and Zigote.Engine.runtime.<rid> must match, or the app "
                     + "throws an ABI mismatch at startup. Set them all from <ZigoteSdkVersion>."
            ),
        };
    }

    /// <summary>
    ///     The engine binary this app will actually load, checked against the manifest that shipped
    ///     with it (<c>zigote-engine.json</c>: version, commit, and a SHA-256 per file).
    ///     <para>
    ///         Worth a check because the failure it catches is otherwise unreadable. A native lib
    ///         that was replaced by hand, half-copied by a cancelled build, or restored from a
    ///         partially-written cache produces a crash inside the engine with no hint that the file
    ///         is not the released one — and "which build is this?" is the first question any
    ///         support conversation has to answer.
    ///     </para>
    /// </summary>
    private static Check CheckEngineIntegrity(string directory)
    {
        string? manifest = Directory.Exists(directory)
            ? Directory
                .EnumerateFiles(
                    path: directory,
                    searchPattern: "zigote-engine.json",
                    searchOption: SearchOption.AllDirectories
                )
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (manifest is null)
        {
            return new Check(
                Status: Status.Info,
                Title: "Engine build: nothing built here yet (run `zigote run` once)"
            );
        }

        string json = File.ReadAllText(manifest);
        string version = Field(json: json, name: "version") ?? "unknown";
        string? sha = Field(json: json, name: "commit");
        string commit = sha is { Length: >= 7 } ? sha[..7] : "unknown";
        var mismatched = new List<string>();
        foreach (Match file in Regex.Matches(
                     input: json,
                     pattern: "\"([^\"]+)\\.(so|dylib|dll|a)\":\\s*\"sha256:([0-9A-Fa-f]+)\""
                 ))
        {
            string name = file.Groups[1].Value + "." + file.Groups[2].Value;
            string path = Path.Combine(path1: Path.GetDirectoryName(manifest)!, path2: name);
            if (!File.Exists(path))
            {
                mismatched.Add($"{name} (missing)");
                continue;
            }

            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            if (!actual.Equals(value: file.Groups[3].Value, comparisonType: StringComparison.OrdinalIgnoreCase))
                mismatched.Add(name);
        }

        return mismatched.Count == 0
            ? new Check(Status: Status.Ok, Title: $"Engine {version} ({commit}) — checksums match")
            : new Check(
                Status: Status.Fail,
                Title: $"Engine {version} ({commit}): " + string.Join(", ", mismatched) +
                       " does not match the published checksum",
                Fix: "Delete bin/ and obj/ and rebuild. If it persists the restored package is "
                     + "damaged — clear it from ~/.nuget/packages and restore again."
            );
    }

    /// <summary>One string field out of the engine manifest — small enough not to want a parser.</summary>
    private static string? Field(string json, string name)
    {
        var match = Regex.Match(input: json, pattern: "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The csproj files at the top of the tree and one level down — the layout `create` writes.</summary>
    private static IEnumerable<string> ProjectFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (string project in Directory.EnumerateFiles(directory, "*.csproj")) yield return project;
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child).StartsWith('.')) continue;
            foreach (string project in Directory.EnumerateFiles(child, "*.csproj")) yield return project;
        }
    }

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
        string? version = Probe(file: "zig", arguments: ["version"]);
        return version is null
            ? new Check(
                Status: Status.Info,
                Title: "Zig toolchain: not on PATH (only needed to rebuild the engine by hand; dotnet build drives it otherwise)"
            )
            : new Check(Status: Status.Ok, Title: $"Zig toolchain ({version})");
    }

    /// <summary>
    ///     Rust builds native/zigote-svg (resvg behind a five-function C ABI), which SvgPicture
    ///     P/Invokes. Unlike Zig it is a FAIL rather than an Info: `dotnet build` errors without it,
    ///     because a missing cargo used to be a warning nobody read and the four SvgAssetTests then
    ///     failed on every machine without Rust, looking like broken tests rather than a missing
    ///     toolchain.
    /// </summary>
    private static Check CheckRust()
    {
        string? version = Probe(file: "cargo", arguments: ["--version"])?.Trim();
        if (version is null)
        {
            return new Check(
                Status: Status.Fail,
                Title: "Rust toolchain: not on PATH",
                Fix: "Install it from https://rustup.rs — rustup picks up the pinned toolchain in "
                     + "native/zigote-svg/rust-toolchain.toml automatically."
            );
        }

        string? pinned = PinnedRustVersion();
        if (pinned is not null && !version.Contains(value: pinned, comparisonType: StringComparison.Ordinal))
        {
            return new Check(
                Status: Status.Info,
                Title: $"Rust toolchain ({version}) — pinned is {pinned}",
                Fix: "rustup honours native/zigote-svg/rust-toolchain.toml; a distro-packaged cargo "
                     + "ignores it. Install rustup to build with the pinned compiler."
            );
        }

        return new Check(Status: Status.Ok, Title: $"Rust toolchain ({version})");
    }

    /// <summary>The channel from native/zigote-svg/rust-toolchain.toml, or null.</summary>
    private static string? PinnedRustVersion()
    {
        foreach (string dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(
                    path1: d.FullName,
                    path2: "native",
                    path3: "zigote-svg",
                    path4: "rust-toolchain.toml"
                );
                if (!File.Exists(candidate)) continue;

                foreach (string line in File.ReadAllLines(candidate))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("channel", StringComparison.Ordinal)) continue;

                    int q = t.IndexOf('"');
                    int q2 = q >= 0 ? t.IndexOf(value: '"', startIndex: q + 1) : -1;
                    if (q >= 0 && q2 > q) return t.Substring(startIndex: q + 1, length: q2 - q - 1);
                }
            }
        }

        return null;
    }

    private static List<Check> CheckWorkloads()
    {
        string? list = Probe(file: "dotnet", arguments: ["workload", "list"]);
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
                    Fix: "dotnet workload install android",
                    FixCommand: ["dotnet", "workload", "install", "android"]
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
                        Fix: "dotnet workload install ios",
                        FixCommand: ["dotnet", "workload", "install", "ios"]
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

        return Probe(file: "xcode-select", arguments: ["-p"]) is { } path
            ? new Check(Status: Status.Ok, Title: $"Xcode ({path})")
            : new Check(
                Status: Status.Fail,
                Title: "Xcode: command-line tools not configured",
                Fix: "xcode-select --install"
            );
    }

    /// <summary>
    ///     What every check here asks: a tool's trimmed output, or null when it is missing or said
    ///     nothing useful. Doctor treats "failed" and "absent" the same on purpose — both mean the
    ///     machine cannot do the thing, and the fix line, not the distinction, is what helps.
    /// </summary>
    private static string? Probe(string file, string[] arguments) =>
        Shell.Capture(file: file, arguments: arguments, requireSuccess: true)?.Trim()
            is { Length: > 0 } output
            ? output
            : null;
}
