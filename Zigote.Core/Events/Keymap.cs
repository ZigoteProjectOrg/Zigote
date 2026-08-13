namespace Zigote.Core.Events;

/// <summary>
///     A key + modifier combination (e.g. <c>Cmd+Shift+P</c>). Modifier matching is exact; build
///     platform "command" chords with <see cref="Command" /> so the same binding resolves to ⌘ on
///     macOS
///     and Ctrl elsewhere. Parse from / print to the conventional <c>Mod+Shift+Key</c> string form.
/// </summary>
public readonly record struct KeyChord(KeyCode Key, Modifiers Modifiers = Modifiers.None)
{
    /// <summary>
    ///     The platform "command" modifier: ⌘ (<see cref="Modifiers.Cmd" />) on macOS, Ctrl
    ///     elsewhere.
    /// </summary>
    public static Modifiers PlatformCommand =>
        OperatingSystem.IsMacOS() ? Modifiers.Cmd : Modifiers.Ctrl;

    /// <summary>A chord using the platform command modifier (cross-platform ⌘/Ctrl shortcut).</summary>
    public static KeyChord Command(KeyCode key, bool shift = false, bool alt = false)
    {
        var m = PlatformCommand;
        if (shift) m |= Modifiers.Shift;
        if (alt) m |= Modifiers.Alt;
        return new KeyChord(key, m);
    }

    public bool Matches(KeyCode key, Modifiers modifiers)
    {
        return Key == key && Modifiers == modifiers;
    }

    public bool Matches(KeyEvent e)
    {
        return e.Down && Matches(e.Key, e.Modifiers);
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(Modifiers.Cmd)) parts.Add("Cmd");
        if (Modifiers.HasFlag(Modifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(Modifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyNames.Display(Key));
        return string.Join("+", parts);
    }

    /// <summary>
    ///     Parse "Cmd+Shift+P" / "Ctrl+S" / "F5" / "Escape". Modifier tokens: cmd/command/meta/super/win,
    ///     ctrl/control, alt/option/opt, shift, and <c>mod</c>/<c>cmdorctrl</c> for the platform command.
    /// </summary>
    public static bool TryParse(string text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = text.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (tokens.Length == 0) return false;

        var mods = Modifiers.None;
        for (var i = 0; i < tokens.Length - 1; i++)
            switch (tokens[i].ToLowerInvariant())
            {
                case "cmd" or "command" or "meta" or "super" or "win": mods |= Modifiers.Cmd; break;
                case "ctrl" or "control": mods |= Modifiers.Ctrl; break;
                case "alt" or "option" or "opt": mods |= Modifiers.Alt; break;
                case "shift": mods |= Modifiers.Shift; break;
                case "mod" or "cmdorctrl": mods |= PlatformCommand; break;
                default: return false;
            }

        if (!KeyNames.TryParse(tokens[^1], out var key)) return false;
        chord = new KeyChord(key, mods);
        return true;
    }
}

/// <summary>
///     A configurable map from action ids (e.g. "edit.copy") to one or more <see cref="KeyChord" />s.
///     Resolve an incoming <see cref="KeyEvent" /> to an action, rebind at runtime, and round-trip the
///     whole table to/from string pairs for persistence. Engine-neutral input policy (no UI
///     dependency).
/// </summary>
public sealed class Keymap
{
    private readonly Dictionary<string, List<KeyChord>> _bindings = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Actions => _bindings.Keys;

    /// <summary>Add a chord to an action (idempotent — duplicate chords are ignored).</summary>
    public void Bind(string action, KeyChord chord)
    {
        if (!_bindings.TryGetValue(action, out var list))
            _bindings[action] = list = [];
        if (!list.Contains(chord)) list.Add(chord);
    }

    /// <summary>Add a chord parsed from a string; returns false (and binds nothing) if it doesn't parse.</summary>
    public bool Bind(string action, string chord)
    {
        if (!KeyChord.TryParse(chord, out var c)) return false;
        Bind(action, c);
        return true;
    }

    /// <summary>Replace all of an action's chords with a single one.</summary>
    public void Rebind(string action, KeyChord chord)
    {
        _bindings[action] = [chord];
    }

    /// <summary>Remove every chord bound to an action.</summary>
    public void Unbind(string action)
    {
        _bindings.Remove(action);
    }

    /// <summary>Remove one chord from an action (dropping the action if it becomes empty).</summary>
    public void Unbind(string action, KeyChord chord)
    {
        if (!_bindings.TryGetValue(action, out var list)) return;
        list.Remove(chord);
        if (list.Count == 0) _bindings.Remove(action);
    }

    public IReadOnlyList<KeyChord> ChordsFor(string action)
    {
        return _bindings.TryGetValue(action, out var list) ? list : [];
    }

    public bool IsBound(string action, KeyCode key, Modifiers modifiers)
    {
        if (!_bindings.TryGetValue(action, out var list)) return false;
        foreach (var c in list)
            if (c.Matches(key, modifiers))
                return true;
        return false;
    }

    /// <summary>The first action whose any chord matches; null if none. Down-events only.</summary>
    public string? Resolve(KeyCode key, Modifiers modifiers)
    {
        foreach (var (action, list) in _bindings)
        foreach (var c in list)
            if (c.Matches(key, modifiers))
                return action;
        return null;
    }

    public string? Resolve(KeyEvent e)
    {
        return e.Down ? Resolve(e.Key, e.Modifiers) : null;
    }

    /// <summary>Flatten to (action, chord-string) pairs for persistence.</summary>
    public IEnumerable<(string Action, string Chord)> Export()
    {
        foreach (var (action, list) in _bindings)
        foreach (var c in list)
            yield return (action, c.ToString());
    }

    /// <summary>
    ///     Replace all bindings with the given (action, chord-string) pairs. Unparsable chords are
    ///     skipped.
    /// </summary>
    public void Load(IEnumerable<(string Action, string Chord)> entries)
    {
        _bindings.Clear();
        foreach (var (action, chord) in entries)
            Bind(action, chord);
    }
}
