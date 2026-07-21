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

    public void Hide()
    {
        IsVisible = false;
    }

    private void RefreshResults()
    {
        _results.Children.Clear();
        var hits = _state.Registry.Search(_query, _state.Graph.DomainId)
            .Take(12)
            .ToList();

        foreach (var def in hits)
        {
            var d = def; // capture
            var row = new Button(
                def.DisplayName,
                () =>
                {
                    NodeChosen?.Invoke(d.Id, _spawnWorldX, _spawnWorldY);
                    Hide();
                }
            ) {
                Padding = new EdgeInsets(
                    8f,
                    4f,
                    8f,
                    4f
                ),
                BackgroundColor = Color.Transparent,
                TextColor = _theme.OnSurface,
                FontSize = _theme.FontSizeBody,
            };
            _results.Children.Add(row);
        }

        if (hits.Count == 0)
            _results.Children.Add(new Label("No results", _theme.FontSizeBody, _theme.Hint));
    }

    public override Size Measure(Constraints c)
    {
        if (!IsVisible) return _size = Size.Zero;
        _size = new Size(260f, 320f);
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
        if (!IsVisible) return;
        _searchField.Measure(Constraints.Tight(_size.Width - 16f, 30f));
        _searchField.Layout(new Offset(origin.X + 8f, origin.Y + 8f));
        _results.Measure(Constraints.Tight(_size.Width, _size.Height - 46f));
        _results.Layout(new Offset(origin.X, origin.Y + 46f));
    }

    public override void Paint(PaintList paint)
    {
        if (!IsVisible) return;
        paint.AddShadow(
            Bounds,
            new Color(
                0,
                0,
                0,
                0.5f
            ),
            8f,
            20f
        );
        paint.AddRect(Bounds, _theme.Surface, 8f);
        paint.AddBorder(Bounds, _theme.Border, 8f);
        _searchField.Paint(paint);
        paint.AddClipStart(
            new Rect(
                Bounds.X,
                Bounds.Y + 46f,
                Bounds.Width,
                Bounds.Height - 46f
            )
        );
        _results.Paint(paint);
        paint.AddClipEnd();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!IsVisible || !Bounds.Contains(point.X, point.Y)) return null;
        return _searchField.HitTest(point) ?? _results.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return IsVisible ? [_searchField, _results] : [];
    }
}