using Zigote.Core.Engine;
using Zigote.UI.Widgets.Controls;

namespace Zigote.UI.Widgets.Menu;

/// <summary>
///     macOS backend for <see cref="NativeMenuBar" />. Translates the cross-platform
///     <see cref="AppMenu" /> model into a native <c>NSMenu</c> via <see cref="MacMenu" />,
///     assigning each actionable item a tag and routing clicks back to its Action.
/// </summary>
public sealed class MacNativeMenuBar : INativeMenuBar
{
    // Cocoa NSEventModifierFlags
    private const uint Cmd = 1u << 20;
    private const uint Shift = 1u << 17;
    private const uint Option = 1u << 19;
    private const uint Control = 1u << 18;
    private const uint Function = 1u << 23; // fn / globe (🌐)

    private readonly Dictionary<int, Action> _actions = new();
    private readonly string _appName;
    private int _nextTag;

    public MacNativeMenuBar(string appName) => _appName = appName;

    public bool TryInstall(IReadOnlyList<AppMenu> menus)
    {
        if (!MacMenu.IsSupported) return false;

        _actions.Clear();
        _nextTag = 0;
        MacMenu.SetHandler(OnSelect);
        MacMenu.Reset(_appName);

        foreach (var menu in menus)
        {
            IntPtr native = MacMenu.AddMenu(menu.Title);
            AddItems(parent: native, items: menu.Items);
            if (menu.Role != AppMenuRole.None)
                MacMenu.SetMenuRole(menu: native, role: (int)menu.Role);
        }

        MacMenu.Commit();
        return true;
    }

    public void Uninstall()
    {
        if (!MacMenu.IsSupported) return;
        _actions.Clear();
        MacMenu.Reset(_appName);
        MacMenu.Commit();
    }

    private void AddItems(nint parent, IReadOnlyList<ContextMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.Separator)
            {
                MacMenu.AddSeparator(parent);
                continue;
            }

            if (item.Children is { Count: > 0 } children)
            {
                IntPtr sub = MacMenu.AddSubmenu(parent: parent, title: item.Label);
                AddItems(parent: sub, items: children);
                continue;
            }

            int tag = ++_nextTag;
            if (item.OnSelect is { } action && item.Enabled) _actions[tag] = action;
            (string key, uint mods) = ParseShortcut(item.Shortcut);
            MacMenu.AddItem(
                parent: parent,
                title: item.Label,
                tag: tag,
                key: key,
                modMask: mods,
                enabled: item.IsEnabled,
                sfSymbol: item.SystemImage,
                @checked: item.Checked == true
            );
        }
    }

    private void OnSelect(int tag)
    {
        if (tag == MacMenu.AboutTag)
        {
            if (NativeMenuBar.AboutRequested is { } about) about();
            else MacMenu.ShowStandardAbout();
            return;
        }

        if (_actions.TryGetValue(key: tag, value: out var action)) action();
    }

    private static (string key, uint mods) ParseShortcut(string? shortcut)
    {
        if (string.IsNullOrEmpty(shortcut)) return ("", 0);

        uint mods = 0;

        // The globe/fn modifier (🌐) is a surrogate pair — strip it as a string before the
        // per-char scan or its low surrogate would be mistaken for the key.
        if (shortcut.Contains(value: "🌐", comparisonType: StringComparison.Ordinal))
        {
            mods |= Function;
            shortcut = shortcut.Replace(
                oldValue: "🌐",
                newValue: "",
                comparisonType: StringComparison.Ordinal
            );
        }

        char key = '\0';
        foreach (char ch in shortcut)
        {
            switch (ch)
            {
                case '⌘': mods |= Cmd; break;
                case '⇧': mods |= Shift; break;
                case '⌥': mods |= Option; break;
                case '⌃': mods |= Control; break;
                default: key = char.ToLowerInvariant(ch); break;
            }
        }

        return key == '\0' ? ("", 0) : (key.ToString(), mods);
    }
}
