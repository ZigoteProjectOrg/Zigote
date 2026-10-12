using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using AppInstance = Zigote.UI.Host.App;

namespace Zigote.Graphs.Editor;

/// <summary>
///     Full-featured graph editor panel.
///     Composes <see cref="GraphCanvas" />, <see cref="GraphInspectorPanel" />, a toolbar,
///     and the floating <see cref="GraphSearchMenu" />.
/// </summary>
public sealed class GraphEditorPanel : Widget
{
    private const float ToolbarH = 38f;
    private const float DefaultSplitRatio = 0.75f;
    private readonly AppInstance _app;

    private readonly GraphCanvas _canvas;
    private readonly GraphInspectorPanel _inspector;
    private readonly Widget _root;
    private readonly GraphSearchMenu _search;
    private readonly SplitPane _split;

    // Status label — mutated reactively when compile result changes.
    private readonly Label _statusLabel;
    private readonly ThemeData _theme;

    private ToolbarButton? _inspectorButton;
    private Size _size;

    public GraphEditorPanel(GraphDocument graph, GraphDomainRegistry registry, ThemeData theme,
        AppInstance app,
        float inspectorWidth = 220f, Action<object?>? onCompiled = null,
        Widget? inspectorHeader = null, bool showToolbar = true, bool inspectorVisible = true)
    {
        _theme = theme;
        _app = app;
        State = new GraphEditorState(graph: graph, registry: registry);

        _canvas = new GraphCanvas(state: State, theme: theme);
        _inspector = new GraphInspectorPanel(state: State, theme: theme);
        _search = new GraphSearchMenu(state: State, theme: theme);

        _statusLabel = new Label(text: "", fontSize: theme.FontSizeCaption, color: theme.Hint);
        UpdateStatus();

        State.CompileChanged += UpdateStatus;
        // Surface the compiled artifact to callers (e.g., apply a shader graph onto a material).
        if (onCompiled is not null)
        {
            State.CompileChanged += () => { if (State.LastCompileResult?.Success == true) onCompiled(State.LastCompileResult.CompiledArtifact); };
            if (State.LastCompileResult?.Success == true) onCompiled(State.LastCompileResult.CompiledArtifact);
        }


        // Feature 6: use SplitPane instead of a fixed-width SizedBox for the inspector
        Widget inspectorSide =
            new ScrollView(new Padding(padding: EdgeInsets.All(8f), child: _inspector));
        // An optional header (e.g. a live material preview) pinned above the scrollable inspector.
        if (inspectorHeader is not null)
        {
            inspectorSide = new Column {
                CrossAxisAlignment = CrossAxisAlignment.Stretch,
                Children = {
                    inspectorHeader,
                    new Expanded(inspectorSide),
                },
            };
        }

        _split = new SplitPane(theme: theme, first: _canvas, second: inspectorSide) {
            SplitRatio = DefaultSplitRatio,
            DividerW = 4f,
            SecondVisible = inspectorVisible,
        };

        // A host that already has its own chrome (an app header bar, a docked panel) can drop the
        // editor's toolbar and keep only the canvas + inspector.
        _root = showToolbar
            ? new Column {
                MainAxisAlignment = MainAxisAlignment.Start,
                CrossAxisAlignment = CrossAxisAlignment.Stretch,
                Children = {
                    new SizedBox(height: ToolbarH, child: BuildToolbar()),
                    new Expanded(_split),
                },
            }
            : _split;

        _canvas.SearchRequested += (wx, wy) => _search.Show(wx, wy, _canvas.SearchConnection);
        _search.VisibilityChanged += MarkNeedsLayout;
        _search.Closed += () => { _canvas.CancelNodeSearch(); _app.RequestFocus(_canvas); };
        _search.NodeChosen += (defId, wx, wy) =>
            _canvas.AddNodeFromSearch(defId, _search.ChosenPinId, wx, wy);
    }

    public GraphEditorState State { get; }

    /// <summary>Controls the entire inspector, including its optional header and divider.</summary>
    public bool InspectorVisible
    {
        get => _split.SecondVisible;
        set
        {
            _split.SecondVisible = value;
            if (_inspectorButton is not null)
            {
                _inspectorButton.Tone = value ? ToolbarTone.Primary : ToolbarTone.Default;
                _inspectorButton.MarkNeedsPaint();
            }
            MarkNeedsLayout();
        }
    }

    // ── Reactive status ───────────────────────────────────────────────────────

    private void UpdateStatus()
    {
        var result = State.LastCompileResult;
        if (result is null)
        {
            _statusLabel.Text = "⟳ pending";
            _statusLabel.Color = _theme.Hint;
            return;
        }

        if (result.Success)
        {
            int warnCount =
                result.Diagnostics.Count(d => d.Severity == GraphDiagnosticSeverity.Warning);
            _statusLabel.Text = warnCount > 0 ? $"✓ Built  ({warnCount} warning)" : "✓ Built";
            _statusLabel.Color = warnCount > 0 ? _theme.Accent : _theme.Success;
        }
        else
        {
            int errCount =
                result.Diagnostics.Count(d => d.Severity == GraphDiagnosticSeverity.Error);
            _statusLabel.Text = result.Diagnostics.FirstOrDefault(d => d.Severity == GraphDiagnosticSeverity.Error)?.Message ?? $"{errCount} errors";
            _statusLabel.Color = _theme.Error;
        }
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private Widget BuildToolbar()
    {
        ToolbarButton Tb(string icon, string? label, Action? onClick,
            ToolbarTone tone = ToolbarTone.Default) =>
            new(icon: icon, onPressed: onClick, label: label) { Tone = tone };

        var undoBtn = Tb(icon: Icons.Undo, label: null, onClick: () => State.Commands.Undo());
        var redoBtn = Tb(icon: Icons.Redo, label: null, onClick: () => State.Commands.Redo());
        var frameBtn = Tb(
            icon: Icons.Fullscreen,
            label: "Frame all",
            onClick: () => { _canvas.FrameAll(); _canvas.MarkNeedsLayout(); }
        );

        // View toggles — a Primary tone reflects the "on" state (minimap off by default; inspector on).
        ToolbarButton minimapBtn = null!;
        minimapBtn = Tb(icon: Icons.Map, label: "Minimap", onClick: null);
        minimapBtn.OnPressed = () =>
        {
            _canvas.MinimapVisible = !_canvas.MinimapVisible;
            minimapBtn.Tone = _canvas.MinimapVisible ? ToolbarTone.Primary : ToolbarTone.Default;
            minimapBtn.MarkNeedsPaint();
            _canvas.MarkNeedsPaint();
        };

        ToolbarButton inspectorBtn = null!;
        inspectorBtn = Tb(
            icon: Icons.Tune,
            label: "Inspector",
            onClick: null,
            tone: ToolbarTone.Primary
        );
        inspectorBtn.OnPressed = () =>
        {
            InspectorVisible = !InspectorVisible;
        };

        _inspectorButton = inspectorBtn;
        inspectorBtn.Tone = InspectorVisible ? ToolbarTone.Primary : ToolbarTone.Default;

        return new ColoredBox(
            color: _theme.Surface,
            child: new Padding(
                padding: EdgeInsets.Symmetric(horizontal: 8f, vertical: 2f),
                child: new Row {
                    MainAxisAlignment = MainAxisAlignment.Start,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = {
                        Tb(Icons.Add, "Add node", () => _canvas.OpenNodeSearch()),
                        new SizedBox(12f),
                        undoBtn,
                        new SizedBox(2f),
                        redoBtn,
                        new SizedBox(10f),
                        frameBtn,
                        new SizedBox(2f),
                        new SizedBox(10f),
                        minimapBtn,
                        new SizedBox(2f),
                        inspectorBtn,
                        new SizedBox(12f),
                        new Expanded(_statusLabel),
                    },
                }
            )
        );
    }

    // ── Widget lifecycle ──────────────────────────────────────────────────────

    /// <summary>See <see cref="GraphCanvas.AutoFrame" />.</summary>
    public bool AutoFrame
    {
        get => _canvas.AutoFrame;
        set => _canvas.AutoFrame = value;
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        _root.Measure(Constraints.Tight(width: _size.Width, height: _size.Height));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _root.Layout(origin);

        if (_search.IsVisible)
        {
            var cb = _canvas.Bounds;
            float width = MathF.Max(0f, MathF.Min(340f, cb.Width - 16f)), height = MathF.Max(0f, MathF.Min(420f, cb.Height - 16f));
            float sx = Math.Clamp(_canvas.SearchAnchor.X, cb.X + 8f, MathF.Max(cb.X + 8f, cb.Right - width - 8f));
            float sy = Math.Clamp(_canvas.SearchAnchor.Y, cb.Y + 8f, MathF.Max(cb.Y + 8f, cb.Bottom - height - 8f));
            _search.Measure(Constraints.Tight(width, height));
            _search.Layout(new Offset(x: sx, y: sy));
        }
    }

    public override void Paint(PaintList paint)
    {
        _root.Paint(paint);
        if (_search.IsVisible) _search.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        if (_search.IsVisible)
        {
            var hit = _search.HitTest(point);
            return hit ?? this; // modal: dismiss on a click, never on hover/hit-testing
        }

        return _root.HitTest(point);
    }

    public override void OnPointerDown(Offset point)
    {
        if (_search.IsVisible && !_search.Bounds.Contains(point.X, point.Y)) _search.Hide();
    }

    public override IEnumerable<Widget> GetChildren()
    {
        yield return _root;
        if (_search.IsVisible) yield return _search;
    }
}
