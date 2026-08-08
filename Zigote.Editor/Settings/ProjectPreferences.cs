using Zigote.Persistence;
using Zigote.Preferences;

namespace Zigote.Editor.Settings;

/// <summary>
///     Per-project editor preferences: a JSON-file-backed <see cref="PreferenceStore" /> at
///     <c>&lt;project&gt;.prefs.json</c> next to the .zigoteproj, so the preferences are
///     project-relative and travel with the project. Same reactive model as the machine-wide
///     <see cref="EditorSettings" />, different backend — the backend is chosen here at the
///     composition root and nothing downstream changes. One provider group per concern:
///     <see cref="Viewport" /> (debug-viz toggles, snap) and <see cref="Layout" /> (dock tree).
/// </summary>
public sealed class ProjectPreferences : IDisposable
{
    public ProjectPreferences(string projectFile)
    {
        Store = new PreferenceStore(new JsonFileKeyValueStore(PathFor(projectFile)));
        Viewport = new ViewportPreferences(Store);
        Layout = new LayoutPreferences(Store);
    }

    public PreferenceStore Store { get; }
    public ViewportPreferences Viewport { get; }
    public LayoutPreferences Layout { get; }

    /// <summary>
    ///     <c>&lt;project&gt;.prefs.json</c>, next to the .zigoteproj — the same convention the
    ///     old standalone layout file used.
    /// </summary>
    public static string PathFor(string projectFile)
    {
        return Path.ChangeExtension(projectFile, ".prefs.json");
    }

    public void Dispose()
    {
        Store.Dispose();
    }
}