namespace Zigote.Cli;

/// <summary>
///     <c>zigote</c> — scaffolds a Zigote app and the platform heads it ships on.
///     <para>
///         The reason this exists is the Android head. A Zigote app is one shared body of C# plus a
///         per-platform head, and the Android head is not a file you write from memory: an
///         Application object that registers the app-main before SDL's thread starts, an
///         SDLActivity subclass, the vendored SDL Java sources, the engine's ABI folder, a manifest
///         whose service type has to match its permission, and a mandatory RID that couples the
///         managed build to the native cross-compile. Getting any one of those wrong produces an
///         app that installs and dies. Hand-copying it from another project is how those mistakes
///         spread.
///     </para>
///     <para>
///         So the templates here are not a starting point to be edited into shape — they are the
///         arrangement that is known to work, with the traps already avoided and commented.
///     </para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Usage(0);

        try
        {
            return args[0] switch
            {
                "create" => Create(args[1..]),
                "add" => Add(args[1..]),
                "--version" or "-v" => Version(),
                _ => Fail($"unknown command '{args[0]}'")
            };
        }
        catch (CliError e)
        {
            // Anything the user can fix by typing something different: one line, no stack trace.
            Console.Error.WriteLine($"zigote: {e.Message}");
            return 1;
        }
    }

    private static int Version()
    {
        Console.WriteLine("zigote 0.1.0");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"zigote: {message}");
        Console.Error.WriteLine();
        return Usage(1);
    }

    private static int Usage(int code)
    {
        Console.WriteLine(
            """
            zigote — scaffold Zigote apps and platform heads

            USAGE
              zigote create <Name> [options]     create an app (shared sources + desktop head)
              zigote add android [options]       add an Android head to an existing app
              zigote --version

            OPTIONS
              --dir <path>          where to work. Default: the current directory.
              --engine <path>       path to the Zigote checkout the generated projects reference.
                                    Default: found by walking up from --dir, else $ZIGOTE_ROOT.
              --id <app.id>         application id for a platform head. Default: dev.zigote.<name>.
              --force               overwrite files that already exist.

            EXAMPLES
              zigote create Metronome
              cd Metronome && zigote add android --id dev.zigote.Metronome

            After `add android`, build with the RID that selects BOTH halves of the build:
              dotnet build <Name>.Android -p:ZigTargetRid=android-arm64   # device
              dotnet build <Name>.Android -p:ZigTargetRid=android-x64     # emulator
            """
        );
        return code;
    }

    // ── commands ──────────────────────────────────────────────────────────────

    private static int Create(string[] args)
    {
        var options = Options.Parse(args, out var positional);
        if (positional.Count == 0) throw new CliError("create needs a name: zigote create <Name>");

        var name = Identifier.Validate(positional[0]);
        var root = Path.Combine(options.Directory, name);
        var engine = options.ResolveEngine(root);

        var files = new Scaffolder(root, options.Force);
        files.Write($"{name}/{name}.csproj", Templates.AppCsproj(name, engine));
        files.Write($"{name}/Program.cs", Templates.AppProgram(name));
        files.Write($"{name}/{name}App.cs", Templates.AppShell(name));
        files.Write(".gitignore", Templates.GitIgnore());
        files.Write("README.md", Templates.Readme(name));

        files.Report();
        Console.WriteLine();
        Console.WriteLine($"  cd {name} && dotnet run --project {name}");
        Console.WriteLine($"  zigote add android                    (from inside {name}/)");
        return 0;
    }

    private static int Add(string[] args)
    {
        var options = Options.Parse(args, out var positional);
        if (positional.Count == 0) throw new CliError("add needs a platform: zigote add android");
        if (positional[0] != "android")
            throw new CliError($"unknown platform '{positional[0]}'. Only 'android' exists today.");

        var root = options.Directory;
        // The app to attach to is the one shared project in the tree — found rather than asked
        // for, because getting it wrong silently produces a head that compiles nothing.
        var app = FindAppProject(root);
        var name = Path.GetFileNameWithoutExtension(app);
        var engine = options.ResolveEngine(root);
        var appId = options.AppId ?? $"dev.zigote.{name}";

        var files = new Scaffolder(root, options.Force);
        files.Write($"{name}.Android/{name}.Android.csproj", Templates.AndroidCsproj(name, appId, engine));
        files.Write($"{name}.Android/Properties/AndroidManifest.xml", Templates.AndroidManifest(appId, name));
        files.Write($"{name}.Android/{name}Application.cs", Templates.AndroidApplication(name));

        files.Report();
        Console.WriteLine();
        Console.WriteLine($"  dotnet build {name}.Android -p:ZigTargetRid=android-arm64   # device");
        Console.WriteLine($"  dotnet build {name}.Android -p:ZigTargetRid=android-x64     # emulator");
        Console.WriteLine();
        Console.WriteLine("The RID is mandatory: it selects the managed RID AND the native");
        Console.WriteLine("cross-compile, and the generated project refuses to build without it.");
        return 0;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     The shared app project a head should compile. Looks for a single non-head csproj at the
    ///     top of the tree, then one directory down — the layout `create` produces.
    /// </summary>
    private static string FindAppProject(string root)
    {
        var candidates = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly)
            .Concat(Directory
                .EnumerateDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.TopDirectoryOnly)))
            .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith(".Android", StringComparison.Ordinal))
            .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith(".iOS", StringComparison.Ordinal))
            .ToList();

        return candidates.Count switch
        {
            0 => throw new CliError($"no app project found under '{root}'. Run this from inside the app, or pass --dir."),
            1 => candidates[0],
            _ => throw new CliError(
                "more than one app project here, so which one the head belongs to is ambiguous: " +
                string.Join(", ", candidates.Select(Path.GetFileName)) + ". Run from inside one of them.")
        };
    }
}

/// <summary>A message meant for the user, not a stack trace.</summary>
public sealed class CliError(string message) : Exception(message);
