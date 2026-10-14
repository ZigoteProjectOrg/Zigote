using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Zigote.Cli;

/// <summary>
///     <c>zigote build</c> — the artifact you hand to a store or a user.
///     <para>
///         What this replaces is a `dotnet publish` line with the right RID, the right
///         configuration, the right package format and the right signing properties, and no help if
///         one of them is wrong. Everything below is one of those, made a default or a named error.
///     </para>
///     <para>
///         Three things it does that the SDK will not. <b>One version</b>: the app csproj's
///         <c>&lt;Version&gt;</c> drives the Android versionName AND versionCode, so the two files
///         that disagree on release day cannot. <b>Icons</b>: every launcher density is regenerated
///         from one <c>icon.png</c> — the job people install two packages for in the framework this
///         is measured against. <b>Signing that says what is missing</b>: an unsigned release names
///         the absent property and the `keytool` line that creates the keystore.
///     </para>
/// </summary>
public static class Build
{
    /// <summary>Android launcher densities: the mipmap folder and the pixel size that belongs in it.</summary>
    private static readonly (string Folder, int Size)[] Densities = [
        ("mipmap-mdpi", 48), ("mipmap-hdpi", 72), ("mipmap-xhdpi", 96),
        ("mipmap-xxhdpi", 144), ("mipmap-xxxhdpi", 192),
    ];

    public static int Run(BuildVerb options)
    {
        string target = options.Target.ToLowerInvariant();
        return target switch {
            "apk" or "aab" => Android(options: options, format: target),
            "exe" or "app" or "appimage" => Desktop(options: options, kind: target),
            "ipa" => Ios(options),
            _ => throw new CliError(
                $"unknown target '{options.Target}'. Use apk, aab, ipa, exe, app or appimage."
            ),
        };
    }

    // ── android ───────────────────────────────────────────────────────────────

    private static int Android(BuildVerb options, string format)
    {
        string head = Projects.RequireHead(
            root: options.Directory,
            suffix: ".Android",
            create: "`zigote add android` writes one."
        );
        string version = AppVersion(options.Directory);
        string rid = options.Rid ?? "android-arm64";
        GenerateAndroidIcons(
            head: head,
            source: IconSource(options),
            name: Path.GetFileNameWithoutExtension(head).Replace(oldValue: ".Android", newValue: "")
        );

        var arguments = new List<string> {
            "publish",
            head,
            "--configuration",
            options.Debug ? "Debug" : "Release",
            $"--property:ZigTargetRid={rid}",
            $"--property:AndroidPackageFormat={format}",
            // One version, three consumers. The code has to be an increasing integer that Play
            // compares numerically, so it is derived rather than typed: 1.4.2 → 10402.
            $"--property:ApplicationDisplayVersion={version}",
            $"--property:ApplicationVersion={VersionCode(version)}",
        };

        if (!options.Debug)
        {
            var signing = AndroidSigning(options);
            if (signing.Count == 0)
            {
                throw new CliError(
                    "a release apk has to be signed, and no keystore was given.\n" +
                    "  Create one:  keytool -genkeypair -v -keystore release.keystore -alias upload \\\n" +
                    "                 -keyalg RSA -keysize 2048 -validity 10000\n" +
                    "  Then:        zigote build " + format +
                    " --keystore release.keystore --keystore-pass <pw> --key-alias upload\n" +
                    "  Or build an unsigned debug artifact with --debug."
                );
            }

            arguments.AddRange(signing);
        }

        if (options.Output is { } output)
        {
            arguments.Add("--output");
            arguments.Add(Path.GetFullPath(output));
        }

        Console.WriteLine(
            $"  {Path.GetFileNameWithoutExtension(head)}  {version}  →  {format} ({rid})"
        );
        return Shell.Dotnet(arguments);
    }

    /// <summary>
    ///     The keystore properties, or nothing. Named individually rather than passed through as a
    ///     blob because the Android SDK's own failure for a missing one is a stack trace from
    ///     apksigner.
    /// </summary>
    private static List<string> AndroidSigning(BuildVerb options)
    {
        if (options.Keystore is null) return [];
        string keystore = Path.GetFullPath(options.Keystore);
        if (!File.Exists(keystore)) throw new CliError($"no keystore at '{keystore}'.");
        string password = options.KeystorePassword
                          ?? Environment.GetEnvironmentVariable("ZIGOTE_KEYSTORE_PASSWORD")
                          ?? throw new CliError(
                              "--keystore needs --keystore-pass (or $ZIGOTE_KEYSTORE_PASSWORD)."
                          );
        string alias = options.KeyAlias
                       ?? throw new CliError("--keystore needs --key-alias — the key inside it.");
        return [
            "--property:AndroidKeyStore=true",
            $"--property:AndroidSigningKeyStore={keystore}",
            $"--property:AndroidSigningStorePass={password}",
            $"--property:AndroidSigningKeyAlias={alias}",
            // The key's own password defaults to the store's, which is what keytool creates unless
            // asked otherwise — and getting this wrong fails at the very last step of a long build.
            $"--property:AndroidSigningKeyPass={options.KeyPassword ?? password}",
        ];
    }

    /// <summary>
    ///     Every launcher density from one square PNG. This is
    ///     <c>flutter_launcher_icons</c> without the package: the sizes are fixed by Android, the
    ///     input is one file, and doing it by hand means five exports that drift apart.
    /// </summary>
    private static void GenerateAndroidIcons(string head, string? source, string name)
    {
        var icon = LoadIcon(source: source, name: name);

        string resources = Path.Combine(path1: Path.GetDirectoryName(head)!, path2: "Resources");
        foreach ((string folder, int size) in Densities)
            icon.Resized(size).Save(
                Path.Combine(path1: resources, path2: folder, path3: "ic_launcher.png")
            );

        DeclareIconInManifest(head);
        Console.WriteLine(
            $"  icons: {Densities.Length} densities from " +
            (source is null ? "the generated placeholder" : Path.GetFileName(source))
        );
    }

    /// <summary>
    ///     Point the manifest at the icons that now exist.
    ///     <para>
    ///         Written here rather than by `add android`, and this is the reason: a manifest that
    ///         names <c>@mipmap/ic_launcher</c> before any icon has been generated fails the build
    ///         with an unresolved resource. So the attribute appears exactly when the resource does.
    ///     </para>
    /// </summary>
    private static void DeclareIconInManifest(string head)
    {
        string manifest = Path.Combine(
            path1: Path.GetDirectoryName(head)!,
            path2: "Properties",
            path3: "AndroidManifest.xml"
        );
        if (!File.Exists(manifest)) return;
        string xml = File.ReadAllText(manifest);
        if (xml.Contains(value: "android:icon=", comparisonType: StringComparison.Ordinal)) return;
        File.WriteAllText(
            path: manifest,
            contents: xml.Replace(
                oldValue: "<application ",
                newValue: "<application android:icon=\"@mipmap/ic_launcher\"\n                 ",
                comparisonType: StringComparison.Ordinal
            )
        );
    }

    /// <summary>
    ///     The icon to rasterise: the project's own, or the generated placeholder. Never nothing —
    ///     an app with no icon is an app the launcher draws a generic sheet for, which reads as a
    ///     broken install rather than a project that has not chosen art yet.
    /// </summary>
    private static Png LoadIcon(string? source, string name)
    {
        if (source is null) return Png.DefaultIcon(name);
        var icon = Png.Load(source);
        return icon.Width == icon.Height
            ? icon
            : throw new CliError(
                $"'{Path.GetFileName(source)}' is {icon.Width}×{icon.Height}. A launcher icon is "
                + "square — 1024×1024 is the usual master."
            );
    }

    /// <summary>
    ///     The icon master: <c>--icon</c>, else the project's own <c>icon.png</c>. One source image
    ///     for every platform and every density, because five hand-exported sizes drift apart and
    ///     the drift shows up on a launcher screen, not in a diff.
    /// </summary>
    private static string? IconSource(BuildVerb options)
    {
        if (options.Icon is { } explicitPath)
        {
            string full = Path.GetFullPath(explicitPath);
            return File.Exists(full) ? full : throw new CliError($"no icon at '{full}'.");
        }

        return new[] {
                Path.Combine(path1: options.Directory, path2: "icon.png"),
                Path.Combine(path1: options.Directory, path2: "Assets", path3: "icon.png"),
            }
            .FirstOrDefault(File.Exists);
    }

    // ── desktop ───────────────────────────────────────────────────────────────

    private static int Desktop(BuildVerb options, string kind)
    {
        string project = Projects.App(options.Directory);
        string version = AppVersion(options.Directory);
        string rid = options.Rid ?? DefaultRid(kind);
        var arguments = new List<string> {
            "publish",
            project,
            "--configuration",
            options.Debug ? "Debug" : "Release",
            "--runtime",
            rid,
            $"--property:ZigTargetRid={rid}",
            // Self-contained: a user double-clicking this must not need a .NET install, which is the
            // difference between "an app" and "a thing that works on the machine that built it".
            "--self-contained",
            "true",
            $"--property:Version={version}",
        };
        arguments.AddRange(AotArguments(options: options, rid: rid));
        arguments.AddRange(SizeArguments(options));

        // The publish output has to be somewhere known to wrap it, and "somewhere known" is worth
        // more than the SDK's default path even when nothing is wrapped.
        string output = Path.GetFullPath(
            options.Output ?? Path.Combine(path1: options.Directory, path2: "artifacts", path3: rid)
        );
        arguments.Add("--output");
        arguments.Add(output);

        Console.WriteLine($"  {Path.GetFileNameWithoutExtension(project)}  {version}  →  {rid}");
        int exit = Shell.Dotnet(arguments);
        if (exit != 0) return exit;

        Console.WriteLine($"  published: {output}  ({Size(DirectorySize(output))})");
        return kind == "appimage"
            ? AppImage(
                options: options,
                publishDirectory: output,
                project: project,
                name: Path.GetFileNameWithoutExtension(project),
                version: version,
                rid: rid
            )
            : 0;
    }

    // ── appimage ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Wrap a published Linux app as an AppImage: one file a user downloads, marks executable
    ///     and runs, on any distro.
    ///     <para>
    ///         An AppImage is an AppDir — a tiny filesystem with the app under <c>usr/</c>, an
    ///         <c>AppRun</c> entry point, a <c>.desktop</c> file and an icon at the root — squashed
    ///         and appended to a runtime binary. Every one of those four pieces is mandatory and
    ///         three of them fail late: appimagetool refuses a missing .desktop, a desktop file whose
    ///         <c>Icon=</c> names no file produces an AppImage with a blank launcher entry, and a
    ///         missing AppRun produces one that does nothing when clicked. So this writes all four,
    ///         and the icon comes from the same <c>icon.png</c> the Android densities come from.
    ///     </para>
    /// </summary>
    private static int AppImage(
        BuildVerb options,
        string publishDirectory,
        string project,
        string name,
        string version,
        string rid
    )
    {
        if (!OperatingSystem.IsLinux())
            throw new CliError("an AppImage is a Linux artifact — build it on Linux.");

        string appDir = Path.Combine(
            path1: Path.GetDirectoryName(publishDirectory)!,
            path2: $"{name}.AppDir"
        );
        if (Directory.Exists(appDir)) Directory.Delete(path: appDir, recursive: true);
        string binaries = Path.Combine(path1: appDir, path2: "usr", path3: "bin");
        CopyTree(source: publishDirectory, target: binaries);

        string executableName = AppExecutable(publishDirectory: binaries, project: project);

        // Exec through a script rather than symlinking the binary: the app resolves its Fonts/ and
        // Assets/ next to the executable, so the working directory has to be usr/bin, not wherever
        // the user happened to double-click from.
        string appRun = Path.Combine(path1: appDir, path2: "AppRun");
        File.WriteAllText(
            path: appRun,
            contents: $"""
                       #!/bin/sh
                       # AppImage entry point. HERE is the mounted AppDir, which only exists while the
                       # image is running — every path has to be derived from it at run time.
                       HERE="$(dirname "$(readlink -f "$0")")"
                       cd "$HERE/usr/bin" || exit 1
                       exec "./{executableName}" "$@"

                       """.ReplaceLineEndings("\n")
        );
        File.SetUnixFileMode(
            path: appRun,
            mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                  | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                  | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
        );

        string desktopId = name.ToLowerInvariant();
        File.WriteAllText(
            path: Path.Combine(path1: appDir, path2: $"{desktopId}.desktop"),
            contents: $"""
                       [Desktop Entry]
                       Type=Application
                       Name={name}
                       Exec={executableName}
                       Icon={desktopId}
                       Categories=Development;
                       X-AppImage-Version={version}
                       Terminal=false

                       """.ReplaceLineEndings("\n")
        );

        string? iconPath = IconSource(options);
        var icon = LoadIcon(source: iconPath, name: name);
        // 256 at the AppDir root is what appimagetool reads; the hicolor copy is what a desktop
        // environment finds after the user installs the image.
        icon.Resized(256).Save(Path.Combine(path1: appDir, path2: $"{desktopId}.png"));
        icon.Resized(256).Save(
            Path.Combine(
                path1: appDir,
                path2: "usr/share/icons/hicolor/256x256/apps",
                path3: $"{desktopId}.png"
            )
        );
        Console.WriteLine(
            iconPath is null
                ? "  icon: generated placeholder (put an icon.png next to the project, or pass --icon)"
                : $"  icon: {Path.GetFileName(iconPath)} → 256×256"
        );

        string tool = AppImageTool(options)
                      ?? throw new CliError(
                          $"AppDir is ready at {appDir}, but appimagetool is not installed.\n" +
                          "  Get it:  wget https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage\n" +
                          "           chmod +x appimagetool-x86_64.AppImage\n" +
                          "  Then:    zigote build appimage --appimagetool ./appimagetool-x86_64.AppImage\n" +
                          "  (or set $ZIGOTE_APPIMAGETOOL, or put it on PATH)"
                      );

        string image = Path.Combine(
            path1: Path.GetDirectoryName(publishDirectory)!,
            path2: $"{name}-{version}-{AppImageArch(rid)}.AppImage"
        );
        var pack = Shell.Command(file: tool, arguments: []);
        // appimagetool is itself an AppImage, and mounting one needs FUSE — absent in most
        // containers and on plenty of hosts. Extract-and-run costs a temp directory and works
        // everywhere, which is the better default for a build tool.
        pack.ArgumentList.Add("--appimage-extract-and-run");
        // zstd over the default gzip: smaller image AND faster to mount, which for an AppImage is
        // startup time on every launch, not just download size.
        pack.ArgumentList.Add("--comp");
        pack.ArgumentList.Add("zstd");
        pack.ArgumentList.Add(appDir);
        pack.ArgumentList.Add(image);
        // Reproducible-ish: without this appimagetool tries to sign and embeds an update line from
        // whatever git remote it finds, which is not this app's release channel.
        pack.Environment["ARCH"] = AppImageArch(rid);
        int packed = Shell.Wait(pack);
        if (packed != 0) return packed;

        Console.WriteLine();
        Console.WriteLine($"  {image}  ({Size(new FileInfo(image).Length)})");
        Console.WriteLine($"  AppDir was {Size(DirectorySize(appDir))} before compression.");
        return 0;
    }

    /// <summary>Bytes as something a person reads — a release note says 62 MB, not 65010112.</summary>
    internal static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
        : $"{bytes / 1024.0:0.0} KB";

    private static long DirectorySize(string directory) =>
        Directory
            .EnumerateFiles(
                path: directory,
                searchPattern: "*",
                searchOption: SearchOption.AllDirectories
            )
            .Sum(f => new FileInfo(f).Length);

    /// <summary>
    ///     Which file in the publish output is the app.
    ///     <para>
    ///         Not "the first extension-less executable": a self-contained publish also contains
    ///         <c>createdump</c>, which is the runtime's crash-dump helper and sorts before almost
    ///         any app name. Picking it produces an AppImage that builds, packages, installs and
    ///         then does nothing at all when clicked — the exact failure this whole verb exists to
    ///         prevent. So the name is derived from the app itself: the assembly's own
    ///         <c>.runtimeconfig.json</c> (JIT) or the assembly name (AOT, which has neither).
    ///     </para>
    /// </summary>
    private static string AppExecutable(string publishDirectory, string project)
    {
        var candidates = new List<string>();
        // A JIT publish names this after the assembly, and there is exactly one.
        string? runtimeConfig = Directory
            .EnumerateFiles(path: publishDirectory, searchPattern: "*.runtimeconfig.json")
            .FirstOrDefault();
        if (runtimeConfig is not null)
        {
            candidates.Add(
                Path.GetFileName(runtimeConfig)
                    .Replace(
                        oldValue: ".runtimeconfig.json",
                        newValue: "",
                        comparisonType: StringComparison.Ordinal
                    )
            );
        }

        // AOT has no runtimeconfig — fall back to what the project says it is called.
        var assemblyName = Regex.Match(
            input: File.ReadAllText(project),
            pattern: @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>"
        );
        candidates.Add(
            assemblyName.Success
                ? assemblyName.Groups[1].Value
                : Path.GetFileNameWithoutExtension(project)
        );

        foreach (string candidate in candidates)
        {
            string path = Path.Combine(path1: publishDirectory, path2: candidate);
            if (File.Exists(path) && IsExecutable(path)) return candidate;
        }

        throw new CliError(
            $"no app executable in {publishDirectory} — looked for " +
            string.Join(separator: ", ", values: candidates) + "."
        );
    }

    /// <summary>
    ///     AppImage's spelling of an architecture. Not the RID's: appimagetool names the runtime it
    ///     embeds from $ARCH and knows only the uname words.
    /// </summary>
    private static string AppImageArch(string rid) =>
        rid.EndsWith(value: "-arm64", comparisonType: StringComparison.Ordinal)
            ? "aarch64"
            : "x86_64";

    /// <summary>appimagetool from --appimagetool, $ZIGOTE_APPIMAGETOOL, or PATH.</summary>
    private static string? AppImageTool(BuildVerb options)
    {
        foreach (string candidate in (ReadOnlySpan<string>) [
                     options.AppImageTool ?? "",
                     Environment.GetEnvironmentVariable("ZIGOTE_APPIMAGETOOL") ?? "",
                 ])
        {
            if (candidate.Length > 0 && File.Exists(Path.GetFullPath(candidate)))
                return Path.GetFullPath(candidate);
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                     Path.PathSeparator
                 ))
        foreach (string exe in (ReadOnlySpan<string>)
                 ["appimagetool", "appimagetool-x86_64.AppImage"])
        {
            if (directory.Length == 0) continue;
            string candidate = Path.Combine(path1: directory, path2: exe);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    // The guard is not ceremony for the analyzer: there is no execute bit on Windows, so asking
    // is meaningless there rather than merely unsupported. AppImages are a Linux artifact anyway.
    private static bool IsExecutable(string path) =>
        OperatingSystem.IsLinux() && File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute);

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(
                     path: source,
                     searchPattern: "*",
                     searchOption: SearchOption.AllDirectories
                 ))
            Directory.CreateDirectory(
                directory.Replace(
                    oldValue: source,
                    newValue: target,
                    comparisonType: StringComparison.Ordinal
                )
            );
        foreach (string file in Directory.EnumerateFiles(
                     path: source,
                     searchPattern: "*",
                     searchOption: SearchOption.AllDirectories
                 ))
        {
            string destination = file.Replace(
                oldValue: source,
                newValue: target,
                comparisonType: StringComparison.Ordinal
            );
            File.Copy(sourceFileName: file, destFileName: destination, overwrite: true);
            // Copy keeps content, not the executable bit — and the app binary without it is an
            // AppImage that unpacks correctly and refuses to start.
            if (!OperatingSystem.IsLinux()) continue;
            var mode = File.GetUnixFileMode(file);
            if (mode.HasFlag(UnixFileMode.UserExecute))
                File.SetUnixFileMode(path: destination, mode: mode);
        }
    }

    /// <summary>
    ///     Size, which for a desktop artifact is dominated by three things: debug symbols, the
    ///     bundled fonts, and the compression of whatever wraps it.
    ///     <para>
    ///         Two of the three are safe to do by default. Symbols do not belong in a release
    ///         download — they are half the managed output and the crash reports that need them are
    ///         served by the published symbol packages instead. Asset shaking drops content the code
    ///         cannot reach, which is what it is for.
    ///     </para>
    ///     <para>
    ///         The third is not safe and stays opt-in. <c>--strip-fonts</c> subsets the bundled text
    ///         faces (Iosevka's six-face .ttc alone is ~66 MB), and the subset covers Latin and
    ///         Cyrillic — so an app that displays user data, filenames or tags in any other script
    ///         renders tofu, and no amount of testing in English reveals it. Shipping the megabytes
    ///         is by far the cheaper mistake, so the flag has to be typed.
    ///     </para>
    /// </summary>
    private static List<string> SizeArguments(BuildVerb options)
    {
        var arguments = new List<string>();
        if (!options.Debug)
        {
            arguments.Add("--property:DebugType=none");
            arguments.Add("--property:DebugSymbols=false");
            arguments.Add("--property:EnableAssetShaking=true");
            // DebugType covers this app's own symbols; the XML doc files and .pdb files that come
            // along with every REFERENCED assembly are a separate copy step, and they are pure
            // download weight for a user who is not writing code against those APIs.
            arguments.Add("--property:AllowedReferenceRelatedFileExtensions=none");
        }

        if (options.StripFonts) arguments.Add("--property:EnableFontStripping=true");
        // The escape hatch. This verb is a set of good defaults over `dotnet publish`, not a wall
        // in front of it: a project with its own opinion (or a one-off experiment) needs a way
        // through that is not "give up and write the publish line by hand".
        foreach (string property in options.Properties)
        {
            arguments.Add(
                property.StartsWith(value: "--property:", comparisonType: StringComparison.Ordinal)
                    ? property
                    : $"--property:{property}"
            );
        }

        // Trimming is deliberately absent: the UI stack reflects over widget and theme members
        // (the inspector, the titlebar hit-test), and a trimmed build fails at runtime, in one
        // widget, on someone else's machine. NativeAOT's own trimming is a different matter — it
        // runs with the rooting build/Zigote.Aot.targets sets up.
        return arguments;
    }

    /// <summary>
    ///     NativeAOT, and the one rule that makes it usable: it is HOST-ONLY. ilc has no cross-OS or
    ///     cross-architecture codegen, so an AOT publish for a RID this machine is not can only fail
    ///     — late, after a long compile, with a linker error that names an object file. Refusing it
    ///     here costs a sentence instead.
    ///     <para>
    ///         AOT is also not for every app: an app that loads assemblies at runtime (the editor
    ///         compiles user scripts into a collectible load context) has a hard JIT dependency, and
    ///         there is no way for this tool to know that from outside — hence the note in the
    ///         error, and the property being an opt-in the project can refuse.
    ///     </para>
    /// </summary>
    private static List<string> AotArguments(BuildVerb options, string rid)
    {
        if (!options.Aot) return [];
        string host = HostRid();
        if (rid != host)
        {
            throw new CliError(
                $"--aot builds for this machine only: it can produce {host}, not {rid}. "
                + "NativeAOT's compiler has no cross-target codegen — publish each RID on its own OS "
                + "(or drop --aot for a portable JIT build)."
            );
        }

        // ONE property, and not PublishAot. A global property reaches every project in the graph,
        // and PublishAot on a netstandard2.0 source generator is NETSDK1207 — the build fails in a
        // project that has nothing to do with the app. ZigoteAot is the gate the engine's own
        // build/Zigote.Aot.targets reads (and that Zigote.UI ships to package-based apps): it turns
        // on PublishAot *inside the app project only*, together with the rooting the widget
        // inspector and the titlebar hit-test need to survive trimming.
        return ["--property:ZigoteAot=true"];
    }

    /// <summary>The RID of the machine running this — the only one NativeAOT can target.</summary>
    internal static string HostRid()
    {
        string architecture =
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                var other => other.ToString().ToLowerInvariant(),
            };
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";
        return $"{os}-{architecture}";
    }

    /// <summary>
    ///     The RID for a target when none was named. Each keeps the HOST's architecture: an arm64
    ///     Mac building a .app wants osx-arm64, an arm64 Linux box building an AppImage wants
    ///     linux-arm64, and defaulting either to x64 produces an artifact that runs under emulation
    ///     at best. Only the OS half is fixed by the target itself.
    /// </summary>
    private static string DefaultRid(string kind)
    {
        string architecture = HostRid().Split('-')[1];
        return kind switch {
            "exe" => $"win-{architecture}",
            "app" => $"osx-{architecture}",
            _ => $"linux-{architecture}",
        };
    }

    // ── ios ───────────────────────────────────────────────────────────────────

    private static int Ios(BuildVerb options)
    {
        if (!OperatingSystem.IsMacOS())
            throw new CliError("an ipa can only be built on macOS with Xcode installed.");

        string head = Projects.RequireHead(
            root: options.Directory,
            suffix: ".iOS",
            create: "`zigote add ios` writes one."
        );
        string version = AppVersion(options.Directory);
        var arguments = new List<string> {
            "publish",
            head,
            "--configuration",
            options.Debug ? "Debug" : "Release",
            "--runtime",
            options.Rid ?? "ios-arm64",
            $"--property:ZigTargetRid={options.Rid ?? "ios-arm64"}",
            $"--property:ApplicationDisplayVersion={version}",
            $"--property:ApplicationVersion={VersionCode(version)}",
            "--property:BuildIpa=true",
        };
        // Apple signs at build time, not after: without a team the archive step fails deep inside
        // codesign, naming a provisioning profile rather than the thing that is missing.
        if (!options.Debug)
        {
            string? team = options.Team ??
                           Environment.GetEnvironmentVariable("ZIGOTE_APPLE_TEAM_ID");
            if (team is null)
            {
                throw new CliError(
                    "a release ipa needs an Apple team: --team <TEAMID> (or $ZIGOTE_APPLE_TEAM_ID). "
                    + "It is the ten-character id on developer.apple.com under Membership."
                );
            }

            arguments.Add($"--property:CodesignTeamIdentifier={team}");
        }

        Console.WriteLine($"  {Path.GetFileNameWithoutExtension(head)}  {version}  →  ipa");
        return Shell.Dotnet(arguments);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     The app's version, from the shared project's <c>&lt;Version&gt;</c>. One number, so the
    ///     manifest, the plist and the desktop metadata cannot disagree — which is a release-day bug
    ///     in every framework this competes with.
    /// </summary>
    internal static string AppVersion(string root)
    {
        string project = Projects.App(root);
        var match = Regex.Match(
            input: File.ReadAllText(project),
            pattern: @"<Version>\s*([^<\s]+)\s*</Version>"
        );
        return match.Success ? match.Groups[1].Value : "1.0.0";
    }

    /// <summary>
    ///     A monotonic integer for a dotted version: 1.4.2 → 10402. Play compares these numerically
    ///     and refuses an upload that does not increase, so it has to be derived from the one string
    ///     rather than typed twice.
    /// </summary>
    internal static int VersionCode(string version)
    {
        int[] parts = version
            .Split('-')[0]
            .Split('.')
            .Select(p => int.TryParse(s: p, result: out int value) ? value : 0)
            .ToArray();
        int major = parts.Length > 0 ? parts[0] : 0;
        int minor = parts.Length > 1 ? parts[1] : 0;
        int patch = parts.Length > 2 ? parts[2] : 0;
        if (minor > 99 || patch > 99)
        {
            throw new CliError(
                $"version '{version}' has a component over 99, which cannot be packed into an "
                + "Android versionCode. Set <ApplicationVersion> in the head yourself."
            );
        }

        return (major * 10_000) + (minor * 100) + patch;
    }
}
