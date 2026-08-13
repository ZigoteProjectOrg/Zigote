namespace Zigote.Core.Animation;

/// <summary>
///     Interpolates a value between <see cref="Begin" /> and <see cref="End" />
///     given a normalized progress t in [0, 1].
/// </summary>
public abstract class Tween<T>(T begin, T end)
{
    public T Begin { get; set; } = begin;
    public T End { get; set; } = end;

    public abstract T Evaluate(float t);
}

/// <summary>float lerp tween.</summary>
public sealed class FloatTween(float begin, float end) : Tween<float>(begin: begin, end: end)
{
    public override float Evaluate(float t) => Begin + ((End - Begin) * t);
}

/// <summary>Color RGBA lerp tween.</summary>
public sealed class ColorTween(Color begin, Color end) : Tween<Color>(begin: begin, end: end)
{
    public override Color Evaluate(float t)
    {
        return new Color(
            r: Begin.R + ((End.R - Begin.R) * t),
            g: Begin.G + ((End.G - Begin.G) * t),
            b: Begin.B + ((End.B - Begin.B) * t),
            a: Begin.A + ((End.A - Begin.A) * t)
        );
    }
}

/// <summary>Offset (X, Y) lerp tween.</summary>
public sealed class OffsetTween(Offset begin, Offset end) : Tween<Offset>(begin: begin, end: end)
{
    public override Offset Evaluate(float t)
    {
        return new Offset(
            x: Begin.X + ((End.X - Begin.X) * t),
            y: Begin.Y + ((End.Y - Begin.Y) * t)
        );
    }
}

/// <summary>Size (Width, Height) lerp tween.</summary>
public sealed class SizeTween(Size begin, Size end) : Tween<Size>(begin: begin, end: end)
{
    public override Size Evaluate(float t)
    {
        return new Size(
            width: Begin.Width + ((End.Width - Begin.Width) * t),
            height: Begin.Height + ((End.Height - Begin.Height) * t)
        );
    }
}
