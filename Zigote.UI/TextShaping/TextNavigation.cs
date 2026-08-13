using System.Globalization;

namespace Zigote.UI.TextShaping;

/// <summary>
///     Pure text-navigation helpers shared by the editable controls (<c>TextField</c>,
///     <c>CodeEditor</c>).
///     Kept dependency-free so the caret/selection logic is unit-testable without a widget tree.
/// </summary>
public static class TextNavigation
{
    /// <summary>
    ///     Return the nearest valid extended-grapheme boundary at or before <paramref name="index" />.
    ///     Editor positions remain UTF-16 offsets for .NET slicing, but are never allowed inside a
    ///     surrogate pair, combining sequence, emoji modifier sequence, or ZWJ emoji.
    /// </summary>
    public static int GraphemeBoundaryAtOrBefore(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        if (index == 0 || index == text.Length) return index;
        var starts = StringInfo.ParseCombiningCharacters(text);
        var found = Array.BinarySearch(starts, index);
        return found >= 0 ? starts[found] : starts[Math.Max(0, ~found - 1)];
    }

    /// <summary>Return the extended-grapheme boundary immediately before <paramref name="index" />.</summary>
    public static int PreviousGraphemeBoundary(string text, int index)
    {
        index = GraphemeBoundaryAtOrBefore(text, index);
        if (index <= 0) return 0;
        var starts = StringInfo.ParseCombiningCharacters(text);
        var found = Array.BinarySearch(starts, index);
        // found >= 0: index is itself a grapheme start → take the one before it.
        // found <  0: index is between starts (e.g. == text.Length) → ~found is the count of starts
        //             strictly before index, so the previous start is starts[~found - 1].
        var prior = found >= 0 ? found : ~found;
        return prior > 0 ? starts[prior - 1] : 0;
    }

    /// <summary>Return the extended-grapheme boundary immediately after <paramref name="index" />.</summary>
    public static int NextGraphemeBoundary(string text, int index)
    {
        index = GraphemeBoundaryAtOrBefore(text, index);
        if (index >= text.Length) return text.Length;
        var starts = StringInfo.ParseCombiningCharacters(text);
        var found = Array.BinarySearch(starts, index);
        var next = found >= 0 ? found + 1 : ~found;
        return next < starts.Length ? starts[next] : text.Length;
    }

    /// <summary>Enumerate all valid caret offsets, including the trailing text boundary.</summary>
    public static int[] GraphemeBoundaries(string text)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        var result = new int[starts.Length + 1];
        starts.CopyTo(result, 0);
        result[^1] = text.Length;
        return result;
    }

    /// <summary>A character that counts as part of a word (for double-click word selection).</summary>
    public static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    /// <summary>
    ///     The half-open span <c>[Start, End)</c> of the "word" at <paramref name="pos" /> within
    ///     <paramref name="text" />: the run of word characters when the cursor is on/just-after one,
    ///     otherwise the single character under the cursor. Returns <c>(pos, pos)</c> when there is
    ///     nothing to select (empty line / past the end on whitespace).
    /// </summary>
    public static (int Start, int End) WordAt(string text, int pos)
    {
        pos = GraphemeBoundaryAtOrBefore(text, pos);
        int start = pos, end = pos;

        if (pos < text.Length && IsWordChar(text[pos]))
        {
            while (start > 0)
            {
                var previous = PreviousGraphemeBoundary(text, start);
                if (!IsWordChar(text[previous])) break;
                start = previous;
            }

            while (end < text.Length && IsWordChar(text[end]))
                end = NextGraphemeBoundary(text, end);
        }
        else if (pos > 0)
        {
            var previous = PreviousGraphemeBoundary(text, pos);
            if (IsWordChar(text[previous]))
            {
                start = previous;
                while (start > 0)
                {
                    previous = PreviousGraphemeBoundary(text, start);
                    if (!IsWordChar(text[previous])) break;
                    start = previous;
                }
            }
            else if (pos < text.Length)
            {
                end = NextGraphemeBoundary(text, pos);
            }
        }
        else if (pos < text.Length)
        {
            end = NextGraphemeBoundary(text, pos); // one non-word grapheme (operator, emoji, etc.)
        }

        return (start, end);
    }
}
