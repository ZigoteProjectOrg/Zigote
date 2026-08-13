using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     A <see cref="RichText" /> whose text can be selected with the mouse and copied — the
///     read-only counterpart of <c>TextField</c> selection. Drag to select across wrapped lines and
///     styled spans, double-click for word select, ⌘/Ctrl+A / ⌘/Ctrl+C, Shift+arrows to extend,
///     Esc to clear.
///     <para>
///         Character geometry (per-grapheme caret positions) is rebuilt only when the underlying run
///         layout is rebuilt, so pointer-drag tracking and selection painting are pure array lookups
///         — zero allocation on the hot path.
///     </para>
/// </summary>
public class SelectableText : RichText
{
    private const float DoubleClickSeconds = 0.4f;

    // SDL scancodes (KeyCode values) — same constants TextField keys off.
    private const uint ScRight = 79, ScLeft = 80, ScEscape = 41, ScHome = 74, ScEnd = 77;

    private int _anchor = -1;
    private int _charCount; // valid entries = FullText.Length + 1
    private int[] _charLine = [];

    // Caret x before each char (line-relative, pre-alignment) + the line each caret sits on.
    // Index [FullText.Length] is the after-last position. Grow-only, rebuilt in OnLayoutRebuilt.
    private float[] _charX = [];
    private bool _dragging;
    private int _extent = -1;
    private int _lastClickPos = -1;
    private float _lastClickTime = -10f;
    private int[] _lineStartChar = []; // first char index per line + end sentinel

    public SelectableText() { }

    public SelectableText(IEnumerable<TextSpan> spans) : base(spans) { }

    public SelectableText(params TextSpan[] spans) : base(spans) { }

    /// <summary>Convenience for plain (single-style) selectable text.</summary>
    public SelectableText(string text) : base(new TextSpan(text)) { }

    public override bool Focusable => true;

    /// <summary>Arrows move the selection caret; keep them away from directional focus nav.</summary>
    public override bool HandlesDirectionalKeys => true;

    public bool HasSelection => _anchor >= 0 && _extent >= 0 && _anchor != _extent;
    public int SelectionStart => HasSelection ? Math.Min(val1: _anchor, val2: _extent) : -1;
    public int SelectionEnd => HasSelection ? Math.Max(val1: _anchor, val2: _extent) : -1;

    public string SelectedText =>
        HasSelection ? FullText[SelectionStart..SelectionEnd] : string.Empty;

    public void ClearSelection()
    {
        if (_anchor < 0 && _extent < 0) return;
        _anchor = -1;
        _extent = -1;
        MarkNeedsPaint();
    }

    public void SelectAll()
    {
        if (FullText.Length == 0) return;
        _anchor = 0;
        _extent = FullText.Length;
        MarkNeedsPaint();
    }

    public override MouseCursor? GetCursor(Offset point) => MouseCursor.Text;

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        base.DescribeSemantics(config);
        config.Flags |= SemanticsFlags.Focusable;
        if (Focused) config.Flags |= SemanticsFlags.Focused;
    }

    // ── Pointer interaction (App captures the pointer between down and up) ──

    public override void OnPointerDown(Offset point)
    {
        int pos = IndexAtPoint(point);

        float now = App.Active?.Time ?? 0f;
        if (now - _lastClickTime < DoubleClickSeconds && Math.Abs(pos - _lastClickPos) <= 1)
        {
            (int start, int end) = TextNavigation.WordAt(text: FullText, pos: pos);
            _dragging = false;
            if (start != end)
            {
                _anchor = start;
                _extent = end;
                MarkNeedsPaint();
            }

            return;
        }

        _lastClickTime = now;
        _lastClickPos = pos;
        _anchor = pos;
        _extent = pos;
        _dragging = true;
        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        if (!_dragging) return;
        int pos = IndexAtPoint(point);
        if (pos == _extent) return;
        _extent = pos;
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        _dragging = false;
        if (_anchor == _extent) ClearSelection();
    }

    public override void OnPointerCancel()
    {
        // A scroller claimed the drag (touch) or the OS took the gesture: stay disarmed, or the
        // next press would extend this stale selection instead of starting a new one.
        _dragging = false;
    }

    /// <summary>
    ///     Press-and-hold selects the word under the finger — drag-select needs a pointer the page
    ///     scroller isn't already using, so it is a mouse-only affordance.
    /// </summary>
    public override void OnLongPress(Offset point)
    {
        (int start, int end) = TextNavigation.WordAt(text: FullText, pos: IndexAtPoint(point));
        if (start == end) return;
        _dragging = false;
        _anchor = start;
        _extent = end;
        MarkNeedsPaint();
    }

    // ── Keyboard ──

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;

        if (mods.HasCommand())
        {
            switch (char.ToLowerInvariant(keyChar))
            {
                case 'a':
                    SelectAll();
                    return;
                case 'c':
                    if (HasSelection) ZigoteEngine.Instance?.SetClipboard(SelectedText);
                    return;
            }

            return;
        }

        bool shift = (mods & Modifiers.Shift) != 0;
        switch (scancode)
        {
            case ScEscape:
                ClearSelection();
                return;
            case ScLeft:
                MoveCaret(
                    extend: shift,
                    pos: TextNavigation.PreviousGraphemeBoundary(
                        text: FullText,
                        index: CaretForMove()
                    )
                );
                return;
            case ScRight:
                MoveCaret(
                    extend: shift,
                    pos: TextNavigation.NextGraphemeBoundary(text: FullText, index: CaretForMove())
                );
                return;
            case ScHome:
                MoveCaret(extend: shift, pos: 0);
                return;
            case ScEnd:
                MoveCaret(extend: shift, pos: FullText.Length);
                return;
        }
    }

    private int CaretForMove() => _extent >= 0 ? _extent : 0;

    private void MoveCaret(bool extend, int pos)
    {
        pos = Math.Clamp(value: pos, min: 0, max: FullText.Length);
        if (extend)
        {
            if (_anchor < 0) _anchor = CaretForMove();
            _extent = pos;
        }
        else
        {
            _anchor = pos;
            _extent = pos;
        }

        MarkNeedsPaint();
    }

    // ── Painting: translucent selection wash under the styled runs ──

    public override void Paint(PaintList paint)
    {
        if (HasSelection && _charCount > 0)
        {
            int selMin = Math.Clamp(value: SelectionStart, min: 0, max: _charCount - 1);
            int selMax = Math.Clamp(value: SelectionEnd, min: 0, max: _charCount - 1);

            for (int r = 0; r < RunCount; r++)
            {
                ref readonly var run = ref Runs[r];
                int runEnd = Math.Min(val1: run.CharStart + run.Slice.Length, val2: _charCount - 1);
                int s0 = Math.Max(val1: run.CharStart, val2: selMin);
                int s1 = Math.Min(val1: runEnd, val2: selMax);
                if (s1 <= s0) continue;

                float x0 = _charX[s0];
                float x1 = s1 < runEnd ? _charX[s1] : run.X + run.Width;
                var line = LineOf(run.Line);
                float alignOff = LineAlignOffset(run.Line);
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X + alignOff + MathF.Min(x: x0, y: x1),
                        y: Bounds.Y + line.Top,
                        width: MathF.Max(x: 1f, y: MathF.Abs(x1 - x0)),
                        height: line.Height
                    ),
                    color: ThemeRef.SelectionTint
                );
            }
        }

        base.Paint(paint);
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: base.DebugStateHash(),
            value2: _anchor,
            value3: _extent,
            value4: Focused
        );
    }

    // ── Char geometry (cold: rebuilt only when the run layout is rebuilt) ──

    internal override void OnLayoutRebuilt()
    {
        int len = FullText.Length;
        _charCount = len + 1;
        if (_charX.Length < _charCount)
        {
            _charX = new float[_charCount];
            _charLine = new int[_charCount];
        }

        float prevX = 0f;
        int prevLine = 0;
        int filled = 0;

        for (int r = 0; r < RunCount; r++)
        {
            ref readonly var run = ref Runs[r];
            var span = SpanAt(run.Span);

            // Chars dropped by wrapping (collapsed spaces, hard newlines) sit at the end of the
            // preceding placed content.
            int runStart = Math.Min(val1: run.CharStart, val2: len);
            for (int i = filled; i < runStart; i++)
            {
                _charX[i] = prevX;
                _charLine[i] = prevLine;
            }

            filled = Math.Max(val1: filled, val2: runStart);

            // Per-grapheme cumulative advances within the run (the TextField advance-cache pattern:
            // per-grapheme widths, summed — kerning drift vs the shaped whole run is acceptable).
            // An ellipsized run's slice is shorter than its source char range; the surplus source
            // chars fall into the gap-fill above on the next iteration (approximate by design).
            string slice = run.Slice;
            int[] boundaries = TextNavigation.GraphemeBoundaries(slice);
            float cum = 0f;
            for (int g = 0; g < boundaries.Length - 1; g++)
            {
                int a = boundaries[g];
                int b = boundaries[g + 1];
                for (int k = a; k < b; k++)
                {
                    int idx = run.CharStart + k;
                    if (idx >= len) break;
                    _charX[idx] = run.X + cum;
                    _charLine[idx] = run.Line;
                    if (idx >= filled) filled = idx + 1;
                }

                cum += SpanWidth(s: span, text: slice[a..b]);
            }

            prevX = run.X + cum;
            prevLine = run.Line;
        }

        for (int i = filled; i < _charCount; i++)
        {
            _charX[i] = prevX;
            _charLine[i] = prevLine;
        }

        // Per-line char ranges: _charLine is non-decreasing (runs are placed in line order), so
        // line L owns [_lineStartChar[L], _lineStartChar[L + 1]) and IndexAtPoint scans one line
        // instead of the whole text.
        int lineCount = Math.Max(val1: 1, val2: LineCount);
        if (_lineStartChar.Length < lineCount + 1) _lineStartChar = new int[lineCount + 1];
        int lineIdx = 0;
        _lineStartChar[0] = 0;
        for (int i = 0; i < _charCount; i++)
        {
            while (lineIdx < _charLine[i])
                _lineStartChar[++lineIdx] = i;
        }

        while (lineIdx < lineCount) _lineStartChar[++lineIdx] = _charCount;

        // Layout changed under the selection — clamp so stale indices can't slice out of range.
        if (_anchor > len) _anchor = len;
        if (_extent > len) _extent = len;
    }

    private LineMetrics LineOf(int line) => Lines[line];

    /// <summary>Caret index nearest to a point (event path — loops over line runs, no allocation).</summary>
    private int IndexAtPoint(Offset point)
    {
        if (_charCount <= 1 || LineCount == 0) return 0;

        float ly = point.Y - Bounds.Y;
        int line = 0;
        while (line < LineCount - 1 && ly >= Lines[line].Top + Lines[line].Height) line++;

        float lx = point.X - Bounds.X - LineAlignOffset(line);

        // Nearest caret on this line: scan the line's char range via the geometry cache. Runs on
        // an RTL line are not x-monotonic in char order, so nearest-distance beats interval
        // bisection here.
        int best = -1;
        float bestDist = float.MaxValue;
        int end = Math.Min(val1: _lineStartChar[line + 1], val2: _charCount);
        for (int i = _lineStartChar[line]; i < end; i++)
        {
            float d = MathF.Abs(_charX[i] - lx);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        if (best < 0) return line >= LineCount - 1 ? _charCount - 1 : 0;

        // The after-last caret of the line: one past its last char when the next char starts a new
        // line (or text ends). _charX for that index carries the end-of-line x via gap fill.
        return best;
    }
}
