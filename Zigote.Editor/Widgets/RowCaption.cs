using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Editor.Widgets;

/// <summary>Reads a property row's caption back out of its widget tree, for the panel filters.</summary>
internal static class RowCaption
{
    /// <summary>
    ///     The text of the first <see cref="Label" /> under <paramref name="widget" /> — which every
    ///     labelled row builder puts first — or null for rows with none (spacers, button rows).
    /// </summary>
    // ponytail: inferred from the tree instead of threading a caption through every row builder;
    // give rows an explicit caption if one ever leads with a non-caption label.
    public static string? Of(Widget widget)
    {
        if (widget is Label label) return label.Text;
        foreach (var child in widget.GetChildren())
            if (Of(child) is { } text)
                return text;

        return null;
    }
}
