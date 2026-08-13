namespace Zigote.Core.Events;

/// <summary>
///     Named physical keys. Values are raw SDL3 scancodes, so <c>(KeyCode)keyEvent.Scancode</c> maps a
///     hardware key straight onto this enum (and <see cref="KeyEvent.Key" /> does exactly that).
///     Physical
///     means layout-independent: <see cref="KeyCode.A" /> is the key in the QWERTY 'A' position
///     regardless
///     of the active keyboard layout. Use this instead of magic scancode literals in shortcut code.
///     (Named
///     <c>KeyCode</c>, not <c>Key</c>, to avoid clashing with the widget-reconciliation <c>Key</c>
///     type.)
/// </summary>
public enum KeyCode : uint
{
    Unknown = 0,

    A = 4,
    B = 5,
    C = 6,
    D = 7,
    E = 8,
    F = 9,
    G = 10,
    H = 11,
    I = 12,
    J = 13,
    K = 14,
    L = 15,
    M = 16,
    N = 17,
    O = 18,
    P = 19,
    Q = 20,
    R = 21,
    S = 22,
    T = 23,
    U = 24,
    V = 25,
    W = 26,
    X = 27,
    Y = 28,
    Z = 29,

    Digit1 = 30,
    Digit2 = 31,
    Digit3 = 32,
    Digit4 = 33,
    Digit5 = 34,
    Digit6 = 35,
    Digit7 = 36,
    Digit8 = 37,
    Digit9 = 38,
    Digit0 = 39,

    Enter = 40,
    Escape = 41,
    Backspace = 42,
    Tab = 43,
    Space = 44,
    Minus = 45,
    Equals = 46,
    LeftBracket = 47,
    RightBracket = 48,
    Backslash = 49,
    Semicolon = 51,
    Apostrophe = 52,
    Grave = 53,
    Comma = 54,
    Period = 55,
    Slash = 56,
    CapsLock = 57,

    F1 = 58,
    F2 = 59,
    F3 = 60,
    F4 = 61,
    F5 = 62,
    F6 = 63,
    F7 = 64,
    F8 = 65,
    F9 = 66,
    F10 = 67,
    F11 = 68,
    F12 = 69,

    PrintScreen = 70,
    ScrollLock = 71,
    Pause = 72,
    Insert = 73,
    Home = 74,
    PageUp = 75,
    Delete = 76,
    End = 77,
    PageDown = 78,
    Right = 79,
    Left = 80,
    Down = 81,
    Up = 82,

    NumLock = 83,
    KpDivide = 84,
    KpMultiply = 85,
    KpMinus = 86,
    KpPlus = 87,
    KpEnter = 88,
    Kp1 = 89,
    Kp2 = 90,
    Kp3 = 91,
    Kp4 = 92,
    Kp5 = 93,
    Kp6 = 94,
    Kp7 = 95,
    Kp8 = 96,
    Kp9 = 97,
    Kp0 = 98,
    KpPeriod = 99,

    Application = 101,

    F13 = 104,
    F14 = 105,
    F15 = 106,
    F16 = 107,
    F17 = 108,
    F18 = 109,
    F19 = 110,
    F20 = 111,
    F21 = 112,
    F22 = 113,
    F23 = 114,
    F24 = 115,

    Menu = 118,

    LeftCtrl = 224,
    LeftShift = 225,
    LeftAlt = 226,
    LeftGui = 227,
    RightCtrl = 228,
    RightShift = 229,
    RightAlt = 230,
    RightGui = 231,

    /// <summary>
    ///     The system "back" action (SDL_SCANCODE_AC_BACK). On Android this is the back gesture
    ///     or button; on desktop it is a browser key most keyboards do not have.
    /// </summary>
    AcBack = 282,
}

/// <summary>
///     Name ↔ <see cref="KeyCode" /> mapping for parsing/printing keymap chords (e.g. "Esc",
///     "F5").
/// </summary>
public static class KeyNames
{
    private static readonly Dictionary<string, KeyCode> Aliases =
        new(StringComparer.OrdinalIgnoreCase) {
            ["esc"] = KeyCode.Escape,
            ["escape"] = KeyCode.Escape,
            ["enter"] = KeyCode.Enter,
            ["return"] = KeyCode.Enter,
            ["space"] = KeyCode.Space,
            ["spacebar"] = KeyCode.Space,
            ["tab"] = KeyCode.Tab,
            ["backspace"] = KeyCode.Backspace,
            ["bksp"] = KeyCode.Backspace,
            ["del"] = KeyCode.Delete,
            ["delete"] = KeyCode.Delete,
            ["ins"] = KeyCode.Insert,
            ["insert"] = KeyCode.Insert,
            ["home"] = KeyCode.Home,
            ["end"] = KeyCode.End,
            ["pageup"] = KeyCode.PageUp,
            ["pgup"] = KeyCode.PageUp,
            ["pagedown"] = KeyCode.PageDown,
            ["pgdn"] = KeyCode.PageDown,
            ["up"] = KeyCode.Up,
            ["down"] = KeyCode.Down,
            ["left"] = KeyCode.Left,
            ["right"] = KeyCode.Right,
            ["minus"] = KeyCode.Minus,
            ["-"] = KeyCode.Minus,
            ["equals"] = KeyCode.Equals,
            ["equal"] = KeyCode.Equals,
            ["="] = KeyCode.Equals,
            ["comma"] = KeyCode.Comma,
            [","] = KeyCode.Comma,
            ["period"] = KeyCode.Period,
            ["dot"] = KeyCode.Period,
            ["."] = KeyCode.Period,
            ["slash"] = KeyCode.Slash,
            ["/"] = KeyCode.Slash,
            ["backslash"] = KeyCode.Backslash,
            ["\\"] = KeyCode.Backslash,
            ["semicolon"] = KeyCode.Semicolon,
            [";"] = KeyCode.Semicolon,
            ["apostrophe"] = KeyCode.Apostrophe,
            ["quote"] = KeyCode.Apostrophe,
            ["'"] = KeyCode.Apostrophe,
            ["grave"] = KeyCode.Grave,
            ["backtick"] = KeyCode.Grave,
            ["tilde"] = KeyCode.Grave,
            ["`"] = KeyCode.Grave,
            ["leftbracket"] = KeyCode.LeftBracket,
            ["["] = KeyCode.LeftBracket,
            ["rightbracket"] = KeyCode.RightBracket,
            ["]"] = KeyCode.RightBracket,
            ["capslock"] = KeyCode.CapsLock,
            ["pause"] = KeyCode.Pause,
            ["printscreen"] = KeyCode.PrintScreen,
            ["menu"] = KeyCode.Menu,
        };

    /// <summary>Parse a single key token (letter, digit, F-key, or alias). Returns false if unrecognised.</summary>
    public static bool TryParse(string token, out KeyCode key)
    {
        key = KeyCode.Unknown;
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();

        if (Aliases.TryGetValue(token, out key)) return true;

        // Single letter A-Z.
        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
        {
            key = KeyCode.A + (uint)(char.ToUpperInvariant(token[0]) - 'A');
            return true;
        }

        // Single digit 0-9.
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = token[0] == '0' ? KeyCode.Digit0 : KeyCode.Digit1 + (uint)(token[0] - '1');
            return true;
        }

        // Function key F1-F24.
        if ((token[0] == 'F' || token[0] == 'f') && int.TryParse(token.AsSpan(1), out var n) &&
            n is >= 1 and <= 24)
        {
            key = n <= 12 ? KeyCode.F1 + (uint)(n - 1) : KeyCode.F13 + (uint)(n - 13);
            return true;
        }

        // Fall back to the enum's own names (e.g. "PageUp", "KpEnter").
        return Enum.TryParse(token, true, out key) && Enum.IsDefined(key);
    }

    /// <summary>Canonical display name for a key (letters/digits/F-keys rendered compactly).</summary>
    public static string Display(KeyCode key)
    {
        if (key is >= KeyCode.A and <= KeyCode.Z)
            return ((char)('A' + (key - KeyCode.A))).ToString();
        if (key == KeyCode.Digit0) return "0";
        if (key is >= KeyCode.Digit1 and <= KeyCode.Digit9)
            return ((char)('1' + (key - KeyCode.Digit1))).ToString();
        if (key is >= KeyCode.F1 and <= KeyCode.F12) return "F" + (key - KeyCode.F1 + 1);
        if (key is >= KeyCode.F13 and <= KeyCode.F24) return "F" + (key - KeyCode.F13 + 13);
        return key.ToString();
    }
}
