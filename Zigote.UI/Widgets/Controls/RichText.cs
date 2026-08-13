using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     One styled run of text inside a <see cref="RichText" />. Every style field is optional and
///     falls back to the owning widget's base style (which falls back to the theme). Spans are plain
///     data — after mutating one in place, call <see cref="RichText.InvalidateSpans" /> on the owner.
/// </summary>
public sealed class TextSpan
{
    public TextSpan() { }

    public TextSpan(string text) => Text = text;

    public TextSpan(string text, Color color)
    {
        Text = text;
        Color = color;
    }

    public string Text { get; set; } = "";
    public Color? Color { get; set; }
    public float? FontSize { get; set; }
    public FontWeight? Weight { get; set; }
    public FontStyle? Style { get; set; }
    public string? FontFamily { get; set; }

    /// <summary>Underline drawn as a thin rect just below the baseline (link styling).</summary>
    public bool Underline { get; set; }

    /// <summary>Strike-through drawn as a thin rect through the x-height.</summary>
    public bool Strikethrough { get; set; }

    /// <summary>Optional highlight painted behind the span's glyphs (inline code, search hits).</summary>
    public Color? Background { get; set; }
}

/// <summary>
///     A paragraph of styled inline spans with greedy word wrapping — the multi-style counterpart of
///     <see cref="Label" />. Spans share one flow: a bold span, a colored span and an underlined link
///     wrap together as one paragraph, breaking at spaces and hard newlines.
///     <para>
///         Layout is resolved once per (spans, width, style, direction) change and cached as placed
///         runs; the steady-state paint replays the cached runs with zero allocation. Mutating a
///         <see cref="TextSpan" /> in place requires <see cref="InvalidateSpans" /> (replacing the
///         <see cref="Spans" /> list invalidates automatically).
///     </para>
///     <para>
///         Under an RTL <see cref="Directionality" /> (or explicit <see cref="LayoutDirection" />)
///         lines fill right-to-left and default alignment becomes the right edge. This mirrors run
///         *placement* only — Unicode bidi reordering of mixed-direction text within a run is the
///         shaper's job and is not re-implemented here.
///     </para>
/// </summary>
public class RichText : LeafWidget
{
    private const string EllipsisGlyph = "…";

    private readonly List<TextSpan> _spans = [];
    internal float BuiltBaseFs;
    internal float BuiltLhFactor = 1.3f;
    internal string FullText = "";
    internal int LineCount;
    internal LineMetrics[] Lines = [];
    internal bool Rtl;
    internal int RunCount;

    // ── Cached layout (rebuilt only when an input changes; see EnsureLayout) ──
    internal PlacedRun[] Runs = [];
    internal int[] SpanOffsets = [];
    internal ThemeData ThemeRef = ThemeData.Dark;
    private string? _builtFamily;
    private int _builtMaxLines;
    private TextOverflow _builtOverflow;
    private bool _builtRtl;
    private FontStyle _builtStyle;
    private int _builtVersion = -1;
    private FontWeight _builtWeight;
    private float _builtWidth = -1f;
    private Size _size;
    private float _totalHeight;
    private bool _truncated;

    private int _version;
    private float _widest;

    public RichText() { }

    public RichText(IEnumerable<TextSpan> spans) => _spans.AddRange(spans);

    public RichText(params TextSpan[] spans) => _spans.AddRange(spans);

    /// <summary>The styled runs, in flow order. Reassigning invalidates the cached layout.</summary>
    public List<TextSpan> Spans
    {
        get => _spans;
        set
        {
            _spans.Clear();
            if (value is not null) _spans.AddRange(value);
            InvalidateSpans();
        }
    }

    // Base style — per-span fields override these; these override the theme.
    public float? FontSize { get; set; }
    public Color? Color { get; set; }
    public FontWeight FontWeight { get; set; } = FontWeight.Normal;
    public FontStyle FontStyle { get; set; } = FontStyle.Normal;
    public string? FontFamily { get; set; }
    public float? LineHeight { get; set; }

    /// <summary>
    ///     Line alignment. <c>null</c> (the default) is "start": left in LTR, right under an RTL
    ///     <see cref="Directionality" />.
    /// </summary>
    public TextAlign? Align { get; set; }

    /// <summary>
    ///     Explicit direction override; <c>null</c> follows the ambient <see cref="Directionality" />
    ///     .
    /// </summary>
    public TextDirection? LayoutDirection { get; set; }

    /// <summary>Optional cap on rendered lines. <c>null</c> leaves wrapping unbounded.</summary>
    public int? MaxLines { get; set; }

    /// <summary>How overflowing text is resolved when it exceeds <see cref="MaxLines" />.</summary>
    public TextOverflow Overflow { get; set; } = TextOverflow.Clip;

    /// <summary>
    ///     Marks the cached run layout stale after in-place span mutation (span objects are plain data
    ///     and cannot observe their own setters).
    /// </summary>
    public void InvalidateSpans()
    {
        _version++;
        MarkNeedsLayout();
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        if (string.IsNullOrEmpty(FullText)) return;
        config.Role = SemanticsRole.Text;
        config.Label = FullText;
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: _version,
            value2: RunCount,
            value3: Color?.R,
            value4: Color?.G,
            value5: Color?.B,
            value6: _truncated
        );
    }

    public override Size Measure(Constraints c)
    {
        ThemeRef = ThemeProvider.Of(BuildContext.Current);
        Rtl = (LayoutDirection ?? Directionality.Of(BuildContext.Current)) == TextDirection.Rtl;

        float baseFs = FontSize ?? ThemeRef.FontSizeBody;
        float lhFactor = LineHeight ?? ThemeRef.LineHeight;
        float maxW = float.IsFinite(c.MaxWidth) && c.MaxWidth > 0f ? c.MaxWidth : float.MaxValue;

        EnsureLayout(maxWidth: maxW, baseFs: baseFs, lhFactor: lhFactor);

        _size = c.Constrain(new Size(width: _widest, height: _totalHeight));
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
        if (RunCount == 0) return;

        bool needsClip = _truncated || MaxLines is > 0 || _widest > Bounds.Width + 0.5f;
        if (needsClip) paint.AddClipStart(Bounds);

        for (int i = 0; i < RunCount; i++)
        {
            ref readonly var run = ref Runs[i];
            var line = Lines[run.Line];
            float x = Bounds.X + LineAlignOffset(run.Line) + run.X;
            float top = Bounds.Y + line.Top;
            float baseline = Bounds.Y + line.Baseline;

            var span = _spans[run.Span];
            if (span.Background is { } bg)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: x,
                        y: top,
                        width: run.Width,
                        height: line.Height
                    ),
                    color: bg
                );
            }

            float fs = SpanFs(span);
            var color = span.Color ?? Color ?? ThemeRef.Label1;
            paint.AddText(
                text: run.Slice,
                baselineX: x,
                baselineY: baseline,
                color: color,
                fontSize: fs,
                lineHeight: BuiltLhFactor,
                fontWeight: span.Weight ?? FontWeight,
                fontStyle: span.Style ?? FontStyle,
                fontFamily: span.FontFamily ?? FontFamily
            );

            // Text decorations are plain rects — no dedicated paint primitive needed.
            float deco = MathF.Max(x: 1f, y: fs / 14f);
            if (span.Underline)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: x,
                        y: baseline + deco,
                        width: run.Width,
                        height: deco
                    ),
                    color: color
                );
            }

            if (span.Strikethrough)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: x,
                        y: baseline - (fs * 0.3f),
                        width: run.Width,
                        height: deco
                    ),
                    color: color
                );
            }
        }

        if (needsClip) paint.AddClipEnd();
    }

    // ── Layout construction (cold path — allocation here is fine, and only here) ──

    /// <summary>Alignment offset of a line inside the widget box (uses live Bounds, so paint-time).</summary>
    internal float LineAlignOffset(int line)
    {
        var align = Align ?? (Rtl ? TextAlign.Right : TextAlign.Left);
        float lineW = Lines[line].Width;
        return align switch {
            TextAlign.Center => MathF.Max(x: 0f, y: (Bounds.Width - lineW) / 2f),
            TextAlign.Right => MathF.Max(x: 0f, y: Bounds.Width - lineW),
            _ => 0f,
        };
    }

    internal float SpanFs(TextSpan s) => s.FontSize ?? FontSize ?? ThemeRef.FontSizeBody;

    internal TextSpan SpanAt(int index) => _spans[index];

    internal float SpanWidth(TextSpan s, string text)
    {
        return TextMeasure.Width(
            text: text,
            fontSize: SpanFs(s),
            weight: s.Weight ?? FontWeight,
            style: s.Style ?? FontStyle,
            fontFamily: s.FontFamily ?? FontFamily
        );
    }

    private void EnsureLayout(float maxWidth, float baseFs, float lhFactor)
    {
        if (_builtVersion == _version && _builtWidth == maxWidth && BuiltBaseFs == baseFs &&
            _builtRtl == Rtl && BuiltLhFactor == lhFactor && _builtMaxLines == (MaxLines ?? 0) &&
            _builtOverflow == Overflow && _builtWeight == FontWeight && _builtStyle == FontStyle &&
            _builtFamily == FontFamily)
            return;

        _builtVersion = _version;
        _builtWidth = maxWidth;
        BuiltBaseFs = baseFs;
        _builtRtl = Rtl;
        BuiltLhFactor = lhFactor;
        _builtMaxLines = MaxLines ?? 0;
        _builtOverflow = Overflow;
        _builtWeight = FontWeight;
        _builtStyle = FontStyle;
        _builtFamily = FontFamily;

        BuildLayout(maxWidth: maxWidth, baseFs: baseFs, lhFactor: lhFactor);
        OnLayoutRebuilt();
    }

    private void BuildLayout(float maxWidth, float baseFs, float lhFactor)
    {
        RunCount = 0;
        LineCount = 0;
        _widest = 0f;
        _truncated = false;

        // Full concatenated text + per-span global char offsets (semantics, selection, word-select).
        if (SpanOffsets.Length < _spans.Count + 1) SpanOffsets = new int[_spans.Count + 1];
        int total = 0;
        for (int s = 0; s < _spans.Count; s++)
        {
            SpanOffsets[s] = total;
            total += _spans[s].Text.Length;
        }

        SpanOffsets[_spans.Count] = total;
        FullText = _spans.Count switch {
            0 => "",
            1 => _spans[0].Text,
            _ => string.Concat(_spans.Select(sp => sp.Text)),
        };

        // ── Wrap state ──
        float cursor = 0f; // x within the current line
        float lineMaxFs = 0f;
        bool lineOpen = false;

        // Current (open) run — a maximal same-span stretch on one line.
        int curSpan = -1;
        int runStart = 0;
        int runEnd = 0;
        float runX = 0f;
        float runW = 0f;

        // Whitespace measured but not yet committed: dropped at a wrap, placed before the next word.
        int pendSpan = -1;
        int pendStart = 0;
        int pendEnd = 0;
        float pendW = 0f;

        bool capped = false;

        void FlushRun()
        {
            if (curSpan < 0 || runEnd <= runStart)
            {
                curSpan = -1;
                return;
            }

            if (Runs.Length == RunCount)
                Array.Resize(array: ref Runs, newSize: Math.Max(val1: 8, val2: Runs.Length * 2));
            string spanText = _spans[curSpan].Text;
            Runs[RunCount++] = new PlacedRun(
                span: curSpan,
                charStart: SpanOffsets[curSpan] + runStart,
                slice: spanText.Substring(startIndex: runStart, length: runEnd - runStart),
                x: runX,
                width: runW,
                line: LineCount
            );
            curSpan = -1;
        }

        void Append(int span, int start, int end, float w, float fs)
        {
            if (curSpan == span && runEnd == start)
            {
                runEnd = end;
                runW += w;
            }
            else
            {
                FlushRun();
                curSpan = span;
                runStart = start;
                runEnd = end;
                runX = cursor;
                runW = w;
            }

            cursor += w;
            lineMaxFs = MathF.Max(x: lineMaxFs, y: fs);
            lineOpen = true;
        }

        void ResolvePending()
        {
            if (pendSpan < 0) return;
            Append(
                span: pendSpan,
                start: pendStart,
                end: pendEnd,
                w: pendW,
                fs: SpanFs(_spans[pendSpan])
            );
            pendSpan = -1;
        }

        // Ends the current line; returns false when the MaxLines cap is reached.
        bool EndLine()
        {
            FlushRun();
            if (Lines.Length == LineCount)
                Array.Resize(array: ref Lines, newSize: Math.Max(val1: 4, val2: Lines.Length * 2));
            float fs = lineMaxFs > 0f ? lineMaxFs : baseFs;
            Lines[LineCount] = new LineMetrics(
                width: cursor,
                maxFontSize: fs,
                top: 0f,
                height: 0f,
                baseline: 0f
            );
            _widest = MathF.Max(x: _widest, y: cursor);
            LineCount++;
            cursor = 0f;
            lineMaxFs = 0f;
            lineOpen = false;
            return _builtMaxLines <= 0 || LineCount < _builtMaxLines;
        }

        for (int s = 0; s < _spans.Count && !capped; s++)
        {
            var span = _spans[s];
            string t = span.Text;
            if (t.Length == 0) continue;
            float fs = SpanFs(span);

            int i = 0;
            while (i < t.Length)
            {
                char ch = t[i];
                if (ch == '\n')
                {
                    pendSpan = -1; // line-trailing spaces vanish at a break
                    if (!EndLine())
                    {
                        capped = true;
                        break;
                    }

                    lineOpen = true; // the line after a hard break exists even while empty
                    i++;
                    continue;
                }

                if (ch == ' ')
                {
                    int st = i;
                    while (i < t.Length && t[i] == ' ') i++;
                    float w = SpanWidth(s: span, text: t[st..i]);
                    if (pendSpan == s && pendEnd == st)
                    {
                        pendEnd = i;
                        pendW += w;
                    }
                    else
                    {
                        ResolvePending();
                        pendSpan = s;
                        pendStart = st;
                        pendEnd = i;
                        pendW = w;
                    }

                    lineOpen = true;
                    continue;
                }

                {
                    int st = i;
                    while (i < t.Length && t[i] != ' ' && t[i] != '\n') i++;
                    float w = SpanWidth(s: span, text: t[st..i]);

                    if (cursor + pendW + w <= maxWidth || cursor <= 0f)
                    {
                        ResolvePending();
                        Append(
                            span: s,
                            start: st,
                            end: i,
                            w: w,
                            fs: fs
                        );
                    }
                    else
                    {
                        pendSpan = -1;
                        if (!EndLine())
                        {
                            capped = true;
                            break;
                        }

                        Append(
                            span: s,
                            start: st,
                            end: i,
                            w: w,
                            fs: fs
                        );
                    }
                }
            }
        }

        if (capped)
        {
            _truncated = true;
            if (Overflow == TextOverflow.Ellipsis && RunCount > 0)
                EllipsizeLastRun(maxWidth);
        }
        else if (lineOpen || (LineCount == 0 && FullText.Length > 0)) EndLine();

        // ── Finalize line geometry (tops/baselines) + RTL mirroring ──
        float top = 0f;
        for (int l = 0; l < LineCount; l++)
        {
            var line = Lines[l];
            float height = line.MaxFontSize * lhFactor;
            Lines[l] = new LineMetrics(
                width: line.Width,
                maxFontSize: line.MaxFontSize,
                top: top,
                height: height,
                baseline: top + (line.MaxFontSize * 0.8f)
            );
            top += height;
        }

        _totalHeight = top;

        if (Rtl)
        {
            for (int r = 0; r < RunCount; r++)
            {
                ref var run = ref Runs[r];
                run = run.WithX(Lines[run.Line].Width - run.X - run.Width);
            }
        }
    }

    /// <summary>
    ///     Shrinks the final placed run so its slice plus an ellipsis fits the remaining width of the
    ///     capped last line (the <see cref="Label" /> Fit pattern, applied to one run).
    /// </summary>
    private void EllipsizeLastRun(float maxWidth)
    {
        ref var run = ref Runs[RunCount - 1];
        var span = _spans[run.Span];
        float budget = MathF.Max(x: 0f, y: maxWidth - run.X);
        float ellipsisW = SpanWidth(s: span, text: EllipsisGlyph);

        string text = run.Slice;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (SpanWidth(s: span, text: text[..mid]) + ellipsisW <= budget) lo = mid;
            else hi = mid - 1;
        }

        string slice = text[..lo].TrimEnd() + EllipsisGlyph;
        float w = SpanWidth(s: span, text: slice);
        run = new PlacedRun(
            span: run.Span,
            charStart: run.CharStart,
            slice: slice,
            x: run.X,
            width: w,
            line: run.Line
        );

        var line = Lines[run.Line];
        Lines[run.Line] = new LineMetrics(
            width: run.X + w,
            maxFontSize: line.MaxFontSize,
            top: line.Top,
            height: line.Height,
            baseline: line.Baseline
        );
    }

    /// <summary>
    ///     Called after the cached run layout is rebuilt (selection subclasses refresh advances
    ///     here).
    /// </summary>
    internal virtual void OnLayoutRebuilt() { }

    /// <summary>One placed same-span stretch on one line, with its pre-sliced paint string.</summary>
    internal readonly struct PlacedRun(
        int span,
        int charStart,
        string slice,
        float x,
        float width,
        int line)
    {
        public readonly int Span = span;

        /// <summary>Offset of the run's first char in the concatenated full text.</summary>
        public readonly int CharStart = charStart;

        public readonly string Slice = slice;

        /// <summary>X relative to the line's start edge (already mirrored under RTL).</summary>
        public readonly float X = x;

        public readonly float Width = width;
        public readonly int Line = line;

        public PlacedRun WithX(float x2)
        {
            return new PlacedRun(
                span: Span,
                charStart: CharStart,
                slice: Slice,
                x: x2,
                width: Width,
                line: Line
            );
        }
    }

    internal readonly struct LineMetrics(
        float width,
        float maxFontSize,
        float top,
        float height,
        float baseline)
    {
        public readonly float Width = width;
        public readonly float MaxFontSize = maxFontSize;
        public readonly float Top = top;
        public readonly float Height = height;
        public readonly float Baseline = baseline;
    }
}
