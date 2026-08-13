namespace Zigote.UI.Material;

/// <summary>
///     The chrome around a text field. <see cref="HintText" />/<see cref="LabelText" /> map onto the
///     field's hint, and <see cref="PrefixIcon" />/<see cref="SuffixIcon" /> render inline when supported.
///     Border/fill styling is taken from the theme (the <c>border</c>/<c>filled</c> args are accepted-and-ignored).
/// </summary>
public sealed class InputDecoration
{
    public InputDecoration(
        string? hintText = null,
        string? labelText = null,
        string? helperText = null,
        Widget? prefixIcon = null,
        Widget? suffixIcon = null,
        bool? filled = null)
    {
        HintText = hintText;
        LabelText = labelText;
        HelperText = helperText;
        PrefixIcon = prefixIcon;
        SuffixIcon = suffixIcon;
        Filled = filled;
    }

    public string? HintText { get; }
    public string? LabelText { get; }
    public string? HelperText { get; }
    public Widget? PrefixIcon { get; }
    public Widget? SuffixIcon { get; }
    public bool? Filled { get; }
}
