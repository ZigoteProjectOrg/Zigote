// The Label string property shadows the Label widget type inside this class, so reference the
// widget type through an alias.

using LabelWidget = Zigote.UI.Widgets.Controls.Label;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwButtonContent — the standard icon + bold label pairing for a button's child. Use as
///     <see cref="AdwButton.Content" /> or as an <see cref="AdwSplitButton" /> main section. Leave
///     <see cref="Color" /> unset and the owning button tints the built children to its foreground.
/// </summary>
public sealed class AdwButtonContent : ComposedWidget
{
    private Color? _color;
    private string? _iconName;
    private string _label;

    public AdwButtonContent(string? iconName = null, string label = "")
    {
        _iconName = iconName;
        _label = label;
    }

    public string? IconName
    {
        get => _iconName;
        set => this.Set(ref _iconName, value);
    }

    public string Label
    {
        get => _label;
        set => this.Set(ref _label, value);
    }

    /// <summary>
    ///     Foreground for the icon and label. A button that resolves its own foreground (every
    ///     .suggested-action / .destructive-action one, where the text must be accent-fg) has to set
    ///     this instead of relying on <c>AdwButtonState.TintForeground</c>: that walks GetChildren,
    ///     and a ComposedWidget has no children until it has been built — one frame too late.
    /// </summary>
    public Color? Color
    {
        get => _color;
        set => this.Set(ref _color, value);
    }

    protected override Widget Build(BuildContext context)
    {
        // buttoncontent: border-spacing 6px, a bold label, and 2px of trailing padding so the
        // label never sits flush against the button's own padding.
        var row = new Row(spacing: AdwMetrics.ButtonContentSpacing, mainAxisSize: MainAxisSize.Min);
        if (IconName is not null)
            row.Children.Add(new IconGlyph(IconName, AdwMetrics.IconSize, _color));
        if (Label.Length > 0)
            row.Children.Add(
                new Padding(
                    EdgeInsets.Only(right: 2f),
                    new LabelWidget(Label, AdwTypography.Heading, _color) { MaxLines = 1 }
                )
            );
        return row;
    }
}
