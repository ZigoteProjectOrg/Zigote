using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Menu;

namespace Zigote.Editor.Settings;

/// <summary>
///     Applies user preferences from <see cref="EditorConfig" /> to the running editor: theme mode
///     (System/Dark/Light + UI font scale), runtime font-face swaps (UI "Inter" face, code-editor
///     "code" face), and vsync. The settings window mutates config through this class so every
///     change is applied live AND persisted; Program.cs subscribes to <see cref="ThemeChanged" />
///     to rebuild the shell (editor panels take their ThemeData by constructor).
/// </summary>
public sealed class EditorPreferences(App app, EditorConfig config)
{
    public App App => app;
    public EditorConfig Config => config;

    /// <summary>Dark after resolving "system" against the OS appearance (unknown → dark).</summary>
    public bool IsDarkResolved => config.ThemeMode switch {
        "dark" => true,
        "light" => false,
        _ => app.Engine.GetSystemTheme() != SystemTheme.Light,
    };

    private float UiFontScale => Math.Clamp(config.UiFontSize, 9f, 26f) / 13f;

    /// <summary>The resolved theme must be re-derived and the UI rebuilt (mode/scale change).</summary>
    public event Action? ThemeChanged;

    /// <summary>The "code" face or editor font size changed (re-style CodeEditor/Console).</summary>
    public event Action? EditorFontChanged;

    /// <summary>The ThemeData the editor should currently run (mode + UI font scale applied).</summary>
    public ThemeData ResolveTheme()
    {
        var t = IsDarkResolved ? ThemeData.Dark : ThemeData.Light;
        return t.WithFontScale(UiFontScale);
    }

    public void SetThemeMode(string mode)
    {
        if (config.ThemeMode == mode) return;
        config.ThemeMode = mode;
        config.Save();
        ThemeChanged?.Invoke();
    }

    /// <summary>Re-resolve a "system" theme after the OS switched appearance.</summary>
    public void OnSystemThemeChanged()
    {
        if (config.ThemeMode == "system") ThemeChanged?.Invoke();
    }

    public void SetUiFontSize(float size)
    {
        config.UiFontSize = Math.Clamp(size, 9f, 26f);
        config.Save();
        // A wholesale sizing change must re-shape all cached text (native + managed) exactly like
        // a face swap does, or stale shaped runs render against the new metrics.
        app.ResetTextRendering();
        ThemeChanged?.Invoke();
    }

    public void SetUiFont(string? path)
    {
        config.UiFontPath = path;
        config.Save();
        ApplyUiFontFace();
    }

    public void SetEditorFont(string? path)
    {
        config.EditorFontPath = path;
        config.Save();
        ApplyEditorFontFace();
        EditorFontChanged?.Invoke();
    }

    public void SetEditorFontSize(float size)
    {
        config.EditorFontSize = Math.Clamp(size, 8f, 32f);
        config.Save();
        app.ResetTextRendering();
        EditorFontChanged?.Invoke();
    }

    public void SetConsoleFontSize(float size)
    {
        config.ConsoleFontSize = size <= 0 ? 0 : Math.Clamp(size, 8f, 24f);
        config.Save();
        app.ResetTextRendering();
        EditorFontChanged?.Invoke();
    }

    public void SetVSync(bool on)
    {
        config.VSync = on;
        config.Save();
        app.VSync = on;
    }

    /// <summary>Switch between the OS-native menu bar and the in-window one (macOS-relevant;
    ///     elsewhere there is no native backend and the in-window bar is always used).</summary>
    public void SetNativeMenuBar(bool on)
    {
        if (config.NativeMenuBar == on) return;
        config.NativeMenuBar = on;
        config.Save();
        NativeMenuBar.Enabled = on;
        // Drop the app menus from the system bar now — the shell rebuild below re-installs the
        // right presentation (native TryInstall or the in-window MenuBar fallback).
        if (!on) NativeMenuBar.Uninstall();
        ThemeChanged?.Invoke();
    }

    /// <summary>Switch between the OS-native file dialogs and the in-app picker (the existing
    ///     fallback path — call sites gate on FileDialog.IsSupported, so this applies live).</summary>
    public void SetNativeFileDialogs(bool on)
    {
        if (config.NativeFileDialogs == on) return;
        config.NativeFileDialogs = on;
        config.Save();
        FileDialog.Enabled = on;
    }

    /// <summary>Window chrome mode ("auto"/"system"/"mac"/"adwaita") — applied live to every
    ///     open window (main included) and inherited by new ones; the override lets any look be
    ///     tested on any OS.</summary>
    public void SetWindowChrome(string mode)
    {
        if (config.WindowChromeMode == mode) return;
        config.WindowChromeMode = mode;
        config.Save();
        WindowChrome.Preference = ParseChrome(mode);
        app.ApplyWindowChrome(WindowChrome.Resolve());
    }

    internal static WindowChromePreference ParseChrome(string mode)
    {
        return mode switch {
            "system" => WindowChromePreference.System,
            "mac" => WindowChromePreference.MacUnified,
            "adwaita" => WindowChromePreference.AdwaitaCsd,
            _ => WindowChromePreference.Auto,
        };
    }

    /// <summary>
    ///     Apply persisted font faces + vsync at boot. Face swaps only run when a non-default font
    ///     is configured — the bundled faces are already registered by App's constructor.
    /// </summary>
    public void ApplyAtBoot()
    {
        if (config.UiFontPath is not null) ApplyUiFontFace();
        if (config.EditorFontPath is not null) ApplyEditorFontFace();
        if (!config.VSync) app.VSync = false;
        NativeMenuBar.Enabled = config.NativeMenuBar;
        FileDialog.Enabled = config.NativeFileDialogs;
        WindowChrome.Preference = ParseChrome(config.WindowChromeMode);
        // App-wide window chrome: the main window gets it here; secondary windows (Settings,
        // dialogs, torn-out panels) inherit it at CreateWindow.
        app.ApplyWindowChrome(WindowChrome.Resolve());
    }

    private void ApplyUiFontFace()
    {
        var path = config.UiFontPath ?? BundledFontPath("Inter-Regular.ttf");
        if (path is not null) app.SetFontFace("Inter", path);
    }

    private void ApplyEditorFontFace()
    {
        var path = config.EditorFontPath ?? BundledFontPath("Iosevka-Regular.ttc");
        if (path is not null) app.SetFontFace("code", path);
    }

    private static string? BundledFontPath(string file)
    {
        var p = Path.Combine(AppContext.BaseDirectory, "Fonts", file);
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    ///     Font files available to the pickers: everything in the bundled Fonts/ directory except
    ///     the icon/emoji faces (those are glyph atlases, not text faces).
    /// </summary>
    public static IReadOnlyList<(string Name, string Path)> AvailableFonts()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fonts");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".ttc" or ".otf")
            .Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    return !name.Contains("MaterialIcons", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("Emoji", StringComparison.OrdinalIgnoreCase);
                }
            )
            .OrderBy(Path.GetFileNameWithoutExtension)
            .Select(f => (Path.GetFileNameWithoutExtension(f)!, f))
            .ToList();
    }
}