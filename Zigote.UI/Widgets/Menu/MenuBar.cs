using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using AppInstance = Zigote.UI.Host.App;

namespace Zigote.UI.Widgets.Menu;

/// <summary>
///     Cross-platform in-window menu bar: a horizontal strip of menu titles that open
///     dropdowns (reusing <see cref="ContextMenu" />) on click. Used on every platform
///     unless a native backend takes ownership via <see cref="NativeMenuBar" />.
/// </summary>
public sealed class MenuBar : Widget
{
    private readonly Row _row = new() { CrossAxisAlignment = CrossAxisAlignment.Center };

    // Titles keep their intrinsic widths and the strip scrolls when they don't all fit: a narrow
    // window (or a phone) would otherwise push the trailing menus off the edge, unreachable. Nothing
    // to scroll on a wide window, so it stays a plain row there.
    private readonly ScrollView _scroller;
    private float _height;
    private ThemeData _theme = ThemeData.Dark;
    private float _width;

    public MenuBar(AppInstance app, IReadOnlyList<AppMenu> menus)
    {
        foreach (var menu in menus)
        {
            Button btn = null!;
            btn = new Button(
                label: menu.Title,
                onPressed: () =>
                {
                    var dropdown = new ContextMenu(app: app, items: [.. menu.Items]) {
                        // Dropdowns close when the pointer wanders off the menu (or its title
                        // button) — unlike right-click context menus, which wait for a click.
                        PointerExitScope = btn.Bounds,
                    };
                    dropdown.ShowAt(new Offset(x: btn.Bounds.X, y: btn.Bounds.Bottom));
                }
            ) {
                Style = ButtonStyle.Flat,
                FontSize = _theme.FontSizeBody,
                Padding = EdgeInsets.Symmetric(horizontal: 10f, vertical: 7f),
            };
            _row.Children.Add(btn);
        }

        _scroller = new ScrollView(_row) {
            ScrollHorizontal = true,
            ScrollVertical = false,
        };
    }

    /// <summary>
    ///     Strip background. Null = the theme surface (its own strip); set it to the host's
    ///     colour when the bar is embedded in something else, e.g. a GNOME headerbar.
    /// </summary>
    public Color? Background { get; set; }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var row = _row.Measure(c);
        _width = float.IsFinite(c.MaxWidth) ? c.MaxWidth : row.Width;
        _height = row.Height;
        // Hand the scroller a tight box — it fills whatever it is given, and the bar is exactly
        // one row tall.
        _scroller.Measure(Constraints.Tight(width: _width, height: _height));
        return c.Constrain(new Size(width: _width, height: _height));
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _width,
            height: _height
        );
        _scroller.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(bounds: Bounds, color: Background ?? _theme.Surface);
        _scroller.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _scroller.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_scroller);
}
