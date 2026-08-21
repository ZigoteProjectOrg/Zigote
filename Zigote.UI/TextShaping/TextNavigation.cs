using System.Globalization;

namespace Zigote.UI.TextShaping;

/// <summary>
///     Pure text-navigation helpers shared by the editable controls (<c>TextField</c>,
///     <c>CodeEditor</c>).
///     Kept dependency-free so the caret/selection logic is unit-testable without a widget tree.
/// </summary>
public static class TextNavigation
{
    // One-entry memo of the last string's grapheme starts, keyed by reference. Every helper below
    // needed the full ParseCombiningCharacters array (an O(n) parse + fresh int[]), and callers hit
    // them per caret paint per FRAME (TextField.CaretX from Paint under the caret-blink repaint)
    // and per arrow key over whole documents — reference-equality covers ~100% of real calls
    // because editors retain their string until it is edited. [ThreadStatic] like PaintList's
    // UTF-8 cache: UI-thread-only in production, isolated under parallel test runners.
    [ThreadStatic] private static string? _startsText;
    [ThreadStatic] private static int[]? _startsCache;

    private static int[] StartsOf(string text)
    {
        if (ReferenceEquals(objA: _startsText, objB: text) && _startsCache is { } cached)
            return cached;
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        _startsText = text;
        _startsCache = starts;
        return starts;
    }

    /// <summary>
    ///     Return the nearest valid extended-grapheme boundary at or before <paramref name="index" />.
    ///     Editor positions remain UTF-16 offsets for .NET slicing, but are never allowed inside a
    ///     surrogate pair, combining sequence, emoji modifier sequence, or ZWJ emoji.
    /// </summary>
    public static int GraphemeBoundaryAtOrBefore(string text, int index)
    {
        index = Math.Clamp(value: index, min: 0, max: text.Length);
        if (index == 0 || index == text.Length) return index;
        int[] starts = StartsOf(text);
        int found = Array.BinarySearch(array: starts, value: index);
        return found >= 0 ? starts[found] : starts[Math.Max(val1: 0, val2: ~found - 1)];
    }

    /// <summary>Return the extended-grapheme boundary immediately before <paramref name="index" />.</summary>
    public static int PreviousGraphemeBoundary(string text, int index)
    {
        index = GraphemeBoundaryAtOrBefore(text: text, index: index);
        if (index <= 0) return 0;
        int[] starts = StartsOf(text);
        int found = Array.BinarySearch(array: starts, value: index);
        // found >= 0: index is itself a grapheme start → take the one before it.
        // found <  0: index is between starts (e.g. == text.Length) → ~found is the count of starts
        //             strictly before index, so the previous start is starts[~found - 1].
        int prior = found >= 0 ? found : ~found;
        return prior > 0 ? starts[prior - 1] : 0;
    }

    /// <summary>Return the extended-grapheme boundary immediately after <paramref name="index" />.</summary>
    public static int NextGraphemeBoundary(string text, int index)
    {
        index = GraphemeBoundaryAtOrBefore(text: text, index: index);
        if (index >= text.Length) return text.Length;
        int[] starts = StartsOf(text);
        int found = Array.BinarySearch(array: starts, value: index);
        int next = found >= 0 ? found + 1 : ~found;
        return next < starts.Length ? starts[next] : text.Length;
    }

    /// <summary>Enumerate all valid caret offsets, including the trailing text boundary.</summary>
    public static int[] GraphemeBoundaries(string text)
    {
        int[] starts = StartsOf(text);
        int[] result = new int[starts.Length + 1];
        starts.CopyTo(array: result, index: 0);
        result[^1] = text.Length;
        return result;
    }

    /// <summary>A character that counts as part of a word (for double-click word selection).</summary>
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    ///     The half-open span <c>[Start, End)</c> of the "word" at <paramref name="pos" /> within
    ///     <paramref name="text" />: the run of word characters when the cursor is on/just-after one,
    ///     otherwise the single character under the cursor. Returns <c>(pos, pos)</c> when there is
    ///     nothing to select (empty line / past the end on whitespace).
    /// </summary>
    public static (int Start, int End) WordAt(string text, int pos)
    {
        pos = GraphemeBoundaryAtOrBefore(text: text, index: pos);
        int start = pos, end = pos;

        if (pos < text.Length && IsWordChar(text[pos]))
        {
            while (start > 0)
            {
                int previous = PreviousGraphemeBoundary(text: text, index: start);
                if (!IsWordChar(text[previous])) break;
                start = previous;
            }

            while (end < text.Length && IsWordChar(text[end]))
                end = NextGraphemeBoundary(text: text, index: end);
        }
        else if (pos > 0)
        {
            int previous = PreviousGraphemeBoundary(text: text, index: pos);
            if (IsWordChar(text[previous]))
            {
                start = previous;
                while (start > 0)
                {
                    previous = PreviousGraphemeBoundary(text: text, index: start);
                    if (!IsWordChar(text[previous])) break;
                    start = previous;
                }
            }
            else if (pos < text.Length) end = NextGraphemeBoundary(text: text, index: pos);
        }
        else if (pos < text.Length)
        {
            end = NextGraphemeBoundary(
                text: text,
                index: pos
            ); // one non-word grapheme (operator, emoji, etc.)
        }

        return (start, end);
    }
}
