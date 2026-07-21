using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using AppInstance = Zigote.UI.Host.App;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets.Menu;

/// <summary>
///     Cross-platform in-window menu bar: a horizontal strip of menu titles that open
///     dropdowns (reusing <see cref="ContextMenu" />) on click. Used on every platform
///     unless a native backend takes ownership via <see cref="NativeMenuBar" />.
/// </summary>
public sealed class MenuBar : Widget
{
    private readonly Row _row = new() { CrossAxisAlignment = CrossAxisAlignment.Center };
    private float _height;
    private ThemeData _theme = ThemeData.Dark;
    private float _width;

    public MenuBar(AppInstance app, IReadOnlyList<AppMenu> menus)
    {
        foreach (var menu in menus)
        {
            Button btn = null!;
            btn = new Button(
                menu.Title,
                () =>
                {
                    var dropdown = new ContextMenu(app, [.. menu.Items]) {
                        // Dropdowns close when the pointer wanders off the menu (or its title
                        // button) — unlike right-click context menus, which wait for a click.
                        PointerExitScope = btn.Bounds,
                    };
                    dropdown.ShowAt(new Offset(btn.Bounds.X, btn.Bounds.Bottom));
                }
            ) {
                Style = ButtonStyle.Flat,
                FontSize = _theme.FontSizeBody,
                Padding = EdgeInsets.Symmetric(10f, 7f),
            };
            _row.Children.Add(btn);
        }
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var row = _row.Measure(c);
        _width = float.IsFinite(c.MaxWidth) ? c.MaxWidth : row.Width;
        _height = row.Height;
        return c.Constrain(new Size(_width, _height));
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _width,
            _height
        );
        _row.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, _theme.Surface);
        _row.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _row.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_row);
    }
}