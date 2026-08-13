using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwShortcutLabel — a keyboard shortcut drawn as key caps rather than written out as text.
///     Takes a GTK accelerator string (<c>&lt;Control&gt;&lt;Shift&gt;n</c>, <c>&lt;Primary&gt;s</c>,
///     <c>F11</c>) and renders one rounded cap per key. Several shortcuts for the same action are
///     separated by a space in the accelerator and drawn with a dim "/" between them; an empty
///     accelerator draws <see cref="DisabledText" /> instead.
///     <para>
///         Modifiers are emitted in libadwaita 1.10's natural order — Ctrl, Alt, Shift, Super — not
///         the order they were typed in, so <c>&lt;Shift&gt;&lt;Control&gt;n</c> and
///         <c>&lt;Control&gt;&lt;Shift&gt;n</c> read identically. On macOS the caps use the platform
///         glyphs (⌃ ⌥ ⇧ ⌘) and Super becomes Command, which is the other half of the same 1.10 fix.
///     </para>
/// </summary>
public sealed class AdwShortcutLabel : Widget
{
    // `.keycap { padding: 6px; min-width: 20px }` inside a `border-spacing: 6px` box.
    private const float CapH = 26f;
    private const float CapMinW = 20f;
    private const float CapPadX = 6f;
    private const float CapGap = 6f;
    private const float SepGap = 6f;
    private const float FontSize = 12.5f;

    /// <summary>
    ///     Laid-out caps for the current accelerator. TextW is carried alongside Width because Paint
    ///     centres the glyphs in the cap and would otherwise re-shape every key on every frame —
    ///     text measurement is the expensive part of this widget, and it cannot change between
    ///     Measure and Paint.
    /// </summary>
    private readonly List<(string Text, float X, float Width, float TextW, bool Separator)> _caps =
        [];

    private string _accelerator;
    private string _disabledText = "Disabled";
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public AdwShortcutLabel(string accelerator = "") => _accelerator = accelerator;

    /// <summary>The accelerator in GTK syntax; space-separated for alternatives.</summary>
    public string Accelerator
    {
        get => _accelerator;
        set => SetLayout(field: ref _accelerator, value: value);
    }

    /// <summary>Shown when <see cref="Accelerator" /> is empty — GNOME's "Disabled".</summary>
    public string DisabledText
    {
        get => _disabledText;
        set => SetLayout(field: ref _disabledText, value: value);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _caps.Clear();

        float x = 0f;
        bool first = true;
        foreach (string accel in Accelerator.Split(
                     separator: ' ',
                     options: StringSplitOptions.RemoveEmptyEntries
                 ))
        {
            if (!first)
            {
                float sepW = TextMeasure.Width(text: "/", fontSize: FontSize);
                _caps.Add(("/", x + SepGap, sepW, sepW, true));
                x += (SepGap * 2f) + sepW;
            }

            first = false;
            foreach (string key in Parse(accel))
            {
                float textW = TextMeasure.Width(text: key, fontSize: FontSize);
                float w = MathF.Max(x: CapMinW, y: textW + (CapPadX * 2f));
                _caps.Add((key, x, w, textW, false));
                x += w + CapGap;
            }

            x -= CapGap; // the trailing gap belongs between caps, not after the last one
        }

        if (_caps.Count == 0)
        {
            float w = TextMeasure.Width(text: DisabledText, fontSize: FontSize);
            _caps.Add((DisabledText, 0f, w, w, true));
            x = w;
        }

        _size = c.Constrain(new Size(width: MathF.Max(x: 0f, y: x), height: CapH));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        var p = AdwPalette.For(_theme);
        foreach ((string text, float x, float width, float textW, bool separator) in _caps)
        {
            var cap = new Rect(
                x: Bounds.X + x,
                y: Bounds.Y,
                width: width,
                height: CapH
            );
            if (!separator)
            {
                // `.keycap { background: currentColor 10%; box-shadow: inset 0 -2px
                // var(--card-shade-color); border-radius: 6px }` — a key cap has a shaded FOOT,
                // not an outline: that bottom bevel is what makes it read as a physical key.
                paint.AddRect(bounds: cap, color: p.ButtonFill, radius: AdwMetrics.CheckRadius);
                paint.AddRect(
                    bounds: new Rect(
                        x: cap.X,
                        y: cap.Bottom - 2f,
                        width: cap.Width,
                        height: 2f
                    ),
                    color: p.CardShade,
                    radius: AdwMetrics.CheckRadius
                );
            }

            paint.AddText(
                text: text,
                baselineX: cap.X + ((width - textW) / 2f),
                baselineY: cap.Y + ((CapH - FontSize) / 2f) + (FontSize * 0.8f),
                color: separator ? p.DimLabel : _theme.OnBackground,
                fontSize: FontSize
            );
        }
    }

    /// <summary>
    ///     Split one accelerator into its display keys: modifiers first, in the fixed order GNOME
    ///     shows them, then the key itself. Unparsable input degrades to the raw string rather than
    ///     vanishing — a shortcut nobody can read still beats a blank space in the menu.
    /// </summary>
    internal static List<string> Parse(string accelerator)
    {
        List<string> keys = [];
        if (string.IsNullOrWhiteSpace(accelerator)) return keys;

        bool mac = OperatingSystem.IsMacOS();
        bool ctrl = false, alt = false, shift = false, super = false;
        string rest = accelerator;

        // <Modifier> tokens in any order and any case, as GTK accepts them.
        while (rest.StartsWith('<') && rest.IndexOf('>') is var close and > 0)
        {
            string name = rest[1..close].ToLowerInvariant();
            rest = rest[(close + 1)..];
            switch (name)
            {
                // <Primary> is Ctrl everywhere but macOS, where it is Command.
                case "primary" when mac:
                case "super" or "meta" or "windows" or "mod4":
                    super = true;
                    break;
                case "primary" or "control" or "ctrl" or "ctl":
                    ctrl = true;
                    break;
                case "alt" or "mod1" or "option":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
            }
        }

        // Natural order (libadwaita 1.10), not typed order: Ctrl, Alt, Shift, Super. macOS orders
        // its own glyph row the way the platform does — Ctrl, Option, Shift, Command.
        if (ctrl) keys.Add(mac ? "⌃" : "Ctrl");
        if (alt) keys.Add(mac ? "⌥" : "Alt");
        if (shift) keys.Add(mac ? "⇧" : "Shift");
        if (super) keys.Add(mac ? "⌘" : "Super");

        if (rest.Length > 0) keys.Add(KeyLabel(key: rest, mac: mac));
        return keys;
    }

    /// <summary>The printable cap for a GDK key name.</summary>
    private static string KeyLabel(string key, bool mac)
    {
        return key.ToLowerInvariant() switch {
            "plus" => "+",
            "minus" => "−",
            "equal" => "=",
            "space" => "Space",
            "return" or "enter" => mac ? "↩" : "Enter",
            "tab" => mac ? "⇥" : "Tab",
            "escape" or "esc" => mac ? "⎋" : "Esc",
            "backspace" => mac ? "⌫" : "Backspace",
            "delete" or "del" => mac ? "⌦" : "Delete",
            "left" => "←",
            "right" => "→",
            "up" => "↑",
            "down" => "↓",
            "page_up" or "pageup" => mac ? "⇞" : "Page Up",
            "page_down" or "pagedown" => mac ? "⇟" : "Page Down",
            "home" => mac ? "↖" : "Home",
            "end" => mac ? "↘" : "End",
            // A single letter caps up; F-keys and anything else keep their given spelling.
            _ => key.Length == 1 ? key.ToUpperInvariant() : key,
        };
    }
}
