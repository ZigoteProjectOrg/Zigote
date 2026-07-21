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
    private static ConcurrentDictionary<Key, Size> _cache = new();
    private static ConcurrentDictionary<Key, Size> _previous = new();

    /// <summary>Measured size of <paramref name="text" /> at the given font size / weight / style.</summary>
    public static Size Measure(
        string text,
        float fontSize,
        FontWeight weight = FontWeight.Normal,
        FontStyle style = FontStyle.Normal,
        string? fontFamily = null)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0f) return Size.Zero;

        var key = new Key(
            text,
            fontSize,
            weight,
            style,
            fontFamily
        );
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (_previous.TryGetValue(key, out cached))
        {
            _cache[key] = cached;
            return cached;
        }

        var engine = ZigoteEngine.Instance;
        var size = engine is not null
            ? engine.MeasureText(
                text,
                fontSize,
                weight: weight,
                style: style,
                fontFamily: fontFamily
            )
            : new Size(text.Length * fontSize * 0.55f, fontSize * 1.2f);

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
        string? fontFamily = null)
    {
        return Measure(
            text,
            fontSize,
            weight,
            style,
            fontFamily
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
        string? FontFamily);
}