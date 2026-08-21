using System.Collections.Concurrent;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;

namespace Zigote.UI.TextShaping;

/// <summary>
///     Single source of truth for text sizing across the widget framework.
///     <para>
///         Backed by the engine's HarfBuzz-aware <see cref="ZigoteEngine.MeasureText" /> and memoised
///         by <c>(text, size, weight, style)</c>, so repeatedly measuring the same label across frames
///         costs a dictionary lookup instead of a fresh FFI call + UTF-8 encode. When no engine is
///         available (unit tests, pre-init), it falls back to a coarse proportional estimate.
///     </para>
///     Replaces the ad-hoc <c>text.Length * fontSize * 0.55f</c> heuristics that were scattered
///     across Label, Badge, Tooltip, ContextMenu, Snackbar, TabBar and Dropdown.
/// </summary>
public static class TextMeasure
{
    // Bound to keep dynamic text (counters, timers) from growing the cache without limit.
    private const int MaxEntries = 8192;

    // Two generations: when the current one fills up it demotes to _previous (whose contents are
    // dropped) instead of clearing outright, so entries still being hit survive eviction — a hit
    // in _previous promotes the entry back into the current generation.
    private static ConcurrentDictionary<Key, Size> _cache = new(KeyComparer.Instance);
    private static ConcurrentDictionary<Key, Size> _previous = new(KeyComparer.Instance);

    /// <summary>
    ///     Span-based measurement for callers that slice a larger string (RichText word wrap,
    ///     ellipsis fitting): a cache hit — the steady state — costs zero allocations. Only a cold
    ///     miss materialises the slice into a string, which the cache then retains.
    /// </summary>
    public static Size Measure(
        ReadOnlySpan<char> text,
        float fontSize,
        FontWeight weight = FontWeight.Normal,
        FontStyle style = FontStyle.Normal,
        string? fontFamily = null,
        float letterSpacing = 0f,
        float wordSpacing = 0f)
    {
        if (text.IsEmpty || fontSize <= 0f) return Size.Zero;

        var alt = new SpanKey(
            text: text,
            size: fontSize,
            weight: weight,
            style: style,
            fontFamily: fontFamily,
            letterSpacing: letterSpacing,
            wordSpacing: wordSpacing
        );
        if (_cache.GetAlternateLookup<SpanKey>().TryGetValue(key: alt, value: out var size))
            return size;
        if (_previous.GetAlternateLookup<SpanKey>()
            .TryGetValue(key: alt, actualKey: out var promoted, value: out size))
        {
            _cache[promoted] = size; // promote with the existing key — no new string
            return size;
        }

        return Measure(
            text: text.ToString(),
            fontSize: fontSize,
            weight: weight,
            style: style,
            fontFamily: fontFamily,
            letterSpacing: letterSpacing,
            wordSpacing: wordSpacing
        );
    }

    /// <inheritdoc cref="Measure(System.ReadOnlySpan{char},float,FontWeight,FontStyle,string?,float,float)" />
    public static float Width(
        ReadOnlySpan<char> text,
        float fontSize,
        FontWeight weight = FontWeight.Normal,
        FontStyle style = FontStyle.Normal,
        string? fontFamily = null,
        float letterSpacing = 0f,
        float wordSpacing = 0f)
    {
        return Measure(
            text: text,
            fontSize: fontSize,
            weight: weight,
            style: style,
            fontFamily: fontFamily,
            letterSpacing: letterSpacing,
            wordSpacing: wordSpacing
        ).Width;
    }

    /// <summary>Measured size of <paramref name="text" /> at the given font size / weight / style.</summary>
    public static Size Measure(
        string text,
        float fontSize,
        FontWeight weight = FontWeight.Normal,
        FontStyle style = FontStyle.Normal,
        string? fontFamily = null,
        float letterSpacing = 0f,
        float wordSpacing = 0f)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0f) return Size.Zero;

        var key = new Key(
            Text: text,
            Size: fontSize,
            Weight: weight,
            Style: style,
            FontFamily: fontFamily,
            LetterSpacing: letterSpacing,
            WordSpacing: wordSpacing
        );
        if (_cache.TryGetValue(key: key, value: out var cached)) return cached;
        if (_previous.TryGetValue(key: key, value: out cached))
        {
            _cache[key] = cached;
            return cached;
        }

        var engine = ZigoteEngine.Instance;
        var size = engine is not null
            ? engine.MeasureText(
                text: text,
                fontSize: fontSize,
                weight: weight,
                style: style,
                letterSpacing: letterSpacing,
                wordSpacing: wordSpacing,
                fontFamily: fontFamily
            )
            : new Size(width: text.Length * fontSize * 0.55f, height: fontSize * 1.2f);

        if (_cache.Count >= MaxEntries)
        {
            var retired = _previous;
            retired.Clear();
            _previous = _cache;
            _cache = retired;
        }

        _cache[key] = size;
        return size;
    }

    /// <summary>Convenience width-only measurement.</summary>
    public static float Width(
        string text,
        float fontSize,
        FontWeight weight = FontWeight.Normal,
        FontStyle style = FontStyle.Normal,
        string? fontFamily = null,
        float letterSpacing = 0f,
        float wordSpacing = 0f)
    {
        return Measure(
            text: text,
            fontSize: fontSize,
            weight: weight,
            style: style,
            fontFamily: fontFamily,
            letterSpacing: letterSpacing,
            wordSpacing: wordSpacing
        ).Width;
    }

    /// <summary>Drop all memoised measurements (e.g. after a font swap).</summary>
    public static void Invalidate()
    {
        _cache.Clear();
        _previous.Clear();
    }

    private readonly record struct Key(
        string Text,
        float Size,
        FontWeight Weight,
        FontStyle Style,
        string? FontFamily,
        float LetterSpacing,
        float WordSpacing);

    /// <summary>A <see cref="Key" /> whose text is a slice of some larger string — lookup only.</summary>
    private readonly ref struct SpanKey(
        ReadOnlySpan<char> text,
        float size,
        FontWeight weight,
        FontStyle style,
        string? fontFamily,
        float letterSpacing,
        float wordSpacing)
    {
        public readonly ReadOnlySpan<char> Text = text;
        public readonly float Size = size;
        public readonly FontWeight Weight = weight;
        public readonly FontStyle Style = style;
        public readonly string? FontFamily = fontFamily;
        public readonly float LetterSpacing = letterSpacing;
        public readonly float WordSpacing = wordSpacing;
    }

    // Ordinal-string equality identical to Key's record semantics, plus the span-keyed alternate
    // view — the hash MUST agree between Key and SpanKey or alternate lookups silently miss.
    private sealed class KeyComparer : IEqualityComparer<Key>,
        IAlternateEqualityComparer<SpanKey, Key>
    {
        public static readonly KeyComparer Instance = new();

        public Key Create(SpanKey a) => new(
            Text: a.Text.ToString(),
            Size: a.Size,
            Weight: a.Weight,
            Style: a.Style,
            FontFamily: a.FontFamily,
            LetterSpacing: a.LetterSpacing,
            WordSpacing: a.WordSpacing
        );

        public bool Equals(SpanKey a, Key k) =>
            a.Size == k.Size && a.Weight == k.Weight && a.Style == k.Style &&
            a.LetterSpacing == k.LetterSpacing && a.WordSpacing == k.WordSpacing &&
            string.Equals(a: a.FontFamily, b: k.FontFamily, comparisonType: StringComparison.Ordinal) &&
            a.Text.SequenceEqual(k.Text);

        public int GetHashCode(SpanKey a) => Hash(
            text: a.Text,
            size: a.Size,
            weight: a.Weight,
            style: a.Style,
            fontFamily: a.FontFamily,
            letterSpacing: a.LetterSpacing,
            wordSpacing: a.WordSpacing
        );

        public bool Equals(Key x, Key y) => x.Equals(y);

        public int GetHashCode(Key k) => Hash(
            text: k.Text,
            size: k.Size,
            weight: k.Weight,
            style: k.Style,
            fontFamily: k.FontFamily,
            letterSpacing: k.LetterSpacing,
            wordSpacing: k.WordSpacing
        );

        private static int Hash(ReadOnlySpan<char> text, float size, FontWeight weight,
            FontStyle style, string? fontFamily, float letterSpacing, float wordSpacing)
        {
            return HashCode.Combine(
                value1: string.GetHashCode(value: text, comparisonType: StringComparison.Ordinal),
                value2: size,
                value3: weight,
                value4: style,
                value5: fontFamily is null
                    ? 0
                    : string.GetHashCode(value: fontFamily, comparisonType: StringComparison.Ordinal),
                value6: letterSpacing,
                value7: wordSpacing
            );
        }
    }
}
