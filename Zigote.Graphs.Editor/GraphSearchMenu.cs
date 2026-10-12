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
    private int VisibleRows => Math.Max(0, (int)((_size.Height - RowsTop - FooterH) / RowH));
    private Rect RowsBounds => new(Bounds.X + Spacing.Xs, Bounds.Y + RowsTop,
        MathF.Max(0, Bounds.Width - Spacing.Xs * 2f), VisibleRows * RowH);

    public GraphSearchMenu(GraphEditorState state, ThemeData theme)
    {
        _state = state; _theme = theme;
        _field = new SearchField(this);
        _field.OnChanged = _ => Refresh();
    }
    public bool IsVisible { get; private set; }
    public string? ChosenPinId { get; private set; }
    public event Action<string, float, float>? NodeChosen;
    public event Action? Closed;
    public event Action? VisibilityChanged;

    public void Show(float worldX, float worldY, GraphPinEndpoint? connection = null)
    {
        _worldX = worldX; _worldY = worldY; _connection = connection;
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
        _hits.Clear(); _selected = _first = 0;
        foreach (var def in _state.Registry.Search(_field.Text, _state.Graph.DomainId).OrderBy(d => d.Category).ThenBy(d => d.DisplayName))
        {
            if (_connection is not { } source) { _hits.Add((def, null, def.Category)); continue; }
            var resolved = GraphConnections.Resolve(_state.Graph, _state.Registry, source);
            if (resolved is null) continue;
            var candidate = new GraphNode { DefinitionId = def.Id };
            var preview = GraphConnections.WithCandidate(_state.Graph, candidate);
            var pins = resolved.Value.Direction == PinDirection.Output ? def.Inputs : def.Outputs;
            foreach (var pin in pins)
                if (GraphConnections.CanConnect(preview, _state.Registry, source,
                    new GraphPinEndpoint(candidate.Id, pin.Id), out _))
                    _hits.Add((def, pin.Id, def.Category + "  ·  " + pin.DisplayName));
        }
        MarkNeedsPaint();
    }
    private void Choose(int index)
    {
        if (index < 0 || index >= _hits.Count) return;
        var hit = _hits[index]; ChosenPinId = hit.PinId;
        NodeChosen?.Invoke(hit.Definition.Id, _worldX, _worldY);
        Hide();
    }
    private void MoveSelection(int delta)
    {
        _selected = Math.Clamp(_selected + delta, 0, Math.Max(0, _hits.Count - 1));
        if (_selected < _first) _first = _selected;
        if (VisibleRows > 0 && _selected >= _first + VisibleRows) _first = _selected - VisibleRows + 1;
        MarkNeedsPaint();
    }
    public override Size Measure(Constraints c) => _size = c.Constrain(new Size(340f, 420f));
    public override void Layout(Offset origin)
    {
        Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
        _first = Math.Clamp(_first, 0, Math.Max(0, _hits.Count - VisibleRows));
        _field.Measure(Constraints.Tight(Math.Max(0, _size.Width - 20f), 32f));
        _field.Layout(new Offset(origin.X + 10f, origin.Y + 32f));
    }
    public override void Paint(PaintList paint)
    {
        if (!IsVisible) return;
        paint.AddElevation(Bounds, Radii.Lg, Elevation.Z2);
        paint.AddRect(Bounds, _theme.Surface, Radii.Lg);
        paint.AddBorder(Bounds, _theme.Separator, Radii.Lg);
        paint.AddClipStart(Bounds);
        paint.AddText(_connection.HasValue ? "Add connected node" : "Add node", Bounds.X + 12f, Bounds.Y + 22f, _theme.OnSurface, _theme.FontSizeBody);
        _field.Paint(paint);
        var rows = RowsBounds;
        paint.AddClipStart(rows);
        for (int i = _first; i < Math.Min(_hits.Count, _first + VisibleRows); i++)
        {
            float y = rows.Y + (i - _first) * RowH;
            bool selected = i == _selected;
            if (selected) paint.AddRect(new Rect(rows.X, y, rows.Width, RowH - 2f), _theme.Selection, Radii.Xs);
            paint.AddText(_hits[i].Definition.DisplayName, rows.X + Spacing.Sm, y + 18f,
                selected ? _theme.OnPrimary : _theme.OnSurface, _theme.FontSizeBody);
            paint.AddText(_hits[i].Subtitle, rows.X + Spacing.Sm, y + 34f,
                selected ? _theme.OnPrimary : _theme.TextMuted, _theme.FontSizeCaption);
        }
        if (_hits.Count == 0) paint.AddText(_connection.HasValue ? "No compatible nodes found" : "No nodes found", rows.X + 10f, rows.Y + 24f, _theme.TextMuted, 12f);
        paint.AddClipEnd();
        paint.AddRect(new Rect(Bounds.X + Spacing.Sm, Bounds.Bottom - FooterH, MathF.Max(0, Bounds.Width - Spacing.Sm * 2), 1f), _theme.Separator);
        paint.AddText($"↑ ↓ Select   Enter Add   Esc Cancel   ·   {_hits.Count} results", Bounds.X + 12f, Bounds.Bottom - 8f, _theme.TextMuted, _theme.FontSizeCaption);
        paint.AddClipEnd();
    }
    private int HitRow(Offset point)
    {
        if (!RowsBounds.Contains(point.X, point.Y)) return -1;
        int index = _first + (int)((point.Y - RowsBounds.Y) / RowH);
        return index < _hits.Count ? index : -1;
    }
    public override void OnPointerDown(Offset point) => Choose(HitRow(point));
    public override void OnPointerMove(Offset point)
    {
        int index = HitRow(point);
        if (index >= 0) { _selected = index; MarkNeedsPaint(); }
    }
    public override void OnScroll(float dx, float dy)
    {
        if (VisibleRows == 0 || _hits.Count == 0) return;
        _first = Math.Clamp(_first - (int)MathF.Sign(dy) * 2, 0, Math.Max(0, _hits.Count - VisibleRows));
        _selected = Math.Clamp(_selected, _first, Math.Min(_hits.Count - 1, _first + VisibleRows - 1));
        MarkNeedsPaint();
    }
    public override Widget? HitTest(Offset point) => !IsVisible || !Bounds.Contains(point.X, point.Y)
        ? null : _field.HitTest(point) ?? this;
    public override IEnumerable<Widget> GetChildren() => IsVisible ? [_field] : [];

    private sealed class SearchField(GraphSearchMenu owner) : AdwTextField(decoration: new AdwInputDecoration(hintText: "Search nodes…"))
    {
        public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
        {
            if (down)
            {
                if (scancode == 41) { owner.Hide(); return; }
                if (scancode == 40 || scancode == 88) { owner.Choose(owner._selected); return; }
                if (scancode == 75) { owner.MoveSelection(-Math.Max(1, owner.VisibleRows)); return; }
                if (scancode == 78) { owner.MoveSelection(Math.Max(1, owner.VisibleRows)); return; }
                if (scancode == 81) { owner.MoveSelection(1); return; }
                if (scancode == 82) { owner.MoveSelection(-1); return; }
            }
            base.OnKey(keyChar, scancode, down, mods);
        }
    }
}
