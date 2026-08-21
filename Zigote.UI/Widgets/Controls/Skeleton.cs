using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     A loading placeholder: a rounded block in a muted colour with a highlight sweeping across
///     it. The <c>skeletonizer</c> slot from the plugin roadmap, which belongs in the framework
///     rather than in a package — it is pure drawing.
///     <para>
///         Build the placeholder from the same layout as the real content, one
///         <see cref="Skeleton" /> per thing that will appear:
///     </para>
///     <code>
///   loading
///       ? new Column(spacing: 12) { Children = { Skeleton.Circle(48), Skeleton.Text(lines: 3) } }
///       : new ProfileCard(user)
/// </code>
///     <para>
///         ponytail: the skeleton is composed, not derived. Flutter's skeletonizer walks the real
///         subtree and replaces every painted box with a grey one; that needs a paint-time
///         interception layer, and this needs a widget. Build the derived version when a screen
///         has enough placeholders that keeping the two layouts in step actually hurts.
///     </para>
/// </summary>
public sealed class Skeleton : Widget
{
    /// <summary>Slices the sweep is drawn with — enough for a smooth ramp, few enough to stay cheap.</summary>
    private const int SweepSlices = 16;

    private readonly AnimationController _anim;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    /// <param name="width">Fixed width, or null to fill the constraint — what a text line does.</param>
    /// <param name="height">Fixed height. The default is one line of body text.</param>
    /// <param name="radius">Corner radius.</param>
    public Skeleton(float? width = null, float height = 14f, float radius = 6f)
    {
        Width = width;
        Height = height;
        Radius = radius;
        // 1.4 s per sweep, linear: a shimmer that eases looks like it is stuttering.
        _anim = new AnimationController(durationSeconds: 1.4f, vsync: this) { Curve = Curves.Linear };
        _anim.OnTick += MarkNeedsPaint;
        _anim.Repeat();
    }

    /// <summary>Fixed width, or null to fill the incoming constraint.</summary>
    public float? Width { get; set; }

    /// <summary>Fixed height.</summary>
    public float Height { get; set; }

    /// <summary>Corner radius; half the height makes a pill, half the width a circle.</summary>
    public float Radius { get; set; }

    /// <summary>Block colour. Defaults to the theme's alternate surface.</summary>
    public Color? BaseColor { get; set; }

    /// <summary>Sweep colour. Defaults to a lighter version of the block colour.</summary>
    public Color? HighlightColor { get; set; }

    /// <summary>
    ///     Turn the sweep off and leave a static block. Set this when the user has asked for
    ///     reduced motion, or for a screenshot test that cannot have a moving pixel in it.
    /// </summary>
    public bool Animated
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value) _anim.Repeat();
            else _anim.Seek(0f);
        }
    } = true;

    /// <summary>A circle — an avatar, a status dot.</summary>
    public static Skeleton Circle(float diameter)
        => new(width: diameter, height: diameter, radius: diameter / 2f);

    /// <summary>A block — an image, a card, a button.</summary>
    public static Skeleton Box(float? width = null, float height = 120f, float radius = 12f)
        => new(width: width, height: height, radius: radius);

    /// <summary>
    ///     A paragraph: full-width lines with a short last one, the shape a block of text
    ///     actually has.
    /// </summary>
    /// <param name="lines">How many lines to draw.</param>
    /// <param name="lineHeight">Height of one line.</param>
    /// <param name="spacing">Gap between lines.</param>
    /// <param name="lastLineFraction">Width of the last line as a fraction of the others.</param>
    public static Widget Text(
        int lines = 3, float lineHeight = 14f, float spacing = 8f, float lastLineFraction = 0.6f)
    {
        var column = new Column(
            crossAxisAlignment: CrossAxisAlignment.Start,
            mainAxisSize: MainAxisSize.Min,
            spacing: spacing);
        for (int i = 0; i < Math.Max(1, lines); i++)
        {
            bool last = i == lines - 1 && lines > 1;
            Widget line = new Skeleton(height: lineHeight, radius: lineHeight / 2f);
            // The short last line needs a width, and the width is a fraction of whatever the
            // column ends up being — that is what FractionallySizedBox is for.
            column.Children.Add(last
                ? new FractionallySizedBox(widthFactor: lastLineFraction, child: line)
                : line);
        }

        return column;
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    // The ticker handed out by CreateTicker is disposed on unmount, so rebind on re-attach or
    // the shimmer stops after the first detach.
    protected override void OnMount() => _anim.AttachTicker(this);

    public override int DebugStateHash()
        => HashCode.Combine(Width, Height, Radius, Animated, _anim.Progress);

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float width = Width ?? (float.IsInfinity(c.MaxWidth) ? 120f : c.MaxWidth);
        _size = c.Constrain(new Size(width: width, height: Height));
        return _size;
    }

    public override void Layout(Offset origin)
        => Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);

    public override void Paint(PaintList paint)
    {
        var baseColor = BaseColor ?? _theme.SurfaceAlt;
        paint.AddRect(bounds: Bounds, color: baseColor, radius: Radius);
        if (!Animated || Bounds.Width <= 0) return;

        var highlight = HighlightColor ?? baseColor.Lighten(0.35f);

        // Clipped to the block's own rounded rect: the sweep must not leak over its corners.
        paint.AddClipStart(bounds: Bounds, radius: Radius);
        for (int i = 0; i < SweepSlices; i++)
        {
            if (Slice(Bounds, _anim.Value, i) is not var (x, width, alpha)) continue;
            paint.AddRect(
                bounds: new Rect(x: x, y: Bounds.Y, width: width, height: Bounds.Height),
                color: highlight.WithAlpha(alpha * highlight.A));
        }

        paint.AddClipEnd();
    }

    public override Widget? HitTest(Offset point) => null;   // a placeholder is not a target

    /// <summary>
    ///     One slice of the highlight band at a given phase, clipped to the block, or null when
    ///     that slice is off the block entirely. The band travels from fully off the left edge to
    ///     fully off the right one, so the block spends part of the loop quiet instead of pulsing
    ///     forever; brightness falls off triangularly from the middle of the band.
    /// </summary>
    internal static (float X, float Width, float Alpha)? Slice(Rect bounds, float phase, int index)
    {
        float band = MathF.Max(24f, bounds.Width * 0.4f);
        float centre = bounds.X - band + (phase * (bounds.Width + (2f * band)));
        float slice = band / SweepSlices;

        float offset = (index + 0.5f) / SweepSlices;      // 0..1 across the band
        float x = centre - (band / 2f) + (offset * band);
        float left = MathF.Max(x, bounds.X);
        float right = MathF.Min(x + slice, bounds.X + bounds.Width);
        if (right <= left) return null;

        return (left, right - left, 1f - MathF.Abs((offset * 2f) - 1f));
    }
}
