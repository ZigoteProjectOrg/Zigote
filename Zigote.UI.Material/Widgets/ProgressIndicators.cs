namespace Zigote.UI.Material;

/// <summary>
///     A horizontal progress bar. Alias over <see cref="ProgressBar" />:
///     a null <c>value</c> is indeterminate (animated), a value in [0,1] is determinate.
///     <c>color</c>/<c>backgroundColor</c> are accepted but the theme colours are used.
/// </summary>
public sealed class LinearProgressIndicator : ProgressBar
{
    public LinearProgressIndicator(
        double? value = null,
        Color? color = null,
        Color? backgroundColor = null,
        double? minHeight = null)
        : base(value is { } v ? (float)v : null)
    {
        if (minHeight is { } m) Height = (float)m;
        _ = color;
        _ = backgroundColor;
    }
}

/// <summary>
///     A spinning ring. Alias over <see cref="Spinner" />
///     (always indeterminate — a determinate arc is not modelled, so <c>value</c> is accepted but the
///     spinner animates regardless). <c>strokeWidth</c> is accepted but not applied.
/// </summary>
public sealed class CircularProgressIndicator : Spinner
{
    public CircularProgressIndicator(
        double? value = null,
        Color? color = null,
        double strokeWidth = 4.0,
        double? size = null)
        : base(size: (float)(size ?? 24.0), color: color)
    {
        _ = value;
        _ = strokeWidth;
    }
}
