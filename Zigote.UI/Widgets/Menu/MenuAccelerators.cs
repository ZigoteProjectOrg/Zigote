using Zigote.Core.Events;
using Zigote.UI.Widgets.Controls;
using AppInstance = Zigote.UI.Host.App;

namespace Zigote.UI.Widgets.Menu;

/// <summary>
///     Keyboard shortcuts for the menu model on every backend that is not macOS. <c>NSMenu</c>
///     dispatches its own key equivalents, so on macOS a <see cref="ContextMenuItem.Shortcut" /> works
///     for free; in the in-window <see cref="MenuBar" /> (and the GNOME primary menu) the same string
///     is display-only text unless something binds it — this is that something.
///     <para>
///         Shortcut strings are accepted in both forms: the mac glyph form ("⌘⇧Z") and the written
///         form ("Ctrl+Shift+Z", "Mod+S", "F5"). ⌘ resolves to <see cref="KeyChord.PlatformCommand" />,
///         so one <see cref="AppMenu" /> model drives both platforms and
///         <see cref="Display" /> renders the label the local platform expects.
///     </para>
/// </summary>
public static class MenuAccelerators
{
    public static bool TryParse(string? shortcut, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(shortcut)) return false;
        if (shortcut.Contains('+', StringComparison.Ordinal))
            return KeyChord.TryParse(shortcut, out chord);

        // Glyph form. The globe/fn modifier is a surrogate pair with no cross-platform meaning —
        // strip it before the per-char scan or its low surrogate reads as the key.
        var mods = Modifiers.None;
        var key = "";
        foreach (var ch in shortcut.Replace("🌐", "", StringComparison.Ordinal))
            switch (ch)
            {
                case '⌘': mods |= KeyChord.PlatformCommand; break;
                case '⇧': mods |= Modifiers.Shift; break;
                case '⌥': mods |= Modifiers.Alt; break;
                case '⌃': mods |= Modifiers.Ctrl; break;
                default: key += ch; break;
            }

        if (!KeyNames.TryParse(key, out var code)) return false;
        chord = new KeyChord(code, mods);
        return true;
    }

    /// <summary>
    ///     The shortcut as this platform writes it: mac glyphs on macOS, "Ctrl+Shift+Z" elsewhere.
    ///     Unparsable strings are passed through untouched, so an app can still hand-write a label.
    /// </summary>
    public static string? Display(string? shortcut)
    {
        if (!TryParse(shortcut, out var chord)) return shortcut;
        if (!OperatingSystem.IsMacOS()) return chord.ToString();

        var s = "";
        if (chord.Modifiers.HasFlag(Modifiers.Ctrl)) s += '⌃';
        if (chord.Modifiers.HasFlag(Modifiers.Alt)) s += '⌥';
        if (chord.Modifiers.HasFlag(Modifiers.Shift)) s += '⇧';
        if (chord.Modifiers.HasFlag(Modifiers.Cmd)) s += '⌘';
        return s + KeyNames.Display(chord.Key);
    }

    /// <summary>
    ///     Bind every enabled, actionable item's shortcut (submenus included) as an app accelerator,
    ///     replacing whatever the previous menu model installed. Pass an empty list to unbind, which is
    ///     what <see cref="NativeMenuBar.TryInstall" /> does once a native bar takes ownership.
    /// </summary>
    public static void Install(AppInstance? app, IReadOnlyList<AppMenu> menus)
    {
        if (app is null) return;
        app.Accelerators.Clear();
        app.Accelerators.AddRange(Collect(menus));
    }

    /// <summary>The (chord, action) pairs <see cref="Install" /> binds — the whole menu tree, flattened.</summary>
    public static List<(KeyChord Chord, Action Run)> Collect(IReadOnlyList<AppMenu> menus)
    {
        List<(KeyChord, Action)> found = [];
        foreach (var menu in menus) Add(found, menu.Items);
        return found;
    }

    private static void Add(List<(KeyChord, Action)> into, IReadOnlyList<ContextMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.Children is { Count: > 0 } children)
            {
                Add(into, children);
                continue;
            }

            // A disabled item's shortcut is inert, exactly as its key equivalent is under NSMenu.
            if (item is { Separator: false, OnSelect: { } action } && item.IsEnabled &&
                TryParse(item.Shortcut, out var chord))
                into.Add((chord, action));
        }
    }
}
