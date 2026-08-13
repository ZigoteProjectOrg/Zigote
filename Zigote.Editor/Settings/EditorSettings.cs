using Zigote.Preferences;

namespace Zigote.Editor.Settings;

/// <summary>
///     Every persisted editor setting, declared once: a <see cref="PreferencesProvider" /> group
///     (keys <c>editor.*</c>) over the SQLite-backed store at <see cref="DbPath" />. Each entry is
///     a reactive <c>Preference&lt;T&gt;</c> — the Settings window writes values, the appliers in
///     <see cref="EditorPreferences" /> observe them and push the change into the running app, and
///     <see cref="PreferencesProvider.Reset" /> restores the whole group in one batch. Project
///     history (recent projects / last project) lives in its own <see cref="ProjectHistory" />
///     group on the same store — data the editor records, not a setting the user declares.
/// </summary>
public sealed class EditorSettings : PreferencesProvider
{
    public EditorSettings(PreferenceStore store) : base(store: store, prefix: "editor")
    {
        ReopenLastProject = Register(key: "reopenLastProject", defaultValue: true);
        NativeMenuBar = Register(key: "nativeMenuBar", defaultValue: true);
        ThemeMode = Register(key: "themeMode", defaultValue: EditorThemeMode.System);
        UiFontPath = Register<string?>(key: "uiFontPath", defaultValue: null);
        UiFontSize = Register(key: "uiFontSize", defaultValue: 13f);
        EditorFontPath = Register<string?>(key: "editorFontPath", defaultValue: null);
        EditorFontSize = Register(key: "editorFontSize", defaultValue: 13f);
        ConsoleFontSize = Register(key: "consoleFontSize", defaultValue: 0f);
        ConsoleClearOnPlay = Register(key: "consoleClearOnPlay", defaultValue: false);
        VSync = Register(key: "vsync", defaultValue: true);
        ReducedEditorGraphics = Register(key: "reducedEditorGraphics", defaultValue: false);
        NativeFileDialogs = Register(key: "nativeFileDialogs", defaultValue: true);
        WindowChromeMode = Register(key: "windowChromeMode", defaultValue: "auto");
        GpuIndex = Register(key: "gpuIndex", defaultValue: -1);
    }

    /// <summary>Reopen the last project on launch (off = always start at the welcome screen).</summary>
    public Preference<bool> ReopenLastProject { get; }

    /// <summary>
    ///     Use the OS-native menu bar where one exists (macOS NSMenu); off renders the
    ///     cross-platform in-window menu bar instead.
    /// </summary>
    public Preference<bool> NativeMenuBar { get; }

    /// <summary>Appearance: follow the OS, or force dark/light.</summary>
    public Preference<EditorThemeMode> ThemeMode { get; }

    /// <summary>UI font file; null = the bundled Inter.</summary>
    public Preference<string?> UiFontPath { get; }

    /// <summary>Base UI (body) font size in points; 13 = the default ramp. Scales all theme font tokens.</summary>
    public Preference<float> UiFontSize { get; }

    /// <summary>Code-editor font file (the "code" face); null = the bundled Iosevka.</summary>
    public Preference<string?> EditorFontPath { get; }

    /// <summary>Code-editor font size in points.</summary>
    public Preference<float> EditorFontSize { get; }

    /// <summary>Console panel font size in points; 0 = follow the theme caption size.</summary>
    public Preference<float> ConsoleFontSize { get; }

    /// <summary>Clear the console panel every time play mode starts.</summary>
    public Preference<bool> ConsoleClearOnPlay { get; }

    /// <summary>Swapchain vsync (off = uncapped present for performance testing).</summary>
    public Preference<bool> VSync { get; }

    /// <summary>Edit-mode reduced viewport graphics (no TAA/bloom/SSR/DoF while authoring).</summary>
    public Preference<bool> ReducedEditorGraphics { get; }

    /// <summary>
    ///     Use the OS-native file/folder dialogs for open/save flows; off uses the in-app
    ///     picker everywhere (the automatic fallback path).
    /// </summary>
    public Preference<bool> NativeFileDialogs { get; }

    /// <summary>
    ///     App-wide window chrome (main window + every secondary): "auto" (macOS unified /
    ///     GNOME Adwaita / else system), "system", "mac", or "adwaita" — the override exists for
    ///     cross-look testing.
    /// </summary>
    public Preference<string> WindowChromeMode { get; }

    /// <summary>
    ///     Which GPU the editor renders on, as an index into <c>ZigoteEngine.EnumerateGpus()</c>.
    ///     -1 (the default) picks the fastest one automatically. Read once at launch — the GPU device
    ///     is created a single time, so changing this only takes effect after a restart.
    /// </summary>
    public Preference<int> GpuIndex { get; }

    /// <summary>Absolute path of the persisted preferences.db (shown in the Settings window).</summary>
    public static string DbPath
    {
        get
        {
            string dir = Path.Combine(
                path1: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                path2: "Zigote"
            );
            Directory.CreateDirectory(dir);
            return Path.Combine(path1: dir, path2: "preferences.db");
        }
    }
}
