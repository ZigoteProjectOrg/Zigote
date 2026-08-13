using Zigote.Editor.Widgets;
using Zigote.Preferences;

namespace Zigote.Editor.Settings;

/// <summary>
///     Per-project shell-layout preferences (keys <c>layout.*</c>): the dock tree as one
///     <see cref="DockLayoutData" /> value, written on every dock change and restored on project
///     open. Conversion to/from the live <c>DockNode</c> tree (including dropping unknown panel
///     ids) lives in <c>DockLayoutStore</c>; this group only persists the data.
/// </summary>
public sealed class LayoutPreferences : PreferencesProvider
{
    public LayoutPreferences(PreferenceStore store) : base(store, "layout")
    {
        Dock = Register<DockLayoutData?>("dock", null);
    }

    /// <summary>The saved dock arrangement; null = the default layout.</summary>
    public Preference<DockLayoutData?> Dock { get; }
}
