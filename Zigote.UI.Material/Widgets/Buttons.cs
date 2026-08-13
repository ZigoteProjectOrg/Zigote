// Button exposes a `Label` string property that shadows the Label *type* inside these subclasses,
// so reference the widget type through an alias.

using LabelWidget = Zigote.UI.Widgets.Controls.Label;

namespace Zigote.UI.Material;

/// <summary>
///     <c>ElevatedButton</c> — a filled, raised action button. Takes a <c>child:</c> widget
///     (usually a <see cref="Text" />) and an <c>onPressed:</c> callback; a null callback disables it.
/// </summary>
public sealed class ElevatedButton : Button
{
    public ElevatedButton(Widget child, Action? onPressed = null) : base(
        label: LabelOf(child),
        onPressed: onPressed
    )
    {
        if (child is not LabelWidget) Content = child;
        Style = ButtonStyle.Elevated;
    }

    internal static string LabelOf(Widget child) => child is LabelWidget l ? l.Text : "";
}

/// <summary><c>FilledButton</c> — a solid, filled button (maps to the elevated fill style).</summary>
public sealed class FilledButton : Button
{
    public FilledButton(Widget child, Action? onPressed = null) : base(
        label: ElevatedButton.LabelOf(child),
        onPressed: onPressed
    )
    {
        if (child is not LabelWidget) Content = child;
        Style = ButtonStyle.Elevated;
    }
}

/// <summary><c>OutlinedButton</c> — a bordered, transparent-fill button.</summary>
public sealed class OutlinedButton : Button
{
    public OutlinedButton(Widget child, Action? onPressed = null) : base(
        label: ElevatedButton.LabelOf(child),
        onPressed: onPressed
    )
    {
        if (child is not LabelWidget) Content = child;
        Style = ButtonStyle.Outlined;
    }
}

/// <summary><c>TextButton</c> — a borderless button that fills only on hover/press.</summary>
public sealed class TextButton : Button
{
    public TextButton(Widget child, Action? onPressed = null) : base(
        label: ElevatedButton.LabelOf(child),
        onPressed: onPressed
    )
    {
        if (child is not LabelWidget) Content = child;
        Style = ButtonStyle.Flat;
    }
}
