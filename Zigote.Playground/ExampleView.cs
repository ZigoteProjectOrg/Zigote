using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Zigote.Modules.UI.CodeEditor;

namespace Playground;

/// <summary>
///     A live example: an editable source beside (or above) the widget it compiles to. <c>⌘↵</c>,
///     <c>⌘S</c> or Run recompiles off the UI thread and swaps the preview; with Live on, every
///     pause in typing does. Errors are squiggled in the editor and listed under the preview — click
///     one to jump to it. <c>Console.WriteLine</c> lands in the example's console. The preview can be
///     flipped to the other theme or to right-to-left. An example that throws — while building,
///     laying out, painting or handling input — is replaced by its exception instead of taking the
///     playground down (see <see cref="Blame" />).
/// </summary>
internal sealed class ExampleView : ComposedWidget
{
    private const int ConsoleLimit = 16_000;

    // Mounted examples whose preview is on screen, for Blame. UI thread only.
    private static readonly HashSet<ExampleView> Live = [];

    // Shared by every example: one switch sets the layout and live-run everywhere.
    private static readonly Signal<bool> SideBySide = new(false);
    private static readonly Signal<bool> RunAsYouType = new(true);

    private readonly Signal<string> _console = new("");
    private readonly Signal<bool> _flipTheme = new(false);
    private readonly Signal<bool> _rtl = new(false);
    private readonly AdwCodeEditor _editor;
    private readonly float _height;
    private readonly string _original;
    private readonly Signal<RunState> _state = new(RunState.Idle);
    private Assembly? _consoleOwner;
    private int _editGeneration;
    private int _generation;
    private readonly string? _from;
    private readonly Signal<string?> _notice = new(null);
    private bool _armed; // may compile and run: always for the docs' own code, only after Run for a link's

    /// <param name="from">The page this example lives on (<c>doc:Title</c>, <c>type:Full.Name</c>), for share links.</param>
    /// <param name="trusted">
    ///     False for code that arrived in a link: it is shown but never compiled or run — not on
    ///     mount, not by live typing — until the reader presses Run. It runs with the reader's
    ///     permissions, so reading it first is the only safeguard.
    /// </param>
    public ExampleView(string source, float height, string? from = null, bool trusted = true)
    {
        _original = source;
        _height = height;
        _from = from;
        _armed = trusted;
        _editor = new AdwCodeEditor(source) {
            Tokenizer = Highlighting.CSharp,
            OnSubmit = Run,
            OnChanged = _ => ScheduleLiveRun(),
            CompletionProvider = Compiler.CompleteAsync,
            SignatureProvider = Compiler.SignatureHelpAsync,
            OnKeyDown = (_, scancode, mods) =>
            {
                if (!mods.HasCommand()) return false;
                if (scancode is 40 or 88) Run(); // ⌘↵
                else if (scancode == 15 && mods.HasFlag(Modifiers.Alt)) Format(); // ⌥⌘L, the IDE "reformat code"
                else return false;
                return true;
            },
            SemanticsLabel = "Example source",
        };
    }

    protected override void OnMount()
    {
        Live.Add(this);
        if (_armed && _state.Peek() == RunState.Idle) Run();
    }

    protected override void OnUnmount() => Live.Remove(this);

    /// <summary>Compile the editor's text and show what it builds. Safe to call repeatedly.</summary>
    public void Run()
    {
        _armed = true;
        int generation = ++_generation;
        string source = _editor.Text;
        var app = Owner;
        _state.Value = _state.Peek() with { Busy = true };
        Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            CompileResult result;
            try
            {
                result = Compiler.Compile(source);
            }
            catch (Exception e)
            {
                result = new CompileResult(null, null, [e.Message]);
            }

            // Latest wins: a slow compile never overwrites the one started after it.
            if (generation != Volatile.Read(ref _generation)) return;
            app?.Post(() =>
            {
                if (generation != _generation) return;
                _editor.Markers = result.Markers;
                // A failed compile keeps the last working preview's console; a new one starts clean.
                if (result.Create is not null) AttachConsole(result.Assembly);
                _state.Value = new RunState(result, clock.ElapsedMilliseconds, false, null);
            });
        });
    }

    // Live mode: compile once typing pauses.
    private void ScheduleLiveRun()
    {
        if (!RunAsYouType.Peek() || !_armed) return;
        int generation = ++_editGeneration;
        var app = Owner;
        Task.Delay(500).ContinueWith(_ =>
        {
            if (generation == Volatile.Read(ref _editGeneration)) app?.Post(Run);
        });
    }

    private void AttachConsole(Assembly? assembly)
    {
        if (_consoleOwner is not null) ExampleConsole.Unregister(_consoleOwner);
        _consoleOwner = assembly;
        _console.Value = "";
        if (assembly is null) return;
        // Any thread may write (examples run tasks); a signal write is safe from anywhere.
        ExampleConsole.Register(assembly, text =>
        {
            string next = _console.Peek() + text;
            _console.Value = next.Length > ConsoleLimit ? next[^ConsoleLimit..] : next;
        });
    }

    /// <summary>Reformat the source (standard C# rules, the docs' brace style) as one undoable edit.</summary>
    public void Format()
    {
        string source = _editor.Text;
        var app = Owner;
        Task.Run(() =>
        {
            IReadOnlyList<CodeTextEdit> edits;
            try
            {
                edits = CodeFormatter.Format(source);
            }
            catch (Exception e)
            {
                System.Console.Error.WriteLine($"playground: format failed: {e}");
                return;
            }

            app?.Post(() =>
            {
                // Typed since the request? Those offsets describe a text that no longer exists.
                if (_editor.Text == source) _editor.ApplyEdits(edits);
            });
        });
    }

    /// <summary>Copy a link that reopens this example — with its current, edited code — in any playground.</summary>
    private void Share()
    {
        Zigote.Core.Engine.ZigoteEngine.Instance?.SetClipboard(ShareLink.Create(_editor.Text, (int)_height, _from));
        _notice.Value = "Link copied — paste it into search to open";
        var app = Owner;
        Task.Delay(3000).ContinueWith(_ => app?.Post(() => _notice.Value = null));
    }

    private void Reset()
    {
        _editor.Text = _original;
        Run();
    }

    /// <summary>
    ///     The playground's frame-error hook: stop the example(s) the exception came from. When no
    ///     example's code is on the stack (a framework widget an example configured badly), every live
    ///     example is stopped. False when nothing is live — the error is the playground's own.
    /// </summary>
    public static bool Blame(Exception error)
    {
        if (Live.Count == 0) return false;
        var onStack = new HashSet<Assembly>();
        for (var e = error; e is not null; e = e.InnerException)
        {
            foreach (var frame in new StackTrace(e).GetFrames())
                if (frame.GetMethod()?.DeclaringType?.Assembly is { } a) onStack.Add(a);
        }

        var guilty = Live.Where(v => v._state.Peek().Result?.Assembly is { } a && onStack.Contains(a))
            .ToList();
        foreach (var view in guilty.Count > 0 ? guilty : [.. Live]) view.Crashed(error);
        return true;
    }

    private void Crashed(Exception error)
    {
        _state.Value = _state.Peek() with { Crash = error };
        if (CrashLine(error) is { } line)
            _editor.Markers = [new CodeMarker(line, 0, line, int.MaxValue, CodeMarkerSeverity.Error, $"{Unwrap(error).GetType().Name}: {Unwrap(error).Message}")];
    }

    // The example's own line the exception left from, zero-based (the embedded PDB names it).
    private static int? CrashLine(Exception e)
    {
        var m = Regex.Match(Unwrap(e).StackTrace ?? "", @"Example\.cs:line (\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) - 1 : null;
    }

    private static Exception Unwrap(Exception e)
    {
        while (e is TargetInvocationException { InnerException: { } inner }) e = inner;
        return e;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        int lines = _original.Count(c => c == '\n') + 1;
        float editorHeight = Math.Clamp(lines * 19f + 16f, 96f, 640f);

        var toolbar = new Row(
            spacing: Spacing.Xs,
            children: [
                new IconGlyph(glyph: MaterialIcons.Code, size: 16f, color: theme.TextSecondary),
                new Label(text: "Live example", style: AdwTypography.Heading, color: theme.TextSecondary),
                new SizedBox(width: Spacing.Sm),
                new Expanded(new Watch(() => new Label(
                        text: _notice.Value ?? Status(_state.Value),
                        style: AdwTypography.Caption,
                        color: _notice.Value is not null ? theme.Accent
                            : _state.Value is { Crash: not null } or { Result.Create: null, Busy: false, Result: not null } ? theme.Error
                            : theme.TextMuted
                    ) { Overflow = TextOverflow.Ellipsis, MaxLines = 1 }
                )),
                Toggle(RunAsYouType, MaterialIcons.Bolt, "Run as you type"),
                Toggle(SideBySide, MaterialIcons.VerticalSplit, "Editor beside the preview"),
                Toggle(_flipTheme, theme.IsDark ? MaterialIcons.LightMode : MaterialIcons.DarkMode, theme.IsDark ? "Preview in light" : "Preview in dark"),
                Toggle(_rtl, MaterialIcons.FormatTextdirectionRToL, "Preview right-to-left"),
                new Tooltip(
                    message: "Copy a link to this example, with your edits",
                    child: new AdwButton(onPressed: Share) {
                        IconName = MaterialIcons.Share,
                        Style = AdwButtonStyle.Flat,
                        Circular = true,
                    }
                ),
                new Tooltip(
                    message: "Format the code (⌥⌘L)",
                    child: new AdwButton(onPressed: Format) {
                        IconName = MaterialIcons.FormatAlignLeft,
                        Style = AdwButtonStyle.Flat,
                        Circular = true,
                    }
                ),
                new Tooltip(
                    message: "Reset to the original source",
                    child: new AdwButton(onPressed: Reset) {
                        IconName = MaterialIcons.Refresh,
                        Style = AdwButtonStyle.Flat,
                        Circular = true,
                    }
                ),
                new SizedBox(width: Spacing.Xs),
                new Tooltip(
                    message: "Compile and run (⌘↵)",
                    child: new AdwButton(label: "Run", onPressed: Run) {
                        IconName = MaterialIcons.PlayArrow,
                        Style = AdwButtonStyle.Suggested,
                    }
                ),
            ]
        );

        var editor = new SizedBox(height: editorHeight, child: _editor);
        Widget Stage(float height) => new SizedBox(height: height, child: new Watch(() => StageFor(theme)));

        return new DecoratedBox {
            Fill = p.CardBg,
            Radius = AdwMetrics.CardRadius,
            BorderColor = p.CardShade,
            Child = new ClipRRect(
                radius: AdwMetrics.CardRadius,
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    mainAxisSize: MainAxisSize.Min,
                    children: [
                        new Padding(EdgeInsets.Only(left: Spacing.Md, right: Spacing.Sm, top: Spacing.Xs, bottom: Spacing.Xs), toolbar),
                        new ColoredBox(p.CardShade, new SizedBox(height: 1f)),
                        new Watch(() => SideBySide.Value
                            ? new SizedBox(
                                height: Math.Max(editorHeight, _height),
                                child: new AdwPaned(first: _editor, second: Stage(Math.Max(editorHeight, _height))) { Position = 0.55f }
                            )
                            : new Column(
                                crossAxisAlignment: CrossAxisAlignment.Stretch,
                                mainAxisSize: MainAxisSize.Min,
                                children: [editor, new ColoredBox(p.CardShade, new SizedBox(height: 1f)), Stage(_height)]
                            )
                        ),
                        new Watch(() => ConsolePane(theme, p)),
                    ]
                )
            ),
        };
    }

    private static Widget Toggle(Signal<bool> on, string icon, string tip) => new Tooltip(
        message: tip,
        child: new Watch(() => new AdwButton(onPressed: () => on.Value = !on.Peek()) {
            IconName = icon,
            Style = on.Value ? AdwButtonStyle.Regular : AdwButtonStyle.Flat,
            Circular = true,
        })
    );

    // The stage: whatever the example builds, under the preview's own theme and direction.
    private Widget StageFor(ThemeData appTheme)
    {
        // Diagnostics are the playground's, not the example's: no theme flip, no mirroring.
        if (_state.Value is { Crash: not null } or { Result.Create: null })
            return new ColoredBox(AdwPalette.For(appTheme).ViewBg, new ClipRect(new Watch(Preview)));
        var theme = _flipTheme.Value ? appTheme.IsDark ? AdwTheme.Light : AdwTheme.Dark : appTheme;
        return new ThemeProvider(
            theme,
            new Directionality(
                _rtl.Value ? TextDirection.Rtl : TextDirection.Ltr,
                new ColoredBox(AdwPalette.For(theme).ViewBg, new ClipRect(new Watch(Preview)))
            )
        );
    }

    private Widget ConsolePane(ThemeData theme, AdwColors p)
    {
        string text = _console.Value;
        if (text.Length == 0) return new SizedBox();
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min,
            children: [
                new ColoredBox(p.CardShade, new SizedBox(height: 1f)),
                new Padding(
                    EdgeInsets.Only(left: Spacing.Md, right: Spacing.Sm, top: Spacing.Xs),
                    new Row(spacing: Spacing.Xs, children: [
                        new IconGlyph(glyph: MaterialIcons.Terminal, size: 14f, color: theme.TextSecondary),
                        new Expanded(new Label(text: "Console", style: AdwTypography.Caption, color: theme.TextSecondary)),
                        new Tooltip(message: "Clear the console", child: new AdwButton(onPressed: () => _console.Value = "") {
                            IconName = MaterialIcons.ClearAll,
                            Style = AdwButtonStyle.Flat,
                            Circular = true,
                        }),
                    ])
                ),
                new SizedBox(
                    height: Math.Min(140f, 22f + (text.Count(c => c == '\n') + 1) * 17f),
                    child: new SingleChildScrollView {
                        Child = new Padding(
                            EdgeInsets.Only(left: Spacing.Md, right: Spacing.Md, bottom: Spacing.Sm),
                            new Label(text: text.TrimEnd('\n'), style: AdwTypography.Monospace, color: theme.OnSurface)
                        ),
                    }
                ),
            ]
        );
    }

    private Widget Preview()
    {
        var state = _state.Value;
        if (state.Crash is { } crash) return new ProblemView("Threw while running", [Describe(crash)]);
        if (state.Result is not { } result)
            return _armed // Run writes _state, so this re-evaluates once it is pressed
                ? new Center(new AdwSpinner())
                : new Center(new Label(text: "Not run yet — read the code, then press Run.", style: AdwTypography.Body));
        if (result.Create is not { } create)
        {
            var errors = result.Markers.Where(m => m.Severity == CodeMarkerSeverity.Error).ToArray();
            return errors.Length == 0
                ? new ProblemView("Does not compile", result.Errors)
                : new ProblemView("Does not compile", errors, m => _editor.GoTo(m.Line, m.Column));
        }

        try
        {
            // Untracked: signals the example reads in its constructor belong to the example, not
            // to this Watch — otherwise every change it makes would re-create it.
            return Reactive.Untracked(create);
        }
        catch (Exception e)
        {
            Crashed(e);
            return new ProblemView("Threw in its constructor", [Describe(e)]);
        }
    }

    private static string Describe(Exception e)
    {
        e = Unwrap(e);
        // The example's own frames are what the reader can act on; the framework's are noise.
        string where = e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Example.cs"))?.Trim() ?? "";
        return $"{e.GetType().Name}: {e.Message}\n{where}";
    }

    private static string Status(RunState s) => s switch {
        { Busy: true } => "Compiling…",
        { Crash: not null } => "Stopped — fix the code and run again",
        { Result.Create: null } => $"{s.Result!.Errors.Length} error(s)",
        { Result: not null } => $"Compiled in {s.Millis} ms",
        _ => "",
    };

    private sealed record RunState(CompileResult? Result, long Millis, bool Busy, Exception? Crash)
    {
        public static readonly RunState Idle = new(null, 0, false, null);
    }
}

/// <summary>A failure in the preview's place: a heading and its lines, or clickable diagnostics.</summary>
internal sealed class ProblemView : ComposedWidget
{
    private readonly string _title;
    private readonly string[] _lines;
    private readonly CodeMarker[] _markers = [];
    private readonly Action<CodeMarker>? _jump;

    public ProblemView(string title, string[] lines)
    {
        _title = title;
        _lines = lines;
    }

    public ProblemView(string title, CodeMarker[] markers, Action<CodeMarker> jump)
    {
        _title = title;
        _lines = [];
        _markers = markers;
        _jump = jump;
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var column = new Column(
            spacing: Spacing.Xs,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min,
            children: [new Label(text: _title, style: AdwTypography.Heading, color: theme.Error)]
        );
        foreach (string line in _lines)
            column.Children.Add(new Label(text: line, style: AdwTypography.Monospace, color: theme.OnSurface));
        foreach (var m in _markers)
        {
            var marker = m;
            column.Children.Add(new Tooltip(
                message: "Jump to the error",
                child: new GestureDetector(
                    new Row(spacing: Spacing.Sm, crossAxisAlignment: CrossAxisAlignment.Start, children: [
                        new IconGlyph(glyph: MaterialIcons.ErrorOutline, size: 16f, color: theme.Error),
                        new Label(text: $"{m.Line + 1}:{m.Column + 1}", style: AdwTypography.Monospace, color: theme.Accent),
                        new Expanded(new Label(text: m.Message, style: AdwTypography.Monospace, color: theme.OnSurface)),
                    ]),
                    onTap: () => _jump?.Invoke(marker)
                )
            ));
        }

        return new SingleChildScrollView {
            Child = new Padding(EdgeInsets.All(Spacing.Md), column),
        };
    }
}
