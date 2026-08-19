using CommandLine;
using CommandLine.Text;

namespace Zigote.Cli;

/// <summary>
///     <c>zigote</c> — scaffolds a Zigote app and the platform heads it ships on, generates
///     platform plugins, previews widgets, and checks the machine with <c>doctor</c>.
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
///         arrangement that is known to work, with the traps already avoided and commented. The
///         command surface itself is declarative (<see cref="Verbs" />): the parser owns help,
///         version and error text, so the options and their documentation cannot drift apart.
///     </para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        using var parser = new Parser(with =>
            {
                with.HelpWriter = null; // rendered below, so heading and destination are ours
                with.AutoHelp = true;
                with.AutoVersion = true;
            }
        );
        var result = parser.ParseArguments<CreateVerb, AddVerb, PreviewVerb, DoctorVerb>(args);

        try
        {
            return result.MapResult(
                parsedFunc1: (CreateVerb verb) => Create(verb),
                parsedFunc2: (AddVerb verb) => Add(verb),
                parsedFunc3: (PreviewVerb verb) => Preview.Run(
                    options: verb,
                    project: FindAppProject(verb.Directory)
                ),
                parsedFunc4: (DoctorVerb verb) => Doctor.Run(verb),
                notParsedFunc: errors => Render(result: result, errors: errors)
            );
        }
        catch (CliError e)
        {
            // Anything the user can fix by typing something different: one line, no stack trace.
            Console.Error.WriteLine($"zigote: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    ///     Help, version, and parse errors, from the same attributes the parser matched against.
    ///     Requested help goes to stdout and exits 0; a mistake goes to stderr and exits 1.
    /// </summary>
    private static int Render(ParserResult<object> result, IEnumerable<Error> errors)
    {
        var errorList = errors.ToList();
        if (errorList.IsVersion())
        {
            Console.Out.WriteLine(
                $"zigote {typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}"
            );
            return 0;
        }

        bool requested = errorList.IsHelp();
        var help = HelpText.AutoBuild(
            parserResult: result,
            onError: h =>
            {
                h.Heading = "zigote — scaffold Zigote apps, plugins and platform heads";
                h.Copyright = "";
                h.AdditionalNewLineAfterOption = false;
                return HelpText.DefaultParsingErrorsHandler(parserResult: result, current: h);
            },
            onExample: e => e,
            verbsIndex: true
        );
        (requested ? Console.Out : Console.Error).WriteLine(help);
        return requested ? 0 : 1;
    }

    // ── create ────────────────────────────────────────────────────────────────

    private static int Create(CreateVerb verb) =>
        verb.First switch {
            "app" => CreateApp(options: verb, name: RequireName(verb: verb, template: "app")),
            "plugin" => CreatePlugin(options: verb, name: RequireName(verb: verb, template: "plugin")),
            // Two bare words is a typo'd template, not a name — naming the app after the first
            // one and silently dropping the second would hide the mistake.
            _ when verb.Second is not null => throw new CliError(
                $"unknown template '{verb.First}' — use 'app' or 'plugin'."
            ),
            _ => CreateApp(options: verb, name: verb.First),
        };

    private static string RequireName(CreateVerb verb, string template) =>
        verb.Second ?? throw new CliError(
            $"create {template} needs a name: zigote create {template} <Name>"
        );

    private static int CreateApp(CreateVerb options, string name)
    {
        name = Identifier.Validate(name);
        string root = Path.Combine(path1: options.Directory, path2: name);
        string engine = options.ResolveEngine(root);

        var files = new Scaffolder(root: root, force: options.Force);
        files.Write(
            relativePath: $"{name}/{name}.csproj",
            content: Templates.AppCsproj(name: name, engine: engine)
        );
        files.Write(relativePath: $"{name}/Program.cs", content: Templates.AppProgram(name));
        files.Write(relativePath: $"{name}/{name}App.cs", content: Templates.AppShell(name));
        files.Write(relativePath: ".gitignore", content: Templates.GitIgnore());
        files.Write(relativePath: "README.md", content: Templates.Readme(name));

        files.Report();
        Console.WriteLine();
        Console.WriteLine($"  cd {name} && dotnet run --project {name}");
        Console.WriteLine($"  zigote add android                    (from inside {name}/)");
        return 0;
    }

    /// <summary>
    ///     A platform plugin: one shared API over <c>PlatformChannel</c>, per-platform
    ///     implementations selected at build time by target framework, and an example app that
    ///     exercises it — the layout docs/plugins.md describes, generated instead of hand-copied.
    ///     Desktop (Windows/macOS/Linux, the base <c>net10.0</c> build) is always present because
    ///     every consumer compiles it; android and ios are opted in via --platforms.
    /// </summary>
    private static int CreatePlugin(CreateVerb options, string name)
    {
        name = Identifier.Validate(name);
        (bool android, bool ios) = ParsePlatforms(options.Platforms);
        string root = Path.Combine(path1: options.Directory, path2: name);
        string engine = options.ResolveEngine(root);

        var files = new Scaffolder(root: root, force: options.Force);
        files.Write(
            relativePath: $"{name}/{name}.csproj",
            content: Templates.PluginCsproj(name: name, engine: engine, android: android, ios: ios)
        );
        files.Write(relativePath: $"{name}/{name}Plugin.cs", content: Templates.PluginShared(name));
        files.Write(
            relativePath: $"{name}/Platforms/Desktop/{name}Channels.cs",
            content: Templates.PluginDesktopChannels(name)
        );
        if (android)
        {
            files.Write(
                relativePath: $"{name}/Platforms/Android/{name}Channels.cs",
                content: Templates.PluginAndroidChannels(name)
            );
        }

        if (ios)
        {
            files.Write(
                relativePath: $"{name}/Platforms/iOS/{name}Channels.cs",
                content: Templates.PluginIosChannels(name)
            );
        }

        // The example app is a real app one level deeper, so its engine path needs its own
        // resolution — measured from example/, where the app project directory sits.
        string exampleEngine = options.ResolveEngine(Path.Combine(path1: root, path2: "example"));
        files.Write(
            relativePath: $"example/{name}Example/{name}Example.csproj",
            content: Templates.AppCsproj(
                name: $"{name}Example",
                engine: exampleEngine,
                extraReference: $"../../{name}/{name}.csproj"
            )
        );
        files.Write(
            relativePath: $"example/{name}Example/Program.cs",
            content: Templates.PluginExampleProgram(name)
        );
        files.Write(
            relativePath: $"example/{name}Example/{name}ExampleApp.cs",
            content: Templates.PluginExampleShell(name)
        );
        files.Write(relativePath: ".gitignore", content: Templates.GitIgnore());
        files.Write(
            relativePath: "README.md",
            content: Templates.PluginReadme(name: name, android: android, ios: ios)
        );

        files.Report();
        Console.WriteLine();
        Console.WriteLine($"  cd {name}/example && dotnet run --project {name}Example");
        Console.WriteLine($"  dotnet pack {name}/{name}                        # ship it as a NuGet package");
        return 0;
    }

    /// <summary>
    ///     Normalize a --platforms list. Desktop names are accepted and folded into the base
    ///     build rather than rejected, because "which word means my laptop" should never be the
    ///     thing that stops a scaffold.
    /// </summary>
    private static (bool Android, bool Ios) ParsePlatforms(string? spec)
    {
        if (spec is null) return (true, true);
        bool android = false, ios = false;
        foreach (string raw in spec.Split(
                     separator: ',',
                     options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                 ))
        {
            switch (raw.ToLowerInvariant())
            {
                case "android":
                    android = true;
                    break;
                case "ios":
                    ios = true;
                    break;
                case "desktop" or "linux" or "macos" or "windows":
                    break; // the base net10.0 build — always generated
                default:
                    throw new CliError(
                        $"unknown platform '{raw}'. Use android, ios, desktop (desktop covers windows/macos/linux and is always included)."
                    );
            }
        }

        return (android, ios);
    }

    // ── add ───────────────────────────────────────────────────────────────────

    private static int Add(AddVerb options)
    {
        if (options.Platform != "android")
            throw new CliError($"unknown platform '{options.Platform}'. Only 'android' exists today.");

        string root = options.Directory;
        // The app to attach to is the one shared project in the tree — found rather than asked
        // for, because getting it wrong silently produces a head that compiles nothing.
        string app = FindAppProject(root);
        string name = Path.GetFileNameWithoutExtension(app);
        string engine = options.ResolveEngine(root);
        string appId = Identifier.ValidateAppId(options.AppId ?? $"dev.zigote.{name}");

        var files = new Scaffolder(root: root, force: options.Force);
        files.Write(
            relativePath: $"{name}.Android/{name}.Android.csproj",
            content: Templates.AndroidCsproj(name: name, appId: appId, engine: engine)
        );
        files.Write(
            relativePath: $"{name}.Android/Properties/AndroidManifest.xml",
            content: Templates.AndroidManifest(appId: appId, name: name)
        );
        files.Write(
            relativePath: $"{name}.Android/{name}Application.cs",
            content: Templates.AndroidApplication(name)
        );

        files.Report();
        Console.WriteLine();
        Console.WriteLine(
            $"  dotnet build {name}.Android -p:ZigTargetRid=android-arm64   # device"
        );
        Console.WriteLine(
            $"  dotnet build {name}.Android -p:ZigTargetRid=android-x64     # emulator"
        );
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
    internal static string FindAppProject(string root)
    {
        var candidates = Directory
            .EnumerateFiles(
                path: root,
                searchPattern: "*.csproj",
                searchOption: SearchOption.TopDirectoryOnly
            )
            .Concat(
                Directory
                    .EnumerateDirectories(root)
                    .Where(d => !Path.GetFileName(d).StartsWith('.'))
                    .SelectMany(d => Directory.EnumerateFiles(
                            path: d,
                            searchPattern: "*.csproj",
                            searchOption: SearchOption.TopDirectoryOnly
                        )
                    )
            )
            .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith(
                    value: ".Android",
                    comparisonType: StringComparison.Ordinal
                )
            )
            .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith(
                    value: ".iOS",
                    comparisonType: StringComparison.Ordinal
                )
            )
            .ToList();

        return candidates.Count switch {
            0 => throw new CliError(
                $"no app project found under '{root}'. Run this from inside the app, or pass --dir."
            ),
            1 => candidates[0],
            _ => throw new CliError(
                "more than one app project here, so which one the head belongs to is ambiguous: " +
                string.Join(separator: ", ", values: candidates.Select(Path.GetFileName)) +
                ". Run from inside one of them."
            ),
        };
    }
}
