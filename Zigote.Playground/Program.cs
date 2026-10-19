namespace Playground;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest();
        // `Zigote.Playground zigote://playground/example?…` opens a shared example.
        if (args.FirstOrDefault(a => a.StartsWith(ShareLink.Scheme, StringComparison.OrdinalIgnoreCase)) is { } link)
        {
            Shell.StartWith = ShareLink.Parse(link);
            if (Shell.StartWith is null) Console.Error.WriteLine("playground: not a valid example link, ignored");
        }

        // Warm Roslyn's binder and the doc files off-thread, so the first completion is instant.
        _ = Compiler.CompleteAsync("class Demo { void M() { } }", 24, default);
        _ = Task.Run(() => CodeFormatter.Format("class Demo{}"));
        new PlaygroundApp().Run();
        return 0;
    }

    /// <summary>
    ///     Headless check that the docs still match the framework: every page parses, and every live
    ///     example compiles and constructs. Run it after an API change — a broken example is a lie in
    ///     the docs.
    /// </summary>
    private static int SelfTest()
    {
        int examples = 0, failures = 0;
        foreach (var entry in Toc.Entries)
        {
            foreach (var code in Markdown.Parse(Toc.Read(entry.File)).OfType<Block.Code>().Where(c => c.Run))
            {
                examples++;
                var result = Compiler.Compile(code.Source);
                string? error = result.Errors.FirstOrDefault();
                if (result.Create is { } create)
                {
                    try
                    {
                        create();
                    }
                    catch (Exception e)
                    {
                        error = $"constructor threw {e.InnerException?.Message ?? e.Message}";
                    }
                }

                if (error is null) continue;
                failures++;
                Console.Error.WriteLine($"FAIL {entry.File} example {examples}: {error}");
                foreach (string more in result.Errors.Skip(1)) Console.Error.WriteLine($"     {more}");
            }
        }

        Console.WriteLine($"Playground self-test: {Toc.Entries.Length} pages, {examples} examples, {failures} failure(s)");
        int catalogFailures = CatalogSelfTest();
        return failures == 0 && catalogFailures == 0 && CompletionSelfTest() && FormatSelfTest() && ShareLinkSelfTest() ? 0 : 1;
    }

    // Share links round-trip any code, and anything else that looks like one is refused.
    private static bool ShareLinkSelfTest()
    {
        const string code = "class Demo : SizedBox { } // ünïcødé 🚀 ```fence``` & = ? # %";
        string link = ShareLink.Create(code, 280, "doc:Layout");
        var back = ShareLink.Parse("  " + link + "\n");
        bool ok = back == new SharedExample(code, 280, "doc:Layout");

        // A deflate bomb: 64 MB of zeros packs into a few KB; it must be refused, not inflated.
        using var bomb = new MemoryStream();
        using (var d = new System.IO.Compression.DeflateStream(bomb, System.IO.Compression.CompressionLevel.SmallestSize))
            d.Write(new byte[64 * 1024 * 1024]);
        string bombLink = "zigote://playground/example?v=1&code=" + Convert.ToBase64String(bomb.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        ok &= ShareLink.Parse(bombLink) is null;
        ok &= ShareLink.Parse(link.Replace("v=1", "v=2")) is null;
        ok &= ShareLink.Parse(link[..^6])?.Source != code; // truncated in transit: refused, never silently "the same"
        ok &= ShareLink.Parse("zigote://playground/example?v=1&code=!!!") is null &&
              ShareLink.Parse("https://example.com/?v=1&code=x") is null &&
              ShareLink.Parse(ShareLink.Create(code, 99999, "javascript:alert(1)"))! is { Height: 1200, From: null };
        Console.WriteLine($"Share-link self-test: {link.Length} chars for {code.Length} — {(ok ? "ok" : "FAIL")}");
        return ok;
    }

    // Formatting an example must keep it compiling and be idempotent (format twice = no change).
    private static bool FormatSelfTest()
    {
        int changed = 0, bad = 0, total = 0;
        var sources = Toc.Entries
            .SelectMany(entry => Markdown.Parse(Toc.Read(entry.File)).OfType<Block.Code>().Where(c => c.Run).Select(c => (entry.File, c.Source)))
            .Concat(Catalog.WidgetCatalog.Entries.Select(e => (File: e.Title + " sample", Catalog.WidgetCatalog.Sample(e.Type)?.Source))
                .Where(s => s.Source is not null).Select(s => (s.File, s.Source!)));
        foreach (var (file, source) in sources)
        {
            var code = (Source: source, File: file);
            var entry = (File: file, 0);
            total++;
            string once = Apply(code.Source, CodeFormatter.Format(code.Source));
            if (once != code.Source) changed++;
            if (CodeFormatter.Format(once).Count > 0 || Compiler.Compile(once).Create is null)
            {
                bad++;
                Console.Error.WriteLine($"FAIL format {entry.File} example {total}: {(Compiler.Compile(once).Create is null ? "no longer compiles: " + Compiler.Compile(once).Errors.FirstOrDefault() : "not idempotent")}");
                File.WriteAllText(Path.Combine(Path.GetTempPath(), $"fmt-fail-{total}.cs"), once);
            }

            if (Environment.GetEnvironmentVariable("PLAYGROUND_FORMAT_DUMP") is { } dir && once != code.Source)
            {
                File.WriteAllText(Path.Combine(dir, $"{total}.before.cs"), code.Source);
                File.WriteAllText(Path.Combine(dir, $"{total}.after.cs"), once);
            }
        }

        // A messy snippet comes out exactly in house style: spacing inside widget-tree collections,
        // indentation by nesting, Allman members, initializers kept on their line.
        const string messy =
            """
            class Demo : ComposedWidget {
              int _count=0;
                  protected override Widget Build(BuildContext context) =>
              new Padding(EdgeInsets.All(16), new Column(spacing: 12, children: [
            new Row(spacing:8, children: [
            new Label("hi",14),
                  new AdwButton(label:"Add",onPressed:()=>_count++) {Pill=true},
              ]),
                new Watch(() => new Label(
              $"count {_count}",
              fontSize: 13)),
                  ]));
              void Tick(){ if(_count>3){_count=0;}
              else
              _count++; }
            }
            """;
        const string tidy =
            """
            class Demo : ComposedWidget
            {
                int _count = 0;
                protected override Widget Build(BuildContext context) =>
                    new Padding(EdgeInsets.All(16), new Column(spacing: 12, children: [
                        new Row(spacing: 8, children: [
                            new Label("hi", 14),
                            new AdwButton(label: "Add", onPressed: () => _count++) { Pill = true },
                        ]),
                        new Watch(() => new Label(
                            $"count {_count}",
                            fontSize: 13)),
                    ]));
                void Tick()
                {
                    if (_count > 3) { _count = 0; }
                    else
                        _count++;
                }
            }
            """;
        if (Apply(messy, CodeFormatter.Format(messy)) != tidy)
        {
            bad++;
            Console.Error.WriteLine($"FAIL format: messy snippet came out as\n{Apply(messy, CodeFormatter.Format(messy))}");
        }

        Console.WriteLine($"Format self-test: {total} examples, {changed} reformatted, {bad} failure(s)");
        return bad == 0;

        static string Apply(string text, IReadOnlyList<CodeTextEdit> edits)
        {
            foreach (var e in edits.OrderByDescending(e => e.Start)) text = text[..e.Start] + e.NewText + text[(e.Start + e.Length)..];
            return text;
        }
    }

    // Completion answers members after a dot, scope at a word, and carries docs.
    private static bool CompletionSelfTest()
    {
        const string src = "class Demo : ComposedWidget { protected override Widget Build(BuildContext c) { var l = new Label(\"x\"); l.; return l; } }";
        var members = Compiler.Complete(src, src.IndexOf("l.;") + 2, default);
        var scope = Compiler.Complete(src, src.IndexOf("l.;"), default);
        var errors = Compiler.Compile(src).Markers;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Compiler.Complete(src, src.IndexOf("l.;"), default);
        long warm = clock.ElapsedMilliseconds;
        bool ok = members.Any(i => i.Label == "Text") && members.All(i => i.Kind != CodeCompletionKind.Keyword) &&
                  scope.Any(i => i.Label == "AdwButton") && scope.Any(i => i.Label == "l" && i.Kind == CodeCompletionKind.Local) &&
                  members.First(i => i.Label == "Text").Detail == "string" &&
                  errors.Any(m => m is { Severity: CodeMarkerSeverity.Error, Line: 0 });
        // Parameter hints: the call around the caret, the argument being typed, named arguments.
        const string call = "class Demo : SizedBox { void M() { new AdwButton(\"x\", ); System.Math.Clamp(1, max: 3, min: 0); } }";
        var hint = Compiler.SignatureHelp(call, call.IndexOf(", )") + 2, default);
        var named = Compiler.SignatureHelp(call, call.IndexOf("min: 0") + 5, default);
        var outside = Compiler.SignatureHelp(call, call.IndexOf("void M"), default);
        var shown = hint?.Signatures[hint.ActiveSignature];
        var clamp = named?.Signatures[named.ActiveSignature];
        ok &= shown is not null && shown.Label.StartsWith("AdwButton(") && shown.ActiveParameter == 1 &&
              clamp is not null && clamp.Label.Substring(clamp.Parameters[clamp.ActiveParameter].Start, clamp.Parameters[clamp.ActiveParameter].Length).EndsWith(" min") &&
              outside is null;

        // Console.WriteLine in an example reaches that example's console sink.
        var echo = Compiler.Compile("class Demo : SizedBox { public Demo() { Console.WriteLine(\"hi {0}\", 7); } }");
        string heard = "";
        ExampleConsole.Register(echo.Assembly!, t => heard += t);
        echo.Create!();
        ok &= heard == "hi 7\n";
        Console.WriteLine($"Completion self-test: {members.Count} members, {scope.Count} in scope, {errors.Count} marker(s), warm {warm} ms — {(ok ? "ok" : "FAIL")}");
        return ok;
    }

    /// <summary>
    ///     Every catalog page must generate and parse, and every sample — curated or generated —
    ///     must compile and construct, so a widget whose constructor changes breaks this test rather
    ///     than a reader's first click.
    /// </summary>
    private static int CatalogSelfTest()
    {
        int samples = 0, failures = 0, documented = 0;
        Markdown.Parse(Catalog.WidgetCatalog.IndexMarkdown());
        foreach (var entry in Catalog.WidgetCatalog.Entries)
        {
            string page;
            try
            {
                page = Catalog.WidgetCatalog.PageMarkdown(entry);
                Markdown.Parse(page);
            }
            catch (Exception e)
            {
                failures++;
                Console.Error.WriteLine($"FAIL catalog {entry.Title}: page generation threw {e.GetType().Name}: {e.Message}");
                continue;
            }

            if (Catalog.XmlDocs.Type(entry.Type) is not null) documented++;
            if (Catalog.WidgetCatalog.Sample(entry.Type) is not { } sample) continue;
            samples++;
            var result = Compiler.Compile(sample.Source);
            string? error = result.Errors.FirstOrDefault();
            if (result.Create is { } create)
            {
                try
                {
                    create();
                }
                catch (Exception e)
                {
                    error = $"constructor threw {e.InnerException?.Message ?? e.Message}";
                }
            }

            if (error is null) continue;
            failures++;
            Console.Error.WriteLine($"FAIL catalog {entry.Title} sample{(sample.Generated ? " (generated)" : "")}: {error}");
        }

        // A curated sample whose file name matches no widget would be silently ignored — catch the typo.
        var widgetNames = Catalog.WidgetCatalog.Entries.Select(e => e.Type.Name).ToHashSet();
        foreach (string res in typeof(Program).Assembly.GetManifestResourceNames().Where(r => r.StartsWith("Samples/")))
        {
            string type = Path.GetFileNameWithoutExtension(res);
            if (widgetNames.Contains(type)) continue;
            failures++;
            Console.Error.WriteLine($"FAIL catalog: curated sample {res} matches no widget");
        }

        // Every page a link can open: any public, non-widget type in the libraries the catalog covers.
        int typePages = 0;
        foreach (var asm in new[] { typeof(Widget).Assembly, typeof(AdwButton).Assembly, typeof(Color).Assembly })
        {
            foreach (var t in asm.GetExportedTypes().Where(t => !typeof(Widget).IsAssignableFrom(t)))
            {
                try
                {
                    Markdown.Parse(Catalog.WidgetCatalog.TypePageMarkdown(t));
                    typePages++;
                }
                catch (Exception e)
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL catalog type page {t.FullName}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        Console.WriteLine(
            $"Catalog self-test: {Catalog.WidgetCatalog.Entries.Length} widgets ({documented} with doc comments), {samples} samples, {typePages} type pages, {failures} failure(s)"
        );
        return failures;
    }
}

internal sealed class PlaygroundApp : AdwaitaApp
{
    public PlaygroundApp() : base(title: "Zigote Playground")
    {
        Width = 1240;
        Height = 820;
        Home = new SafeArea(new Shell());
    }

    // A broken example becomes its own error message; anything else still crashes as usual.
    protected override bool OnFrameError(Exception error) => ExampleView.Blame(error);
}
