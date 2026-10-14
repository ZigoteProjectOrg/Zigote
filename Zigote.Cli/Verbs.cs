using CommandLine;

namespace Zigote.Cli;

/// <summary>
///     The declarative command surface: one class per verb, options as attributed properties, and
///     the parser generates help, version and error text from them — the switch statements and
///     hand-rolled usage screen this replaces were three copies of the same information that had
///     already drifted once.
/// </summary>
public abstract class CommonVerb
{
    [Option("dir", MetaValue = "path", HelpText = "Where to work. Default: the current directory.")]
    public string? Dir { get; set; }

    [Option(
        "engine",
        MetaValue = "path",
        HelpText =
            "Build against a Zigote CHECKOUT at this path (ProjectReferences) instead of the " +
            "released packages. For engine development. A scaffold inside a checkout picks " +
            "this up on its own."
    )]
    public string? Engine { get; set; }

    /// <summary>The working directory, absolute. Falls back to where the tool was invoked.</summary>
    public string Directory => Path.GetFullPath(Dir ?? System.IO.Directory.GetCurrentDirectory());

    /// <summary>
    ///     Where the generated projects point their ProjectReferences — or <c>null</c>, meaning
    ///     package references instead.
    ///     <para>
    ///         Null is the normal answer, and that is the whole point of shipping an SDK: an app
    ///         built against a checkout has no version, breaks when it is moved one directory up,
    ///         and makes CI reproduce a layout instead of restoring a dependency. A checkout is
    ///         used only when the person asked for one with <c>--engine</c>, or is standing inside
    ///         one — where working on the engine is the likely reason they are there.
    ///     </para>
    ///     <para>
    ///         Emitted as a RELATIVE path so the generated project survives being cloned by someone
    ///         else; the fallback to <c>ZIGOTE_ROOT</c> matches what the engine's own build honours.
    ///     </para>
    /// </summary>
    public string? ResolveEngine(string projectRoot)
    {
        string? full = Engine is null
            ? FindEnclosingCheckout(projectRoot)
            : FindEngineRoot(start: projectRoot, explicitPath: Engine);
        if (full is null) return null;

        // Relative to the directory the generated csproj sits in — always one level under the
        // project root, for both the app project and a platform head — because the path is
        // consumed as $(MSBuildThisFileDirectory)<this>. Measuring from the root instead is off by
        // exactly one level, which resolves to a plausible-looking path that does not exist.
        return Path
            .GetRelativePath(
                relativeTo: Path.Combine(path1: projectRoot, path2: "head"),
                path: full
            ).Replace(oldChar: '\\', newChar: '/');
    }

    /// <summary>
    ///     A checkout the target directory is INSIDE. Deliberately narrower than
    ///     <see cref="FindEngineRoot" />: a checkout that merely sits next door is someone else's
    ///     source tree, and binding a new app to it is how "it built on my machine" starts. Being
    ///     inside one is different — that is the engine's own repo, and an app scaffolded there is
    ///     almost always a test of the engine.
    /// </summary>
    private static string? FindEnclosingCheckout(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start));
             dir is not null;
             dir = dir.Parent)
        {
            if (IsCheckout(dir.FullName))
                return dir.FullName;
        }

        return null;
    }

    /// <summary>
    ///     Locate a Zigote checkout: the explicit path if given (validated, not trusted), else the
    ///     nearest ancestor or sibling-of-ancestor, else <c>$ZIGOTE_ROOT</c>. Null when none is
    ///     found — <c>doctor</c> reports that as a finding.
    /// </summary>
    public static string? FindEngineRoot(string start, string? explicitPath)
    {
        if (explicitPath is not null)
        {
            string full = Path.GetFullPath(explicitPath);
            if (!IsCheckout(full))
            {
                throw new CliError(
                    $"'{full}' does not look like a Zigote checkout (no Zigote.UI/Zigote.UI.csproj)."
                );
            }

            return full;
        }

        for (var dir = new DirectoryInfo(Path.GetFullPath(start));
             dir is not null;
             dir = dir.Parent)
        {
            if (IsCheckout(dir.FullName)) return dir.FullName;
            // A sibling checkout is as common as an ancestor one: apps usually live next to the
            // engine, not inside it.
            foreach (string sibling in (ReadOnlySpan<string>)["Zigote", "zigote"])
            {
                string candidate = Path.Combine(path1: dir.FullName, path2: sibling);
                if (IsCheckout(candidate)) return candidate;
            }
        }

        string? env = Environment.GetEnvironmentVariable("ZIGOTE_ROOT");
        return env is not null && IsCheckout(Path.GetFullPath(env)) ? Path.GetFullPath(env) : null;
    }

    private static bool IsCheckout(string root) =>
        File.Exists(Path.Combine(path1: root, path2: "Zigote.UI", path3: "Zigote.UI.csproj"));
}

/// <summary>Verbs that write files share the overwrite opt-in and the SDK version they write.</summary>
public abstract class ScaffoldVerb : CommonVerb
{
    [Option("force", HelpText = "Overwrite files that already exist.")]
    public bool Force { get; set; }

    [Option(
        "sdk",
        MetaValue = "version",
        HelpText = "The Zigote version the generated project is built against. " +
                   "Default: this tool's own version, so the two ship together."
    )]
    public string? SdkVersion { get; set; }

    /// <summary>
    ///     The version written into the generated PackageReferences. Validated here rather than
    ///     trusted: it lands in a csproj as XML, and a restore error naming a malformed version is
    ///     a much worse way to learn about a typo.
    /// </summary>
    public string Version => SdkVersion is null ? Sdk.Version : Sdk.ValidateVersion(SdkVersion);
}

[Verb(
    "create",
    HelpText = "Create an app or a platform plugin from the known-good template. " +
               "`create <Name>` alone means an app."
)]
public sealed class CreateVerb : ScaffoldVerb
{
    [Value(
        index: 0,
        MetaName = "template",
        Required = true,
        HelpText = "'app', 'plugin', or directly the project name (which means an app)."
    )]
    public string First { get; set; } = "";

    [Value(
        index: 1,
        MetaName = "name",
        HelpText = "The project name, when a template word was given."
    )]
    public string? Second { get; set; }

    [Option(
        "platforms",
        MetaValue = "list",
        HelpText = "plugin: comma-separated extra targets — android, ios. " +
                   "Desktop (windows/macos/linux) is always included. Default: android,ios."
    )]
    public string? Platforms { get; set; }
}

[Verb("add", HelpText = "Add a platform head (android, ios) to the app in the current directory.")]
public sealed class AddVerb : ScaffoldVerb
{
    [Value(
        index: 0,
        MetaName = "platform",
        Required = true,
        HelpText = "The platform to add: android or ios."
    )]
    public string Platform { get; set; } = "";

    [Option(
        "id",
        MetaValue = "app.id",
        HelpText = "Application id for the head. Default: dev.zigote.<name>."
    )]
    public string? AppId { get; set; }
}

[Verb(
    "run",
    HelpText = "Run the app — on the attached device if there is one, else on this machine. " +
               "Reloads edits on save."
)]
public sealed class RunVerb : CommonVerb
{
    [Option(
        "device",
        MetaValue = "id",
        HelpText = "Where to run: a serial, a model name, or 'desktop'. " +
                   "Default: the only attached device, else this machine."
    )]
    public string? DeviceSpec { get; set; }

    [Option(
        "widget",
        MetaValue = "type",
        HelpText = "Run one widget on its own instead of the app, as Namespace.Type. Desktop only."
    )]
    public string? Widget { get; set; }

    [Option("release", HelpText = "Release configuration. Implies --no-reload.")]
    public bool Release { get; set; }

    [Option(
        "no-reload",
        HelpText =
            "Plain build-and-run. Faster frames (JIT, not the interpreter hot reload needs), " +
            "at the cost of a restart per edit."
    )]
    public bool NoReload { get; set; }

    [Option(
        "inspect",
        HelpText =
            "Start the app with the inspect protocol on, and print the port it announces — " +
            "what the Rider plugin and the MCP server attach to. Desktop only."
    )]
    public bool Inspect { get; set; }

    [Option(
        "debug",
        HelpText = "Device only: start suspended and forward the Mono soft-debugger port, so a " +
                   "debugger can attach before any app code runs."
    )]
    public bool Debug { get; set; }

    [Option(
        "debug-port",
        Default = 10000,
        MetaValue = "port",
        HelpText = "Port for --debug, on both the host and the device."
    )]
    public int DebugPort { get; set; }
}

[Verb(
    "build",
    HelpText = "Build a shippable artifact: apk, aab, ipa, exe, app or appimage."
)]
public sealed class BuildVerb : CommonVerb
{
    [Value(
        index: 0,
        MetaName = "target",
        Required = true,
        HelpText =
            "apk | aab (Android), ipa (iOS, macOS only), exe (Windows), app (macOS), appimage (Linux)."
    )]
    public string Target { get; set; } = "";

    [Option("rid", MetaValue = "rid", HelpText = "Override the runtime identifier for the target.")]
    public string? Rid { get; set; }

    [Option(
        "out",
        MetaValue = "dir",
        HelpText = "Where to put the artifact. Default: artifacts/<rid>/ beside the app."
    )]
    public string? Output { get; set; }

    [Option(
        "icon",
        MetaValue = "file.png",
        HelpText = "The icon master, square. Every launcher density is regenerated from it. " +
                   "Default: icon.png next to the app."
    )]
    public string? Icon { get; set; }

    [Option(
        "aot",
        HelpText = "NativeAOT: one native binary, no .NET install, fast start. This machine's " +
                   "architecture only — the AOT compiler cannot cross-target — and not for apps " +
                   "that load assemblies at runtime."
    )]
    public bool Aot { get; set; }

    [Option(
        "strip-fonts",
        HelpText = "Subset the bundled fonts to Latin + Cyrillic (Iosevka alone drops ~66 MB). " +
                   "Only for an app whose text is all chrome you wrote — user data in another " +
                   "script renders as tofu."
    )]
    public bool StripFonts { get; set; }

    [Option(
        "property",
        MetaValue = "name=value",
        Separator = ';',
        HelpText =
            "Extra MSBuild properties, passed straight through (repeat or separate with ';')."
    )]
    public IEnumerable<string> Properties { get; set; } = [];

    [Option(
        "appimagetool",
        MetaValue = "path",
        HelpText = "appimage: the packaging tool. Default: $ZIGOTE_APPIMAGETOOL, else PATH."
    )]
    public string? AppImageTool { get; set; }

    [Option(
        "debug",
        HelpText =
            "Debug configuration, and no signing. For a build you install yourself, not one you ship."
    )]
    public bool Debug { get; set; }

    [Option(
        "keystore",
        MetaValue = "path",
        HelpText = "Android: the keystore to sign a release with."
    )]
    public string? Keystore { get; set; }

    [Option(
        "keystore-pass",
        MetaValue = "password",
        HelpText =
            "Android: the keystore password. $ZIGOTE_KEYSTORE_PASSWORD is read when this is absent, " +
            "which keeps it out of your shell history."
    )]
    public string? KeystorePassword { get; set; }

    [Option("key-alias", MetaValue = "name", HelpText = "Android: the key inside the keystore.")]
    public string? KeyAlias { get; set; }

    [Option(
        "key-pass",
        MetaValue = "password",
        HelpText = "Android: the key's password, if it differs from the store's."
    )]
    public string? KeyPassword { get; set; }

    [Option("team", MetaValue = "id", HelpText = "iOS: the Apple developer team id to sign with.")]
    public string? Team { get; set; }
}

[Verb(
    "test",
    HelpText = "Run the app's tests, and turn any golden-image failures into a readable diff."
)]
public sealed class TestVerb : CommonVerb
{
    [Option(
        "update-goldens",
        HelpText =
            "Accept the rendering as it is now: the harness overwrites the expected images " +
            "instead of failing (ZIGOTE_UPDATE_GOLDENS=1)."
    )]
    public bool UpdateGoldens { get; set; }

    [Option(
        "filter",
        MetaValue = "expression",
        HelpText = "Passed through to `dotnet test --filter`."
    )]
    public string? Filter { get; set; }
}

[Verb("logs", HelpText = "Follow the running app's log output from the attached device.")]
public sealed class LogsVerb : CommonVerb
{
    [Option("device", MetaValue = "id", HelpText = "Which device, when more than one is attached.")]
    public string? DeviceSpec { get; set; }
}

[Verb("preview", HelpText = "Run one widget of the app on its own, reloading on save.")]
public sealed class PreviewVerb : CommonVerb
{
    [Value(index: 0, MetaName = "type", HelpText = "The widget to preview, as Namespace.Type.")]
    public string? Target { get; set; }

    [Option("list", HelpText = "Print the previewable widgets and exit.")]
    public bool ListTargets { get; set; }

    [Option("no-watch", HelpText = "Plain `dotnet run`, giving up reload-on-save.")]
    public bool NoWatch { get; set; }
}

[Verb(
    "device",
    HelpText = "List attached Android devices and emulators. " +
               "`device run` and `device logs` are aliases for `run` and `logs`."
)]
public sealed class DeviceVerb : CommonVerb
{
    [Value(
        index: 0,
        MetaName = "action",
        HelpText =
            "list (default) — attached devices; run — deploy and hot-reload; logs — this app's logcat."
    )]
    public string Action { get; set; } = "list";

    [Option("serial", MetaValue = "id", HelpText = "Which device, when more than one is attached.")]
    public string? Serial { get; set; }

    [Option("json", HelpText = "list: one JSON object per device, for editors and scripts.")]
    public bool Json { get; set; }

    [Option(
        "no-reload",
        HelpText =
            "Plain deploy-and-run. Faster frames (JIT, not the interpreter hot reload needs), " +
            "at the cost of a redeploy per edit."
    )]
    public bool NoReload { get; set; }

    [Option("release", HelpText = "Release configuration. Implies --no-reload.")]
    public bool Release { get; set; }

    [Option(
        "debug",
        HelpText =
            "Start the app suspended and forward the Mono soft-debugger port, so a debugger " +
            "can attach before any app code runs."
    )]
    public bool Debug { get; set; }

    [Option(
        "debug-port",
        Default = 10000,
        MetaValue = "port",
        HelpText = "Port for --debug, on both the host and the device."
    )]
    public int DebugPort { get; set; }
}

[Verb(
    "doctor",
    HelpText =
        "Check this machine for everything Zigote development needs, and say how to fix what is missing."
)]
public sealed class DoctorVerb : CommonVerb
{
    [Option(
        "fix",
        HelpText =
            "Run the fixes this tool knows how to run (SDK workloads), after showing them. " +
            "Never runs anything without this flag, and never without asking."
    )]
    public bool Fix { get; set; }

    [Option("json", HelpText = "One JSON object per finding, for editors and scripts.")]
    public bool Json { get; set; }
}
