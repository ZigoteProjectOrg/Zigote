using Zigote.Core;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Layout;
using LayoutPadding = Zigote.UI.Widgets.Layout.Padding;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     A flat, macOS-style content surface: an opaque <see cref="ThemeData.Surface" /> fill at the
///     theme card radius, an optional hairline <see cref="ThemeData.Separator" /> border, and a soft
///     elevation shadow. Cards are content, not floating chrome — Liquid Glass is intentionally not
///     used here. Composed from a <see cref="DecoratedBox" /> + <see cref="LayoutPadding" />.
/// </summary>
public class Card(Widget? child = null) : ComposedWidget
{
    private bool _bordered = true;
    private Widget? _child = child;
    private Color? _color;
    private float _elevation = 4f;
    private EdgeInsets? _padding;
    private float? _radius;

    public Widget? Child
    {
        get => _child;
        set
        {
            _child = value;
            Invalidate();
        }
    }

    public EdgeInsets? Padding
    {
        get => _padding;
        set
        {
            _padding = value;
            Invalidate();
        }
    }

    public float? Radius
    {
        get => _radius;
        set
        {
            _radius = value;
            Invalidate();
        }
    }

    public Color? Color
    {
        get => _color;
        set
        {
            _color = value;
            Invalidate();
        }
    }

    /// <summary>Shadow depth. Mapped to the <see cref="Elevation" /> Z1/Z2/Z3 buckets; 0 disables.</summary>
    public float Elevation
    {
        get => _elevation;
        set
        {
            _elevation = value;
            Invalidate();
        }
    }

    /// <summary>Draw a hairline <see cref="ThemeData.Separator" /> border around the card.</summary>
    public bool Bordered
    {
        get => _bordered;
        set
        {
            _bordered = value;
            Invalidate();
        }
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = Theme.Of(context);
        var padding = _padding ?? EdgeInsets.All(theme.Padding);

        return new DecoratedBox {
            Elevation = _elevation > 0f ? ElevationBucket() : null,
            Fill = _color ?? theme.Surface,
            BorderColor = _bordered ? theme.Separator : Core.Color.Transparent,
            Radius = _radius ?? theme.CardRadius,
            Child = new LayoutPadding(padding: padding, child: _child),
        };
    }

    private ShadowStyle ElevationBucket()
    {
        // Fully qualify the static token class — the instance `Elevation` property shadows the name.
        return _elevation switch {
            >= 12f => UI.Theme.Elevation.Z3,
            >= 6f => UI.Theme.Elevation.Z2,
            _ => UI.Theme.Elevation.Z1,
        };
    }
}
