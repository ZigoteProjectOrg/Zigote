using System.Text.Json;

namespace Zigote.Editor;

/// <summary>
///     Editor-level (not project-level) preferences persisted per user, independent of
///     any open project. Stores the recent-project list and the last opened project so
///     the editor can reopen it on the next launch.
/// </summary>
public sealed class EditorConfig
{
    private const int MaxRecent = 12;

    public List<string> RecentProjects { get; set; } = [];
    public string? LastProject { get; set; }

    /// <summary>Edit-mode reduced viewport graphics (no TAA/bloom/SSR/DoF while authoring).</summary>
    public bool ReducedEditorGraphics { get; set; }

    /// <summary>Reopen the last project on launch (off = always start at the welcome screen).</summary>
    public bool ReopenLastProject { get; set; } = true;

    /// <summary>Appearance: "system" (follow OS), "dark", or "light".</summary>
    public string ThemeMode { get; set; } = "system";

    /// <summary>UI font file; null = the bundled Inter.</summary>
    public string? UiFontPath { get; set; }

    /// <summary>Base UI (body) font size in points; 13 = the default ramp. Scales all theme font tokens.</summary>
    public float UiFontSize { get; set; } = 13f;

    /// <summary>Code-editor font file (the "code" face); null = the bundled Iosevka.</summary>
    public string? EditorFontPath { get; set; }

    /// <summary>Code-editor font size in points.</summary>
    public float EditorFontSize { get; set; } = 13f;

    /// <summary>Console panel font size in points; 0 = follow the theme caption size.</summary>
    public float ConsoleFontSize { get; set; }

    /// <summary>Clear the console panel every time play mode starts.</summary>
    public bool ConsoleClearOnPlay { get; set; }

    /// <summary>Swapchain vsync (off = uncapped present for performance testing).</summary>
    public bool VSync { get; set; } = true;

    /// <summary>Use the OS-native menu bar where one exists (macOS NSMenu); off renders the
    ///     cross-platform in-window menu bar instead.</summary>
    public bool NativeMenuBar { get; set; } = true;

    /// <summary>Absolute path of the persisted editor.json (shown in the settings window).</summary>
    public static string FilePath => ConfigPath;

    private static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Zigote"
            );
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "editor.json");
        }
    }

    public static EditorConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<EditorConfig>(File.ReadAllText(ConfigPath)) ??
                       new EditorConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Zigote] Failed to load editor config: {ex.Message}");
        }

        return new EditorConfig();
    }

    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, SaveOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Zigote] Failed to save editor config: {ex.Message}");
        }
    }

    /// <summary>Record a project as the most-recently opened and persist immediately.</summary>
    public void RecordOpened(string projectPath)
    {
        var full = Path.GetFullPath(projectPath);
        RecentProjects.RemoveAll(p => PathEquals(p, full));
        RecentProjects.Insert(0, full);
        if (RecentProjects.Count > MaxRecent)
            RecentProjects.RemoveRange(MaxRecent, RecentProjects.Count - MaxRecent);
        LastProject = full;
        Save();
    }

    /// <summary>Drop a project from the recent list (e.g. when it no longer exists).</summary>
    public void Forget(string projectPath)
    {
        var full = Path.GetFullPath(projectPath);
        RecentProjects.RemoveAll(p => PathEquals(p, full));
        if (LastProject != null && PathEquals(LastProject, full)) LastProject = null;
        Save();
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}