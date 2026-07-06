namespace Zigote.UI.Material;

/// <summary>
///     A label for one page in a <c>TabBar</c>. Carries a <see cref="Text" />
///     label and/or an <see cref="Icon" /> widget. The strip renders the text (icon-only tabs fall back
///     to an empty label until icon tabs are wired natively).
/// </summary>
public sealed class Tab
{
    public Tab(string? text = null, Widget? icon = null, Widget? child = null)
    {
        Text = text;
        Icon = icon;
        Child = child;
    }

    public string? Text { get; }
    public Widget? Icon { get; }
    public Widget? Child { get; }

    /// <summary>Best-effort plain-text label for the strip.</summary>
    public string Label => Text ?? "";
}