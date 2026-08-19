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
        HelpText = "Path to the Zigote checkout the generated projects reference. " +
                   "Default: found by walking up from --dir, else $ZIGOTE_ROOT."
    )]
    public string? Engine { get; set; }

    /// <summary>The working directory, absolute. Falls back to where the tool was invoked.</summary>
    public string Directory => Path.GetFullPath(Dir ?? System.IO.Directory.GetCurrentDirectory());

    /// <summary>
    ///     Where the generated projects point their ProjectReferences.
    ///     <para>
    ///         Walking up from the target beats asking, because the overwhelmingly common case is a
    ///         checkout sitting beside or above the new app — and a wrong engine path only shows up
    ///         much later, as an unresolvable reference. Emitted as a RELATIVE path so the generated
    ///         project survives being moved or cloned by someone else; the fallback to
    ///         <c>ZIGOTE_ROOT</c> matches what the engine's own build honours.
    ///     </para>
    /// </summary>
    public string ResolveEngine(string projectRoot)
    {
        string full = FindEngineRoot(start: projectRoot, explicitPath: Engine)
                      ?? throw new CliError(
                          "cannot find a Zigote checkout. Pass --engine <path> or set ZIGOTE_ROOT."
                      );

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
    ///     Locate a Zigote checkout: the explicit path if given (validated, not trusted), else the
    ///     nearest ancestor or sibling-of-ancestor, else <c>$ZIGOTE_ROOT</c>. Null when none is
    ///     found — <c>doctor</c> reports that as a finding where <see cref="ResolveEngine" />
    ///     treats it as an error.
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

        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            if (IsCheckout(dir.FullName)) return dir.FullName;
            // A sibling checkout is as common as an ancestor one: apps usually live next to the
            // engine, not inside it.
            foreach (string sibling in (ReadOnlySpan<string>) ["Zigote", "zigote"])
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

/// <summary>Verbs that write files share the overwrite opt-in.</summary>
public abstract class ScaffoldVerb : CommonVerb
{
    [Option("force", HelpText = "Overwrite files that already exist.")]
    public bool Force { get; set; }
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

    [Value(index: 1, MetaName = "name", HelpText = "The project name, when a template word was given.")]
    public string? Second { get; set; }

    [Option(
        "platforms",
        MetaValue = "list",
        HelpText = "plugin: comma-separated extra targets — android, ios. " +
                   "Desktop (windows/macos/linux) is always included. Default: android,ios."
    )]
    public string? Platforms { get; set; }
}

[Verb("add", HelpText = "Add a platform head to the app in the current directory.")]
public sealed class AddVerb : ScaffoldVerb
{
    [Value(
        index: 0,
        MetaName = "platform",
        Required = true,
        HelpText = "The platform to add. Only 'android' exists today."
    )]
    public string Platform { get; set; } = "";

    [Option(
        "id",
        MetaValue = "app.id",
        HelpText = "Application id for the head. Default: dev.zigote.<name>."
    )]
    public string? AppId { get; set; }
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
    "doctor",
    HelpText = "Check this machine for everything Zigote development needs, and say how to fix what is missing."
)]
public sealed class DoctorVerb : CommonVerb;
