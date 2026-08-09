namespace Zigote.UI.Widgets.Focus;

/// <summary>
///     A widget whose focusable descendants are ONE Tab stop rather than many — GTK's list
///     "tab-behavior: item", which libadwaita 1.10 (GNOME 51) switched <c>AdwSidebar</c> to. Tab
///     enters the group once, landing on <see cref="TabTarget" /> (the current row), and the next
///     Tab leaves it; arrow keys still walk every row inside, because directional traversal ignores
///     grouping.
///     <para>
///         Without this a 40-row navigation list costs 40 Tab presses to step past — the reason GTK
///         made "item" the default for lists that already navigate with arrows.
///     </para>
/// </summary>
public interface IFocusGroup
{
    /// <summary>
    ///     The one descendant Tab should land on — normally the selected row. Null falls back to the
    ///     group's first focusable, so a group with nothing selected is still reachable.
    /// </summary>
    Widget? TabTarget { get; }
}
