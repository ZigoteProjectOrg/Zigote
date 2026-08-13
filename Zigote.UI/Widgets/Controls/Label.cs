using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Controls;

/// <summary>How a label resolves text that does not fit its measured width.</summary>
public enum TextOverflow
{
    /// <summary>Hard-clip the overflowing glyphs at the box edge.</summary>
    Clip,

    /// <summary>Truncate and append a trailing ellipsis sized to fit.</summary>
    Ellipsis,
}

/// <summary>Renders a single string of text in the flat macOS type scale.</summary>
public class Label : Widget
{
    public enum LabelStyle
    {
        Body,
        Caption,
        Title,
    }

    private const string Ellipsis = "…";

    // Word-wrapped lines for the multi-line path, cached by (text, width, font inputs, line cap) so a
    // re-measure with unchanged inputs allocates nothing (the paint/measure hot path stays zero-GC).
    private readonly List<string> _lines = [];
    private string _drawText = "";

    // Resolved at Measure time and reused by Paint so the two passes never disagree.
    private float _fontSize;
    private bool _multiline;

    private Size _size;
    private string _text;
    private ThemeData _theme = ThemeData.Dark;
    private bool _truncated;
    private string? _wrapFamily;
    private float _wrapFs = -1f;
    private int? _wrapMaxLines;
    private TextOverflow _wrapOverflow = TextOverflow.Clip;
    private FontStyle _wrapStyle = FontStyle.Normal;
    private string? _wrapText;
    private FontWeight _wrapWeight = FontWeight.Normal;
    private float _wrapWidth = -1f;

    public Label(string text) => _text = text;

    public Label(string text, float fontSize, Color color)
    {
        _text = text;
        FontSize = fontSize;
        Color = color;
    }

    public Label(string text, float fontSize)
    {
        _text = text;
        FontSize = fontSize;
    }

    /// <summary>
    ///     Drive the label from a <see cref="TextStyle" /> — size, weight, leading, slant and
    ///     font family in one shot (e.g. <c>new Label(code, Typography.Code)</c> for Iosevka).
    /// </summary>
    public Label(string text, TextStyle style, Color? color = null)
    {
        _text = text;
        FontSize = style.Size;
        FontWeight = style.Weight;
        FontStyle = style.Style;
        LineHeight = style.LineHeight;
        FontFamily = style.FontFamily;
        Color = color;
    }

    public TextAlign Align { get; set; } = TextAlign.Left;

    public string Text
    {
        get => _text;
        set => SetLayout(field: ref _text, value: value);
    }

    public LabelStyle Style { get; set; } = LabelStyle.Body;
    public float? FontSize { get; set; }
    public Color? Color { get; set; }
    public FontWeight FontWeight { get; set; } = FontWeight.Normal;
    public FontStyle FontStyle { get; set; } = FontStyle.Normal;
    public float? LineHeight { get; set; }
    public float LetterSpacing { get; set; } = 0f;

    /// <summary>
    ///     Optional font-family name (e.g. an icon or monospace face loaded via the engine).
    ///     <c>null</c> uses the default UI font (Inter).
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>Optional cap on rendered lines. <c>null</c> leaves wrapping unbounded.</summary>
    public int? MaxLines { get; set; }

    /// <summary>How overflowing text is resolved when it exceeds the available width.</summary>
    public TextOverflow Overflow { get; set; } = TextOverflow.Clip;

    /// <summary>
    ///     Exclude this label from the accessibility tree — for purely decorative text already announced
    ///     by an enclosing control (rare; composed controls mark their own inner labels as leaves
    ///     instead).
    /// </summary>
    public bool Decorative { get; set; }

    public override bool ExcludeSemantics => Decorative;

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        if (string.IsNullOrEmpty(Text)) return;
        config.Role = Style == LabelStyle.Title ? SemanticsRole.Header : SemanticsRole.Text;
        config.Label = Text;
    }

    public static Label Body(string text) => new(text) { Style = LabelStyle.Body };

    public static Label Caption(string text) => new(text) { Style = LabelStyle.Caption };

    public static Label Title(string text) => new(text) { Style = LabelStyle.Title };

    private float ResolveFontSize()
    {
        return FontSize ?? Style switch {
            LabelStyle.Caption => _theme.FontSizeCaption,
            LabelStyle.Title => _theme.FontSizeTitle,
            _ => _theme.FontSizeBody,
        };
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _fontSize = ResolveFontSize();
        float lh = LineHeight ?? _theme.LineHeight;

        bool singleLine = MaxLines == 1;

        // Multi-line (the default when MaxLines != 1) with a bounded width: word-wrap here and paint each
        // line separately. AddText draws ONE unwrapped line, so without this a Text wider than its box runs
        // off the right edge instead of wrapping. An unbounded width has no wrap point → single-line path.
        if (!singleLine && float.IsFinite(c.MaxWidth) && c.MaxWidth > 0f &&
            !string.IsNullOrEmpty(Text))
        {
            EnsureWrapped(c.MaxWidth);
            if (_lines.Count > 1)
            {
                _multiline = true;
                float widest = 0f;
                foreach (string line in _lines)
                {
                    widest = MathF.Max(
                        x: widest,
                        y: TextMeasure.Width(
                            text: line,
                            fontSize: _fontSize,
                            weight: FontWeight,
                            style: FontStyle,
                            fontFamily: FontFamily,
                            letterSpacing: LetterSpacing
                        )
                    );
                }

                _size = c.Constrain(new Size(width: widest, height: _lines.Count * _fontSize * lh));
                return _size;
            }
        }

        _multiline = false;

        // Single-line: measure intrinsically, then resolve overflow against MaxWidth.
        var full = TextMeasure.Measure(
            text: Text,
            fontSize: _fontSize,
            weight: FontWeight,
            style: FontStyle,
            fontFamily: FontFamily,
            letterSpacing: LetterSpacing
        );
        float lineH = full.Height > 0f ? full.Height : _fontSize * lh;

        _drawText = Text;
        _truncated = false;

        // Zero is a real width, not "unmeasured": a flex child squeezed to nothing by its siblings
        // gets MaxWidth 0, and reporting the full intrinsic width there made the label paint straight
        // over whatever took the space — the queue toolbar's summary line across its own buttons.
        // Unbounded stays unbounded: MaxWidth is +∞ then, so the comparison is false either way.
        if (full.Width > c.MaxWidth && !string.IsNullOrEmpty(Text))
        {
            _truncated = true;
            _drawText = Overflow == TextOverflow.Ellipsis
                ? Fit(text: Text, maxWidth: c.MaxWidth)
                : Text;
            _size = c.Constrain(new Size(width: c.MaxWidth, height: lineH));
            return _size;
        }

        _size = c.Constrain(new Size(width: full.Width, height: lineH));
        return _size;
    }

    /// <summary>
    ///     Greedy word-wrap of <see cref="Text" /> to <paramref name="maxWidth" />, honouring explicit
    ///     newlines and capping at <see cref="MaxLines" /> (ellipsizing the last line when
    ///     <see cref="Overflow" /> is Ellipsis). Cached by (text, width, font inputs, line cap) so a
    ///     repeated measure with unchanged inputs is a no-op.
    /// </summary>
    private void EnsureWrapped(float maxWidth)
    {
        if (_wrapText == Text && _wrapWidth == maxWidth && _wrapFs == _fontSize &&
            _wrapWeight == FontWeight && _wrapStyle == FontStyle && _wrapFamily == FontFamily &&
            _wrapMaxLines == MaxLines && _wrapOverflow == Overflow)
            return;
        _wrapText = Text;
        _wrapWidth = maxWidth;
        _wrapFs = _fontSize;
        _wrapWeight = FontWeight;
        _wrapStyle = FontStyle;
        _wrapFamily = FontFamily;
        _wrapMaxLines = MaxLines;
        _wrapOverflow = Overflow;

        // Line widths are per-word advances summed with the space advance — one TextMeasure entry
        // per word instead of one per growing line prefix (kerning drift vs the shaped whole line
        // is acceptable, as in the selection advance cache).
        float spaceW = TextMeasure.Width(
            text: " ",
            fontSize: _fontSize,
            weight: FontWeight,
            style: FontStyle,
            fontFamily: FontFamily,
            letterSpacing: LetterSpacing
        );

        _lines.Clear();
        foreach (string hardLine in Text.Split('\n'))
        {
            if (hardLine.Length == 0)
            {
                _lines.Add(string.Empty);
                continue;
            }

            string cur = string.Empty;
            float curW = 0f;
            foreach (string word in hardLine.Split(' '))
            {
                float wordW = TextMeasure.Width(
                    text: word,
                    fontSize: _fontSize,
                    weight: FontWeight,
                    style: FontStyle,
                    fontFamily: FontFamily,
                    letterSpacing: LetterSpacing
                );
                if (cur.Length == 0)
                {
                    cur = word;
                    curW = wordW;
                    continue;
                }

                if (curW + spaceW + wordW <= maxWidth)
                {
                    cur = cur + " " + word;
                    curW += spaceW + wordW;
                }
                else
                {
                    _lines.Add(cur);
                    cur = word;
                    curW = wordW;
                }
            }

            _lines.Add(cur);
        }

        if (MaxLines is { } ml && ml > 0 && _lines.Count > ml)
        {
            string remainder = string.Join(
                separator: ' ',
                values: _lines.GetRange(index: ml - 1, count: _lines.Count - (ml - 1))
            );
            _lines.RemoveRange(index: ml - 1, count: _lines.Count - (ml - 1));
            _lines.Add(
                Overflow == TextOverflow.Ellipsis
                    ? Fit(text: remainder, maxWidth: maxWidth)
                    : remainder
            );
        }
    }

    /// <summary>
    ///     Longest leading slice of <paramref name="text" /> that, plus an ellipsis, fits in
    ///     <paramref name="maxWidth" />
    ///     .
    /// </summary>
    private string Fit(string text, float maxWidth)
    {
        float ellipsisW = TextMeasure.Width(
            text: Ellipsis,
            fontSize: _fontSize,
            weight: FontWeight,
            style: FontStyle,
            fontFamily: FontFamily,
            letterSpacing: LetterSpacing
        );
        if (ellipsisW >= maxWidth) return Ellipsis;

        float budget = maxWidth - ellipsisW;

        // Binary search the longest prefix whose width fits the budget.
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            float w = TextMeasure.Width(
                text: text[..mid],
                fontSize: _fontSize,
                weight: FontWeight,
                style: FontStyle,
                fontFamily: FontFamily,
                letterSpacing: LetterSpacing
            );
            if (w <= budget) lo = mid;
            else hi = mid - 1;
        }

        return text[..lo].TrimEnd() + Ellipsis;
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

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: Text,
            value2: Color?.R,
            value3: Color?.G,
            value4: Color?.B,
            value5: FontWeight,
            value6: _truncated
        );
    }

    public override void Paint(PaintList paint)
    {
        // Be robust when painted without a preceding Measure (some raw-Widget hosts build and paint
        // labels directly each frame). Resolve the font size and draw text on demand in that case.
        if (_fontSize <= 0f)
        {
            _fontSize = ResolveFontSize();
            _drawText = Text;
        }

        if (_fontSize <= 0f) return;

        var color = Color ?? Style switch {
            LabelStyle.Caption => _theme.Label2,
            LabelStyle.Title => _theme.OnSurface,
            _ => _theme.Label1,
        };

        float lh = LineHeight ?? _theme.LineHeight;

        // Multi-line: draw each wrapped line at its own baseline, clipped to the box.
        if (_multiline)
        {
            float lineH = _fontSize * lh;
            paint.AddClipStart(Bounds);
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i];
                if (line.Length == 0) continue;
                float lineW = TextMeasure.Width(
                    text: line,
                    fontSize: _fontSize,
                    weight: FontWeight,
                    style: FontStyle,
                    fontFamily: FontFamily,
                    letterSpacing: LetterSpacing
                );
                float lineX = Align switch {
                    TextAlign.Center => Bounds.X + ((Bounds.Width - lineW) / 2f),
                    TextAlign.Right => Bounds.Right - lineW,
                    _ => Bounds.X,
                };
                paint.AddText(
                    text: line,
                    baselineX: lineX,
                    baselineY: Bounds.Y + (_fontSize * 0.8f) + (i * lineH),
                    color: color,
                    fontSize: _fontSize,
                    lineHeight: lh,
                    fontWeight: FontWeight,
                    fontStyle: FontStyle,
                    letterSpacing: LetterSpacing,
                    fontFamily: FontFamily
                );
            }

            paint.AddClipEnd();
            return;
        }

        string draw = _drawText.Length == 0 && !string.IsNullOrEmpty(Text) ? Text : _drawText;
        if (string.IsNullOrEmpty(draw)) return;

        float drawW = TextMeasure.Width(
            text: draw,
            fontSize: _fontSize,
            weight: FontWeight,
            style: FontStyle,
            fontFamily: FontFamily,
            letterSpacing: LetterSpacing
        );

        float drawX = Align switch {
            TextAlign.Center => Bounds.X + ((Bounds.Width - drawW) / 2f),
            TextAlign.Right => Bounds.Right - drawW,
            _ => Bounds.X,
        };
        // baseline ≈ top + font_size * 0.8
        float baseline = Bounds.Y + (_fontSize * 0.8f);

        // Clip only when the text genuinely overflows its box. A bare MaxLines cap on text that
        // fits must NOT clip: glyph ink (side bearings, AA fringe) can extend a pixel or two past
        // the summed advances, and hard-clipping at the measured width shaves the last glyph.
        bool needsClip = _truncated;
        if (needsClip) paint.AddClipStart(Bounds);

        paint.AddText(
            text: draw,
            baselineX: drawX,
            baselineY: baseline,
            color: color,
            fontSize: _fontSize,
            lineHeight: lh,
            fontWeight: FontWeight,
            fontStyle: FontStyle,
            letterSpacing: LetterSpacing,
            fontFamily: FontFamily
        );

        if (needsClip) paint.AddClipEnd();
    }
}
