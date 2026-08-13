using Zigote.Core;

namespace Zigote.UI.Widgets;

/// <summary>Reading direction of a script — the horizontal flow of text and, by convention, of layout.</summary>
public enum TextDirection
{
    /// <summary>Left-to-right (Latin, Cyrillic, CJK, most scripts).</summary>
    Ltr,

    /// <summary>Right-to-left (Arabic, Hebrew, Persian, Urdu, …).</summary>
    Rtl,
}

/// <summary>
///     Publishes the ambient <see cref="TextDirection" /> to its subtree (the LTR/RTL analogue of
///     <c>ThemeProvider</c>). Direction-aware layout primitives (<c>Row</c>, <c>Wrap</c>,
///     <c>Padding</c> with <see cref="EdgeInsetsDirectional" />, <c>RichText</c>) resolve it during
///     Measure and mirror their main-axis placement under <see cref="TextDirection.Rtl" />.
///     A <c>LocalizationsScope</c> installs one automatically from the active locale.
/// </summary>
public sealed class Directionality : InheritedWidget
{
    private TextDirection _direction;

    public Directionality(TextDirection direction, Widget? child = null)
    {
        _direction = direction;
        Child = child;
    }

    public TextDirection Direction
    {
        get => _direction;
        set
        {
            if (_direction == value) return;
            _direction = value;
            MarkNeedsLayout();
            NotifyDependents();
        }
    }

    /// <summary>The text direction in scope (registers a dependency); defaults to <see cref="TextDirection.Ltr" />.</summary>
    public static TextDirection Of(BuildContext ctx)
    {
        return ctx.DependOn<Directionality>()?.Direction ?? TextDirection.Ltr;
    }

    /// <summary>The text direction in scope, or <c>null</c> when none is provided.</summary>
    public static TextDirection? MaybeOf(BuildContext ctx)
    {
        return ctx.DependOn<Directionality>()?.Direction;
    }

    public override bool UpdateShouldNotify(InheritedWidget oldWidget)
    {
        return oldWidget is not Directionality old || old.Direction != Direction;
    }
}

/// <summary>
///     <see cref="EdgeInsets" /> whose horizontal sides are direction-relative: <see cref="Start" />
///     is the leading edge (left in LTR, right in RTL) and <see cref="End" /> the trailing edge.
///     Resolve against the ambient <see cref="TextDirection" /> via <see cref="Resolve" />.
/// </summary>
public readonly struct EdgeInsetsDirectional(float start, float top, float end, float bottom)
{
    public readonly float Start = start;
    public readonly float Top = top;
    public readonly float End = end;
    public readonly float Bottom = bottom;

    public static EdgeInsetsDirectional All(float v)
    {
        return new EdgeInsetsDirectional(
            v,
            v,
            v,
            v
        );
    }

    public static EdgeInsetsDirectional Symmetric(float horizontal, float vertical)
    {
        return new EdgeInsetsDirectional(
            horizontal,
            vertical,
            horizontal,
            vertical
        );
    }

    public static EdgeInsetsDirectional Only(
        float start = 0f,
        float top = 0f,
        float end = 0f,
        float bottom = 0f)
    {
        return new EdgeInsetsDirectional(
            start,
            top,
            end,
            bottom
        );
    }

    /// <summary>Physical insets for the given direction (start/end swap sides under RTL).</summary>
    public EdgeInsets Resolve(TextDirection direction)
    {
        return direction == TextDirection.Rtl
            ? new EdgeInsets(
                End,
                Top,
                Start,
                Bottom
            )
            : new EdgeInsets(
                Start,
                Top,
                End,
                Bottom
            );
    }
}
