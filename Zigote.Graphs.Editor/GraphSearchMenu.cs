using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Graphs.Editor;

/// <summary>Keyboard-first node search, optionally constrained to a dangling cable.</summary>
public sealed class GraphSearchMenu : Widget
{
    private const float RowH = ControlMetrics.MenuRowHeight * 2f;
    private const float RowsTop = 72f;
    private const float FooterH = 24f;
    private readonly SearchField _field;
    private readonly GraphEditorState _state;
    private readonly ThemeData _theme;
    private readonly List<(NodeDefinition Definition, string? PinId, string Subtitle)> _hits = [];
    private GraphPinEndpoint? _connection;
    private float _worldX, _worldY;
    private int _selected, _first;
    private Size _size;

    private int VisibleRows => Math.Max(
        val1: 0,
        val2: (int)((_size.Height - RowsTop - FooterH) / RowH)
    );

    private Rect RowsBounds => new(
        x: Bounds.X + Spacing.Xs,
        y: Bounds.Y + RowsTop,
        width: MathF.Max(x: 0, y: Bounds.Width - (Spacing.Xs * 2f)),
        height: VisibleRows * RowH
    );

    public GraphSearchMenu(GraphEditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;
        _field = new SearchField(this) { OnChanged = _ => Refresh() };
    }

    public bool IsVisible { get; private set; }
    public string? ChosenPinId { get; private set; }
    public event Action<string, float, float>? NodeChosen;
    public event Action? Closed;
    public event Action? VisibilityChanged;

    public void Show(float worldX, float worldY, GraphPinEndpoint? connection = null)
    {
        _worldX = worldX;
        _worldY = worldY;
        _connection = connection;
        ChosenPinId = null;
        _field.Text = "";
        IsVisible = true;
        Refresh();
        VisibilityChanged?.Invoke();
        App.Active?.RequestFocus(_field);
    }

    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        Closed?.Invoke();
        VisibilityChanged?.Invoke();
    }

    private void Refresh()
    {
        _hits.Clear();
        _selected = _first = 0;
        foreach (var def in _state.Registry
                     .Search(query: _field.Text, domainId: _state.Graph.DomainId)
                     .OrderBy(d => d.Category).ThenBy(d => d.DisplayName))
        {
            if (_connection is not { } source)
            {
                _hits.Add((def, null, def.Category));
                continue;
            }

            var resolved = GraphConnections.Resolve(
                graph: _state.Graph,
                registry: _state.Registry,
                endpoint: source
            );
            if (resolved is null) continue;
            var candidate = new GraphNode { DefinitionId = def.Id };
            var preview = GraphConnections.WithCandidate(graph: _state.Graph, candidate: candidate);
            var pins = resolved.Value.Direction == PinDirection.Output ? def.Inputs : def.Outputs;
            foreach (var pin in pins)
            {
                if (GraphConnections.CanConnect(
                        graph: preview,
                        registry: _state.Registry,
                        a: source,
                        b: new GraphPinEndpoint(NodeId: candidate.Id, PinId: pin.Id),
                        reason: out _
                    ))
                    _hits.Add((def, pin.Id, def.Category + "  ·  " + pin.DisplayName));
            }
        }

        MarkNeedsPaint();
    }

    private void Choose(int index)
    {
        if (index < 0 || index >= _hits.Count) return;
        var hit = _hits[index];
        ChosenPinId = hit.PinId;
        NodeChosen?.Invoke(arg1: hit.Definition.Id, arg2: _worldX, arg3: _worldY);
        Hide();
    }

    private void MoveSelection(int delta)
    {
        _selected = Math.Clamp(
            value: _selected + delta,
            min: 0,
            max: Math.Max(val1: 0, val2: _hits.Count - 1)
        );
        if (_selected < _first) _first = _selected;
        if (VisibleRows > 0 && _selected >= _first + VisibleRows)
            _first = _selected - VisibleRows + 1;
        MarkNeedsPaint();
    }

    public override Size Measure(Constraints c) =>
        _size = c.Constrain(new Size(width: 340f, height: 420f));

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _first = Math.Clamp(
            value: _first,
            min: 0,
            max: Math.Max(val1: 0, val2: _hits.Count - VisibleRows)
        );
        _field.Measure(
            Constraints.Tight(width: Math.Max(val1: 0, val2: _size.Width - 20f), height: 32f)
        );
        _field.Layout(new Offset(x: origin.X + 10f, y: origin.Y + 32f));
    }

    public override void Paint(PaintList paint)
    {
        if (!IsVisible) return;
        paint.AddElevation(bounds: Bounds, radius: Radii.Lg, style: Elevation.Z2);
        paint.AddRect(bounds: Bounds, color: _theme.Surface, radius: Radii.Lg);
        paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: Radii.Lg);
        paint.AddClipStart(Bounds);
        paint.AddText(
            text: _connection.HasValue ? "Add connected node" : "Add node",
            baselineX: Bounds.X + 12f,
            baselineY: Bounds.Y + 22f,
            color: _theme.OnSurface,
            fontSize: _theme.FontSizeBody
        );
        _field.Paint(paint);
        var rows = RowsBounds;
        paint.AddClipStart(rows);
        for (int i = _first; i < Math.Min(val1: _hits.Count, val2: _first + VisibleRows); i++)
        {
            float y = rows.Y + ((i - _first) * RowH);
            bool selected = i == _selected;
            if (selected)
                paint.AddRect(
                    bounds: new Rect(
                        x: rows.X,
                        y: y,
                        width: rows.Width,
                        height: RowH - 2f
                    ),
                    color: _theme.Selection,
                    radius: Radii.Xs
                );
            paint.AddText(
                text: _hits[i].Definition.DisplayName,
                baselineX: rows.X + Spacing.Sm,
                baselineY: y + 18f,
                color: selected ? _theme.OnPrimary : _theme.OnSurface,
                fontSize: _theme.FontSizeBody
            );
            paint.AddText(
                text: _hits[i].Subtitle,
                baselineX: rows.X + Spacing.Sm,
                baselineY: y + 34f,
                color: selected ? _theme.OnPrimary : _theme.TextMuted,
                fontSize: _theme.FontSizeCaption
            );
        }

        if (_hits.Count == 0)
            paint.AddText(
                text: _connection.HasValue ? "No compatible nodes found" : "No nodes found",
                baselineX: rows.X + 10f,
                baselineY: rows.Y + 24f,
                color: _theme.TextMuted,
                fontSize: 12f
            );
        paint.AddClipEnd();
        paint.AddRect(
            bounds: new Rect(
                x: Bounds.X + Spacing.Sm,
                y: Bounds.Bottom - FooterH,
                width: MathF.Max(x: 0, y: Bounds.Width - (Spacing.Sm * 2)),
                height: 1f
            ),
            color: _theme.Separator
        );
        paint.AddText(
            text: $"↑ ↓ Select   Enter Add   Esc Cancel   ·   {_hits.Count} results",
            baselineX: Bounds.X + 12f,
            baselineY: Bounds.Bottom - 8f,
            color: _theme.TextMuted,
            fontSize: _theme.FontSizeCaption
        );
        paint.AddClipEnd();
    }

    private int HitRow(Offset point)
    {
        if (!RowsBounds.Contains(px: point.X, py: point.Y)) return -1;
        int index = _first + (int)((point.Y - RowsBounds.Y) / RowH);
        return index < _hits.Count ? index : -1;
    }

    public override void OnPointerDown(Offset point) => Choose(HitRow(point));

    public override void OnPointerMove(Offset point)
    {
        int index = HitRow(point);
        if (index >= 0)
        {
            _selected = index;
            MarkNeedsPaint();
        }
    }

    public override void OnScroll(float dx, float dy)
    {
        if (VisibleRows == 0 || _hits.Count == 0) return;
        _first = Math.Clamp(
            value: _first - ((int)MathF.Sign(dy) * 2),
            min: 0,
            max: Math.Max(val1: 0, val2: _hits.Count - VisibleRows)
        );
        _selected = Math.Clamp(
            value: _selected,
            min: _first,
            max: Math.Min(val1: _hits.Count - 1, val2: _first + VisibleRows - 1)
        );
        MarkNeedsPaint();
    }

    public override Widget? HitTest(Offset point) =>
        !IsVisible || !Bounds.Contains(px: point.X, py: point.Y)
            ? null
            : _field.HitTest(point) ?? this;

    public override IEnumerable<Widget> GetChildren() => IsVisible ? [_field] : [];

    private sealed class SearchField(GraphSearchMenu owner) : AdwTextField(
        decoration: new AdwInputDecoration(hintText: "Search nodes…")
    )
    {
        public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
        {
            if (down)
            {
                if (scancode == 41)
                {
                    owner.Hide();
                    return;
                }

                if (scancode == 40 || scancode == 88)
                {
                    owner.Choose(owner._selected);
                    return;
                }

                if (scancode == 75)
                {
                    owner.MoveSelection(-Math.Max(val1: 1, val2: owner.VisibleRows));
                    return;
                }

                if (scancode == 78)
                {
                    owner.MoveSelection(Math.Max(val1: 1, val2: owner.VisibleRows));
                    return;
                }

                if (scancode == 81)
                {
                    owner.MoveSelection(1);
                    return;
                }

                if (scancode == 82)
                {
                    owner.MoveSelection(-1);
                    return;
                }
            }

            base.OnKey(
                keyChar: keyChar,
                scancode: scancode,
                down: down,
                mods: mods
            );
        }
    }
}
