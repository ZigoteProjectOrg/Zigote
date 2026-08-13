namespace Zigote.Cli;

/// <summary>Flags shared by every command, parsed once.</summary>
public sealed class Options
{
    public string Directory { get; private init; } = ".";
    public string? Engine { get; private init; }
    public string? AppId { get; private init; }
    public bool Force { get; private init; }

    /// <summary>preview: print the previewable widgets instead of showing one.</summary>
    public bool ListTargets { get; private init; }

    /// <summary>preview: plain <c>dotnet run</c>, giving up reload-on-save.</summary>
    public bool NoWatch { get; private init; }

    public static Options Parse(string[] args, out List<string> positional)
    {
        positional = [];
        string? dir = null, engine = null, id = null;
        bool force = false;
        bool list = false;
        bool noWatch = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir":
                    dir = Next(args: args, i: ref i, flag: "--dir");
                    break;
                case "--engine":
                    engine = Next(args: args, i: ref i, flag: "--engine");
                    break;
                case "--id":
                    id = Next(args: args, i: ref i, flag: "--id");
                    break;
                case "--force":
                    force = true;
                    break;
                case "--list":
                    list = true;
                    break;
                case "--no-watch":
                    noWatch = true;
                    break;
                default:
                    if (args[i].StartsWith('-')) throw new CliError($"unknown option '{args[i]}'");
                    positional.Add(args[i]);
                    break;
            }
        }

        return new Options {
            Directory = Path.GetFullPath(dir ?? System.IO.Directory.GetCurrentDirectory()),
            Engine = engine,
            AppId = id,
            Force = force,
            ListTargets = list,
            NoWatch = noWatch,
        };
    }

    private static string Next(string[] args, ref int i, string flag)
    {
        if (++i >= args.Length) throw new CliError($"{flag} needs a value");
        return args[i];
    }

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
        string found = Engine
                       ?? SearchUpwards(projectRoot)
                       ?? Environment.GetEnvironmentVariable("ZIGOTE_ROOT")
                       ?? throw new CliError(
                           "cannot find a Zigote checkout. Pass --engine <path> or set ZIGOTE_ROOT."
                       );

        string full = Path.GetFullPath(found);
        if (!File.Exists(Path.Combine(path1: full, path2: "Zigote.UI", path3: "Zigote.UI.csproj")))
        {
            throw new CliError(
                $"'{full}' does not look like a Zigote checkout (no Zigote.UI/Zigote.UI.csproj)."
            );
        }

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

    private static string? SearchUpwards(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(
                    Path.Combine(path1: dir.FullName, path2: "Zigote.UI", path3: "Zigote.UI.csproj")
                )) return dir.FullName;
            // A sibling checkout is as common as an ancestor one: apps usually live next to the
            // engine, not inside it.
            foreach (string sibling in new[] {
                         "Zigote",
                         "zigote",
                     })
            {
                string candidate = Path.Combine(path1: dir.FullName, path2: sibling);
                if (File.Exists(
                        Path.Combine(
                            path1: candidate,
                            path2: "Zigote.UI",
                            path3: "Zigote.UI.csproj"
                        )
                    )) return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

/// <summary>A project name that is also a valid C# namespace and an Android package segment.</summary>
public static class Identifier
{
    public static string Validate(string name)
    {
        if (name.Length == 0) throw new CliError("the name cannot be empty");
        if (!char.IsLetter(name[0])) throw new CliError($"'{name}' must start with a letter");
        if (!name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new CliError(
                $"'{name}' may only contain letters, digits and underscores — it becomes a C# namespace and an Android package segment."
            );
        }

        return name;
    }
}

/// <summary>
///     Writes the generated files, refusing to clobber anything that already exists unless asked.
///     Collects what it did so the command can print one summary instead of a line per file.
/// </summary>
public sealed class Scaffolder(string root, bool force)
{
    private readonly List<string> _skipped = [];
    private readonly List<string> _written = [];

    public void Write(string relativePath, string content)
    {
        string path = Path.Combine(path1: root, path2: relativePath);
        if (File.Exists(path) && !force)
        {
            _skipped.Add(relativePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Normalized to the platform's line endings so the generated sources do not show up as a
        // whole-file diff on the first commit from a different OS.
        File.WriteAllText(path: path, contents: content.ReplaceLineEndings());
        _written.Add(relativePath);
    }

    public void Report()
    {
        foreach (string f in _written) Console.WriteLine($"  created  {f}");
        foreach (string f in _skipped)
            Console.WriteLine($"  exists   {f}  (use --force to overwrite)");
    }
}
