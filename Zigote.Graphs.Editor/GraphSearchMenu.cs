using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Graphs.Editor;

/// <summary>
///     Floating node search popup. Call <see cref="Show" /> to display it at a world position,
///     then subscribe to <see cref="NodeChosen" /> to receive the selected definition ID.
/// </summary>
public sealed class GraphSearchMenu : Widget
{
    private readonly Column _results;

    private readonly TextField _searchField;
    private readonly GraphEditorState _state;
    private readonly ThemeData _theme;
    private string _query = "";

    // ── Widget lifecycle ──────────────────────────────────────────────────────

    private Size _size;
    private float _spawnWorldX, _spawnWorldY;

    public GraphSearchMenu(GraphEditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;

        _searchField = new TextField(decoration: new InputDecoration("Search nodes…")) {
            OnChanged = q =>
            {
                _query = q;
                RefreshResults();
            },
        };
        _results = new Column {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };
    }

    public bool IsVisible { get; private set; }

    public event Action<string, float, float>? NodeChosen;

    public void Show(float worldX, float worldY)
    {
        _spawnWorldX = worldX;
        _spawnWorldY = worldY;
        IsVisible = true;
        _query = "";
        _searchField.Text = "";
        RefreshResults();
        App.Active?.RequestFocus(_searchField);
    }

    public void Hide() => IsVisible = false;

    private void RefreshResults()
    {
        _results.Children.Clear();
        var hits = _state.Registry.Search(query: _query, domainId: _state.Graph.DomainId)
            .Take(12)
            .ToList();

        foreach (var def in hits)
        {
            var d = def; // capture
            var row = new Button(
                label: def.DisplayName,
                onPressed: () =>
                {
                    NodeChosen?.Invoke(arg1: d.Id, arg2: _spawnWorldX, arg3: _spawnWorldY);
                    Hide();
                }
            ) {
                Padding = new EdgeInsets(
                    left: 8f,
                    top: 4f,
                    right: 8f,
                    bottom: 4f
                ),
                BackgroundColor = Color.Transparent,
                TextColor = _theme.OnSurface,
                FontSize = _theme.FontSizeBody,
            };
            _results.Children.Add(row);
        }

        if (hits.Count == 0)
        {
            _results.Children.Add(
                new Label(text: "No results", fontSize: _theme.FontSizeBody, color: _theme.Hint)
            );
        }
    }

    public override Size Measure(Constraints c)
    {
        if (!IsVisible) return _size = Size.Zero;
        _size = new Size(width: 260f, height: 320f);
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
        if (!IsVisible) return;
        _searchField.Measure(Constraints.Tight(width: _size.Width - 16f, height: 30f));
        _searchField.Layout(new Offset(x: origin.X + 8f, y: origin.Y + 8f));
        _results.Measure(Constraints.Tight(width: _size.Width, height: _size.Height - 46f));
        _results.Layout(new Offset(x: origin.X, y: origin.Y + 46f));
    }

    public override void Paint(PaintList paint)
    {
        if (!IsVisible) return;
        paint.AddShadow(
            bounds: Bounds,
            color: new Color(
                r: 0,
                g: 0,
                b: 0,
                a: 0.5f
            ),
            borderRadius: 8f,
            blurRadius: 20f
        );
        paint.AddRect(bounds: Bounds, color: _theme.Surface, radius: 8f);
        paint.AddBorder(bounds: Bounds, color: _theme.Border, radius: 8f);
        _searchField.Paint(paint);
        paint.AddClipStart(
            new Rect(
                x: Bounds.X,
                y: Bounds.Y + 46f,
                width: Bounds.Width,
                height: Bounds.Height - 46f
            )
        );
        _results.Paint(paint);
        paint.AddClipEnd();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!IsVisible || !Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _searchField.HitTest(point) ?? _results.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => IsVisible ? [_searchField, _results] : [];
}
