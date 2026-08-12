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
    private const float ToolbarH = 32f;
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

    private bool _inspectorVisible = true;
    private Size _size;

    public GraphEditorPanel(GraphDocument graph, GraphDomainRegistry registry, ThemeData theme,
        AppInstance app,
        float inspectorWidth = 220f, Action<object?>? onCompiled = null,
        Widget? inspectorHeader = null, bool showToolbar = true)
    {
        _theme = theme;
        _app = app;
        State = new GraphEditorState(graph, registry);

        _canvas = new GraphCanvas(State, theme);
        _inspector = new GraphInspectorPanel(State, theme);
        _search = new GraphSearchMenu(State, theme);

        _statusLabel = new Label("", theme.FontSizeCaption, theme.Hint);
        UpdateStatus();

        State.CompileChanged += UpdateStatus;
        // Surface the compiled artifact to callers (e.g., apply a shader graph onto a material).
        if (onCompiled is not null)
        {
            State.CompileChanged += () => onCompiled(State.LastCompileResult?.CompiledArtifact);
            onCompiled(State.LastCompileResult?.CompiledArtifact); // initial value
        }


        // Feature 6: use SplitPane instead of a fixed-width SizedBox for the inspector
        Widget inspectorSide = new ScrollView(new Padding(EdgeInsets.All(8f), _inspector));
        // An optional header (e.g. a live material preview) pinned above the scrollable inspector.
        if (inspectorHeader is not null)
            inspectorSide = new Column {
                CrossAxisAlignment = CrossAxisAlignment.Stretch,
                Children = {
                    inspectorHeader,
                    new Expanded(inspectorSide),
                },
            };

        _split = new SplitPane(theme, _canvas, inspectorSide) {
            SplitRatio = DefaultSplitRatio,
            DividerW = 4f,
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

        _canvas.SearchRequested += (wx, wy) => _search.Show(wx, wy);
        _search.NodeChosen += (defId, wx, wy) => _canvas.AddNode(defId, wx, wy);
    }

    public GraphEditorState State { get; }

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
            var warnCount =
                result.Diagnostics.Count(d => d.Severity == GraphDiagnosticSeverity.Warning);
            _statusLabel.Text = warnCount > 0 ? $"✓ Built  ({warnCount} warning)" : "✓ Built";
            _statusLabel.Color = warnCount > 0 ? _theme.Accent : _theme.Success;
        }
        else
        {
            var errCount =
                result.Diagnostics.Count(d => d.Severity == GraphDiagnosticSeverity.Error);
            _statusLabel.Text = $"✗ {errCount} error(s)";
            _statusLabel.Color = _theme.Error;
        }
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private Widget BuildToolbar()
    {
        ToolbarButton Tb(string icon, string? label, Action? onClick,
            ToolbarTone tone = ToolbarTone.Default)
        {
            return new ToolbarButton(icon, onClick, label) { Tone = tone };
        }

        var undoBtn = Tb(Icons.Undo, null, () => State.Commands.Undo());
        var redoBtn = Tb(Icons.Redo, null, () => State.Commands.Redo());
        var frameBtn = Tb(Icons.Fullscreen, "Frame", () => _canvas.FrameAll());

        var validateBtn = Tb(
            Icons.CheckCircle,
            "Validate",
            () =>
            {
                var result = State.Validate();
                _app.ShowSnackbar(
                    result.IsValid
                        ? "Graph is valid"
                        : $"{result.Diagnostics.Count(d => d.Severity == GraphDiagnosticSeverity.Error)} error(s)"
                );
            }
        );

        // View toggles — a Primary tone reflects the "on" state (minimap off by default; inspector on).
        ToolbarButton minimapBtn = null!;
        minimapBtn = Tb(Icons.Map, "Minimap", null);
        minimapBtn.OnPressed = () =>
        {
            _canvas.MinimapVisible = !_canvas.MinimapVisible;
            minimapBtn.Tone = _canvas.MinimapVisible ? ToolbarTone.Primary : ToolbarTone.Default;
            minimapBtn.MarkNeedsPaint();
            _canvas.MarkNeedsPaint();
        };

        ToolbarButton inspectorBtn = null!;
        inspectorBtn = Tb(
            Icons.Tune,
            "Inspector",
            null,
            ToolbarTone.Primary
        );
        inspectorBtn.OnPressed = () =>
        {
            _inspectorVisible = !_inspectorVisible;
            _split.SplitRatio = _inspectorVisible ? DefaultSplitRatio : 1.0f;
            _split.MarkNeedsLayout();
            inspectorBtn.Tone = _inspectorVisible ? ToolbarTone.Primary : ToolbarTone.Default;
            inspectorBtn.MarkNeedsPaint();
        };

        var domain = State.Registry.TryGetDomain(State.Graph.DomainId, out var d)
            ? d?.DisplayName
            : null;
        var titleLabel = new Label(
            $"{State.Graph.Name}  [{domain ?? State.Graph.DomainId}]",
            _theme.FontSizeCaption,
            _theme.Hint
        );

        return new ColoredBox(
            _theme.Surface,
            new Padding(
                EdgeInsets.Symmetric(8f, 2f),
                new Row {
                    MainAxisAlignment = MainAxisAlignment.Start,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = {
                        titleLabel,
                        new SizedBox(14f),
                        undoBtn,
                        new SizedBox(2f),
                        redoBtn,
                        new SizedBox(10f),
                        frameBtn,
                        new SizedBox(2f),
                        validateBtn,
                        new SizedBox(10f),
                        minimapBtn,
                        new SizedBox(2f),
                        inspectorBtn,
                        new SizedBox(12f),
                        _statusLabel,
                    },
                }
            )
        );
    }

    // ── Widget lifecycle ──────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
        _root.Measure(Constraints.Tight(_size.Width, _size.Height));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _root.Layout(origin);

        if (_search.IsVisible)
        {
            var cb = _canvas.Bounds;
            var sx = cb.X + (cb.Width - 260f) * 0.5f;
            var sy = cb.Y + (cb.Height - 320f) * 0.35f;
            _search.Measure(Constraints.Tight(260f, 320f));
            _search.Layout(new Offset(sx, sy));
        }
    }

    public override void Paint(PaintList paint)
    {
        _root.Paint(paint);
        if (_search.IsVisible) _search.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        if (_search.IsVisible)
        {
            var hit = _search.HitTest(point);
            if (hit is not null) return hit;
            if (!_search.Bounds.Contains(point.X, point.Y))
                _search.Hide();
        }

        return _root.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        yield return _root;
        if (_search.IsVisible) yield return _search;
    }
}