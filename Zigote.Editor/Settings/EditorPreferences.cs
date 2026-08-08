using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Menu;

namespace Zigote.Editor.Settings;

/// <summary>
///     The reactive glue between <see cref="EditorSettings" /> and the running editor: effects
///     observe the preferences and apply every change live — theme mode (System/Dark/Light + UI
///     font scale), runtime font-face swaps (UI "Inter" face, code-editor "code" face), vsync, and
///     the menu-bar presentation. The Settings window (or any code) only writes a preference value;
///     persistence happens inside the preference itself and the matching applier reacts. A batched
///     group <c>Reset()</c> settles each applier once. Program.cs subscribes to
///     <see cref="ThemeChanged" /> to rebuild the shell (editor panels take their ThemeData by
///     constructor).
/// </summary>
public sealed class EditorPreferences(App app, EditorSettings settings, ProjectHistory history)
{
    // Appliers live for the whole process; the fields keep the effects from being collected.
    private Effect? _chromeApplier;
    private Effect? _editorFontApplier;
    private Effect? _fileDialogApplier;
    private Effect? _themeApplier;
    private Effect? _uiFontApplier;
    private Effect? _vsyncApplier;

    public App App => app;
    public EditorSettings Settings => settings;

    /// <summary>Project history (recent projects) — its own preference group, not a setting.</summary>
    public ProjectHistory History => history;

    /// <summary>Dark after resolving "system" against the OS appearance (unknown → dark).</summary>
    public bool IsDarkResolved => settings.ThemeMode.Peek() switch {
        EditorThemeMode.Dark => true,
        EditorThemeMode.Light => false,
        _ => app.Engine.GetSystemTheme() != SystemTheme.Light,
    };

    private float UiFontScale => Math.Clamp(settings.UiFontSize.Peek(), 9f, 26f) / 13f;

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

    /// <summary>Re-resolve a "system" theme after the OS switched appearance.</summary>
    public void OnSystemThemeChanged()
    {
        if (settings.ThemeMode.Peek() == EditorThemeMode.System) ThemeChanged?.Invoke();
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
    ///     Wire the appliers. Each is an <see cref="Effect" /> whose construction pass applies the
    ///     persisted state — face swaps only when a non-default font is configured, the bundled
    ///     faces are already registered by App's constructor — and whose later passes react to
    ///     preference writes from anywhere: the Settings window, a group Reset, or code.
    /// </summary>
    public void ApplyAtBoot()
    {
        var boot = true;

        var appliedUiSize = settings.UiFontSize.Peek();
        var appliedNativeBar = settings.NativeMenuBar.Peek();
        _themeApplier = new Effect(() =>
            {
                _ = settings.ThemeMode.Value; // tracked: a mode swap rebuilds the shell
                var uiSize = settings.UiFontSize.Value;
                var nativeBar = settings.NativeMenuBar.Value;
                NativeMenuBar.Enabled = nativeBar;
                if (boot) return;
                if (nativeBar != appliedNativeBar)
                {
                    appliedNativeBar = nativeBar;
                    // Drop the app menus from the system bar now — the shell rebuild below re-installs
                    // the right presentation (native TryInstall or the in-window MenuBar fallback).
                    if (!nativeBar) NativeMenuBar.Uninstall();
                }

                if (Math.Abs(uiSize - appliedUiSize) > 0.001f)
                {
                    appliedUiSize = uiSize;
                    // A wholesale sizing change must re-shape all cached text (native + managed)
                    // exactly like a face swap does, or stale runs render against the new metrics.
                    app.ResetTextRendering();
                }

                InvokeUntracked(ThemeChanged);
            }
        );

        _uiFontApplier = new Effect(() =>
            {
                var path = settings.UiFontPath.Value;
                if (boot && path is null) return;
                ApplyUiFontFace(path);
            }
        );

        var appliedEditorFont = settings.EditorFontPath.Peek();
        _editorFontApplier = new Effect(() =>
            {
                var path = settings.EditorFontPath.Value;
                _ = settings.EditorFontSize.Value;
                _ = settings.ConsoleFontSize.Value;
                if (boot)
                {
                    if (path is not null) ApplyEditorFontFace(path);
                    return;
                }

                if (!string.Equals(path, appliedEditorFont, StringComparison.Ordinal))
                {
                    appliedEditorFont = path;
                    ApplyEditorFontFace(path);
                }

                app.ResetTextRendering();
                InvokeUntracked(EditorFontChanged);
            }
        );

        _vsyncApplier = new Effect(() =>
            {
                var on = settings.VSync.Value;
                if (boot && on) return; // swapchains start vsync-on; only a persisted "off" applies
                app.VSync = on;
            }
        );

        // Call sites gate on FileDialog.IsSupported, so the native/in-app choice applies live.
        _fileDialogApplier = new Effect(() =>
            {
                FileDialog.Enabled = settings.NativeFileDialogs.Value;
            }
        );

        _chromeApplier = new Effect(() =>
            {
                // App-wide window chrome: the main window gets it here; secondary windows (Settings,
                // dialogs, torn-out panels) inherit it at CreateWindow.
                WindowChrome.Preference = ParseChrome(settings.WindowChromeMode.Value);
                app.ApplyWindowChrome(WindowChrome.Resolve());
                if (boot) return;
                // The shell reads the titlebar insets at build time (toolbar leading gap) — rebuild it
                // so the layout tracks the new chrome, same as the native-menu-bar toggle.
                InvokeUntracked(ThemeChanged);
            }
        );

        boot = false;
    }

    private void ApplyUiFontFace(string? configured)
    {
        var path = configured ?? BundledFontPath("Inter-Regular.ttf");
        if (path is not null) app.SetFontFace("Inter", path);
    }

    private void ApplyEditorFontFace(string? configured)
    {
        var path = configured ?? BundledFontPath("Iosevka-Regular.ttc");
        if (path is not null) app.SetFontFace("code", path);
    }

    private static string? BundledFontPath(string file)
    {
        var p = Path.Combine(AppContext.BaseDirectory, "Fonts", file);
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    ///     Shell-rebuild handlers read preferences and widget state at will; fired from inside an
    ///     applier those reads would become phantom dependencies of the effect, so tracking is
    ///     suspended around the invoke.
    /// </summary>
    private static void InvokeUntracked(Action? handler)
    {
        if (handler is null) return;
        Reactive.Untracked(() =>
            {
                handler();
                return true;
            }
        );
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