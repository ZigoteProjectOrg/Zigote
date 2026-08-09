using System.Text;
using Zigote.Core.Animation;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     A multiline, monospace code editor. Syntax highlighting is delegated to an injected
///     <see cref="Tokenizer" /> (no LSP, no colour schemes) — the language parsers themselves live
///     outside <c>Zigote.UI</c>, in the FParsec-based <c>Zigote.Modules.UI.CodeEditor</c> module.
///     Renders in the bundled Iosevka face (<see cref="Typography.Code" />, family
///     <c>"code"</c>) with a right-aligned line-number gutter, vertical + horizontal scrolling, caret
///     /
///     selection, clipboard, and the standard editing keys.
///     <para>
///         Implements <see cref="ITextInputClient" /> so the app's focus gate routes printable input
///         to
///         <see cref="OnTextInput" /> even though it isn't a <c>TextField</c>.
///     </para>
/// </summary>
public sealed class CodeEditor : Widget, ITextInputClient
{
    private const int TabWidth = 4;

    // Undo: cap the snapshot history and coalesce same-kind edits made within this window into one step
    // (so undo walks back by word-ish runs, not per keystroke).
    private const int UndoDepth = 256;
    private const float UndoCoalesceSeconds = 0.6f;
    private const float Pad = Spacing.Sm;
    private const float GutterPadRight = Spacing.Sm;
    private const float GutterPadLeft = Spacing.Sm;
    private const float ScrollEase = 22f; // smooth-scroll ease rate (higher = snappier)
    private const float BarHitWidth = 16f; // grabbable scrollbar strip width

    /// <summary>How far a fling throws the scroll target, as a fraction of lift-off velocity.</summary>
    private const float FlingSeconds = 0.35f;

    // Double-click (word select) detection — no click-count in the event pipeline, so track timing.
    private const float DoubleClickSeconds = 0.4f;

    // Content-addressed native text layouts for painted segments (token slices, plain lines, gutter
    // numbers). The docked editor is read-only with stable content, so the same strings recur every
    // frame and across scroll → near-perfect reuse with no per-frame HarfBuzz shaping. Holding these
    // handles is also what exercises the engine's atlas-reset re-shape path: a handle stays valid
    // across a reset and the layout re-bakes itself lazily (see FreeTypeTextRenderer.appendLayoutGlyphs).
    private const int
        LayoutCacheCap = 16384; // enough for long source files without cyclic re-shaping

    private readonly Dictionary<string, TextLayout> _layouts = new();

    // Line-number strings cached by line index (index l always renders "l+1") so the gutter doesn't
    // allocate a string per visible line per frame.
    private readonly List<string> _lineNoCache = [];

    private readonly List<string> _lines = [""];

    // Entering tokenizer state per line (index = line). Cached across frames, rebuilt lazily by
    // EnsureLineStates, and cleared on any content edit — so a focused editor no longer re-lexes from
    // line 0 every frame while scrolled down.
    private readonly List<int> _lineStates = [];
    private readonly List<DocSnapshot> _redo = [];
    private readonly List<Token> _tokenBuffer = []; // reused per painted line — no per-frame alloc
    private readonly Dictionary<int, CachedLineTokens> _tokenCache = [];

    // Undo/redo: snapshot stacks of the whole document + caret/selection. Cleared when a new document
    // is loaded (SetTextInternal). See RecordEdit / Undo / Redo.
    private readonly List<DocSnapshot> _undo = [];
    private readonly List<VisualRow> _visualRows = [];
    private int _anchorCol;
    private int _anchorLine = -1;
    private int _barDrag; // 0 = none, 1 = vertical scrollbar, 2 = horizontal scrollbar
    private float _barGrab; // pointer offset within the grabbed thumb
    private int _caretCol;

    // Caret + selection as (line, col). Anchor < 0 means no selection.
    private int _caretLine;
    private float _charWidth; // monospace advance of one glyph in the code face
    private int _compositionSelectionLength;
    private int _compositionSelectionStart;
    private string _compositionText = string.Empty;
    private float _contentWidth; // widest line in px (horizontal-scroll extent)

    // Resolved at Measure, reused by Paint so the two passes agree.
    private float _fontSize = Typography.Code.Size;
    private float? _fontSizeOverride;
    private float _gutterWidth;

    private bool _isDragging;
    private int _lastClickCol;
    private int _lastClickLine;
    private float _lastClickTime = -1f;
    private EditKind _lastEditKind = EditKind.Other;
    private float _lastEditTime = -1f;

    private bool
        _layoutsUnavailable; // no engine (headless tests) / backend without layout support → AddText

    private float _lineHeight; // pixels per line
    private Ticker? _scrollTicker;

    private float _scrollX;
    private float _scrollY;
    private Size _size;
    private bool _softWrap;
    private float _targetX; // smooth-scroll target — the rendered offset eases toward this
    private float _targetY;
    private ThemeData _theme = ThemeData.Dark;
    private ILineTokenizer? _tokenizer;

    public CodeEditor(string text = "")
    {
        SetTextInternal(text);
    }

    public string Text
    {
        get => string.Join('\n', _lines);
        set => SetTextInternal(value);
    }

    /// <summary>
    ///     The syntax highlighter. <c>null</c> renders plain, unhighlighted text. Concrete tokenizers
    ///     (C#, JSON, WGSL, Zig) are produced by <c>Zigote.Modules.UI.CodeEditor</c>'s FParsec parsers.
    /// </summary>
    public ILineTokenizer? Tokenizer
    {
        get => _tokenizer;
        set
        {
            if (ReferenceEquals(_tokenizer, value)) return;
            _tokenizer = value;
            _lineStates.Clear();
            _tokenCache.Clear();
            MarkNeedsPaint();
        }
    }

    public Action<string>? OnChanged { get; set; }

    /// <summary>
    ///     Invoked on ⌘/Ctrl+S. The host decides what "save" means (e.g. write the open file to disk);
    ///     the editor itself owns no file I/O. Fires only when the editor has keyboard focus.
    /// </summary>
    public Action? OnSubmit { get; set; }

    public bool ReadOnly { get; set; }

    public override MouseCursor? GetCursor(Offset point)
    {
        return MouseCursor.Text;
    }

    /// <summary>Wrap long physical lines into viewport-width visual rows without changing the document.</summary>
    public bool SoftWrap
    {
        get => _softWrap;
        set => SetLayout(ref _softWrap, value);
    }

    public override bool Focusable => true;

    /// <summary>The editor owns the arrow keys for caret movement, so they're never repurposed for focus.</summary>
    public override bool HandlesDirectionalKeys => true;

    /// <summary>Optional accessible name for the editor (e.g. the file or field it edits).</summary>
    public string? SemanticsLabel { get; set; }

    // ── Selection helpers ─────────────────────────────────────────────────────

    private bool HasSelection =>
        _anchorLine >= 0 && (_anchorLine != _caretLine || _anchorCol != _caretCol);

    /// <summary>Content origin (top-left of the text area, after the gutter), excluding scroll.</summary>
    private float TextLeft => Bounds.X + _gutterWidth + Pad;

    private float TextTop => Bounds.Y + Pad;

    // Only an editable, focused editor blinks a caret; a read-only docked viewer doesn't, so the frame
    // loop can stop repainting it every frame (see App's focus repaint gate / ITextInputClient).
    public bool WantsCaretBlink => Focused && !ReadOnly;

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.TextField;
        config.Label = SemanticsLabel;
        config.Value = Text;
        config.Actions = SemanticsAction.Focus |
                         (ReadOnly ? SemanticsAction.None : SemanticsAction.SetValue);
        config.AddFlag(SemanticsFlags.Focusable)
            .AddFlag(SemanticsFlags.Focused, Focused)
            .AddFlag(SemanticsFlags.Multiline)
            .AddFlag(SemanticsFlags.ReadOnly, ReadOnly);
    }

    private void SetTextInternal(string text)
    {
        _lines.Clear();
        var split = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var l in split) _lines.Add(l);
        if (_lines.Count == 0) _lines.Add("");
        _lineStates.Clear(); // whole document changed — drop the entering-state cache
        _tokenCache.Clear();
        ClearLayouts(); // and the cached glyph layouts — the old lines' strings may never recur
        _undo.Clear(); // a freshly loaded document has no edit history to step back into
        _redo.Clear();
        _lastEditKind = EditKind.Other;
        ClampCaret();
        ClearSelection();
        _scrollX = _scrollY = _targetX = _targetY = 0f; // new document starts at the top
        _scrollTicker?.Stop();
        MarkNeedsPaint();
    }

    private void ClearSelection()
    {
        _anchorLine = -1;
    }

    /// <summary>Ordered (start, end) selection bounds as (line, col), start &lt;= end.</summary>
    private ((int Line, int Col) Start, (int Line, int Col) End) OrderedSelection()
    {
        var a = (_anchorLine, _anchorCol);
        var b = (_caretLine, _caretCol);
        return Before(a, b) ? (a, b) : (b, a);
    }

    private static bool Before((int Line, int Col) a, (int Line, int Col) b)
    {
        return a.Line < b.Line || (a.Line == b.Line && a.Col <= b.Col);
    }

    private void ClampCaret()
    {
        _caretLine = Math.Clamp(_caretLine, 0, _lines.Count - 1);
        _caretCol = TextNavigation.GraphemeBoundaryAtOrBefore(
            _lines[_caretLine],
            Math.Clamp(_caretCol, 0, _lines[_caretLine].Length)
        );
    }

    private void StartOrExtendSelection(bool extend)
    {
        if (extend)
        {
            if (_anchorLine < 0)
            {
                _anchorLine = _caretLine;
                _anchorCol = _caretCol;
            }
        }
        else
        {
            ClearSelection();
        }
    }

    // ── Edit primitives ───────────────────────────────────────────────────────

    private void DeleteSelection()
    {
        if (!HasSelection) return;
        var (s, e) = OrderedSelection();
        if (s.Line == e.Line)
        {
            _lines[s.Line] = _lines[s.Line][..s.Col] + _lines[s.Line][e.Col..];
        }
        else
        {
            var head = _lines[s.Line][..s.Col];
            var tail = _lines[e.Line][e.Col..];
            _lines[s.Line] = head + tail;
            _lines.RemoveRange(s.Line + 1, e.Line - s.Line);
        }

        _caretLine = s.Line;
        _caretCol = s.Col;
        ClearSelection();
    }

    private void InsertText(string text, EditKind kind = EditKind.Other)
    {
        if (ReadOnly || string.IsNullOrEmpty(text)) return;
        RecordEdit(kind);
        if (HasSelection) DeleteSelection();

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var cur = _lines[_caretLine];
        var before = cur[.._caretCol];
        var after = cur[_caretCol..];

        if (!text.Contains('\n'))
        {
            _lines[_caretLine] = before + text + after;
            _caretCol += text.Length;
        }
        else
        {
            var parts = text.Split('\n');
            _lines[_caretLine] = before + parts[0];
            var insertAt = _caretLine + 1;
            for (var k = 1; k < parts.Length - 1; k++)
                _lines.Insert(insertAt++, parts[k]);
            var last = parts[^1];
            _lines.Insert(insertAt, last + after);
            _caretLine = insertAt;
            _caretCol = last.Length;
        }

        ClearSelection();
        Commit();
    }

    private void Commit()
    {
        _lineStates.Clear(); // content changed → the entering-state cache is stale, rebuild lazily
        _tokenCache.Clear();
        if (Bounds.Width > 0f)
        {
            RebuildVisualRows(MathF.Max(_charWidth, Bounds.Width - _gutterWidth - Pad * 2f));
            _contentWidth = 0f;
            foreach (var row in _visualRows) _contentWidth = MathF.Max(_contentWidth, row.Width);
        }

        OnChanged?.Invoke(Text);
        EnsureCaretVisible();
        MarkNeedsLayout();
    }

    // ── Undo / redo ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Snapshot the pre-edit document so it can be undone. Consecutive edits of the same kind
    ///     (continuous typing, continuous deleting) within <see cref="UndoCoalesceSeconds" /> share one
    ///     undo entry; anything else (paste, newline, cut, a kind change) starts a new one. Must be called
    ///     <em>before</em> mutating <see cref="_lines" />.
    /// </summary>
    private void RecordEdit(EditKind kind)
    {
        var now = App.Active?.Time ?? 0f;
        var coalesce = kind != EditKind.Other && kind == _lastEditKind
                                              && _undo.Count > 0 && now - _lastEditTime <
                                              UndoCoalesceSeconds;
        if (!coalesce)
        {
            _undo.Add(CaptureSnapshot());
            if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
            _redo.Clear(); // a fresh edit invalidates the redo branch
        }

        _lastEditKind = kind;
        _lastEditTime = now;
    }

    private DocSnapshot CaptureSnapshot()
    {
        return new DocSnapshot(
            _lines.ToArray(),
            _caretLine,
            _caretCol,
            _anchorLine,
            _anchorCol
        );
    }

    public void Undo()
    {
        if (ReadOnly || _undo.Count == 0) return;
        _redo.Add(CaptureSnapshot());
        RestoreSnapshot(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        _lastEditKind = EditKind.Other; // the next edit begins a fresh coalescing run
    }

    public void Redo()
    {
        if (ReadOnly || _redo.Count == 0) return;
        _undo.Add(CaptureSnapshot());
        RestoreSnapshot(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        _lastEditKind = EditKind.Other;
    }

    private void RestoreSnapshot(DocSnapshot s)
    {
        _lines.Clear();
        _lines.AddRange(s.Lines);
        if (_lines.Count == 0) _lines.Add("");
        _caretLine = s.CaretLine;
        _caretCol = s.CaretCol;
        _anchorLine = s.AnchorLine;
        _anchorCol = s.AnchorCol;
        ClampCaret();
        Commit(); // rebuild caches/rows, fire OnChanged, keep the caret in view
    }

    // ── Clipboard ─────────────────────────────────────────────────────────────

    private string SelectedText()
    {
        if (!HasSelection) return string.Empty;
        var (s, e) = OrderedSelection();
        if (s.Line == e.Line) return _lines[s.Line][s.Col..e.Col];
        var sb = new StringBuilder();
        sb.Append(_lines[s.Line][s.Col..]).Append('\n');
        for (var l = s.Line + 1; l < e.Line; l++) sb.Append(_lines[l]).Append('\n');
        sb.Append(_lines[e.Line][..e.Col]);
        return sb.ToString();
    }

    private void CopyAction()
    {
        if (HasSelection) ZigoteEngine.Instance?.SetClipboard(SelectedText());
    }

    private void CutAction()
    {
        if (ReadOnly)
        {
            CopyAction();
            return;
        }

        CopyAction();
        if (HasSelection)
        {
            RecordEdit(EditKind.Other);
            DeleteSelection();
            Commit();
        }
    }

    private void PasteAction()
    {
        var pasted = ZigoteEngine.Instance?.GetClipboard() ?? string.Empty;
        if (pasted.Length > 0) InsertText(pasted);
    }

    private void SelectAll()
    {
        _anchorLine = 0;
        _anchorCol = 0;
        _caretLine = _lines.Count - 1;
        _caretCol = _lines[_caretLine].Length;
        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    private int RowForCaret(int line, int col)
    {
        if (_visualRows.Count == 0)
            for (var i = 0; i < _lines.Count; i++)
                _visualRows.Add(
                    new VisualRow(
                        i,
                        0,
                        _lines[i].Length,
                        0f,
                        IsFastGridText(_lines[i])
                    )
                );
        for (var i = 0; i < _visualRows.Count; i++)
        {
            var row = _visualRows[i];
            if (row.Line == line && col >= row.Start && col <= row.End) return i;
        }

        return Math.Clamp(line, 0, Math.Max(0, _visualRows.Count - 1));
    }

    private float ColToX(int line, int col)
    {
        if (_visualRows.Count == 0) return 0f;
        return ColToX(_visualRows[RowForCaret(line, col)], col);
    }

    private float ColToX(VisualRow row, int col)
    {
        var line = _lines[row.Line];
        col = TextNavigation.GraphemeBoundaryAtOrBefore(line, Math.Clamp(col, row.Start, row.End));
        if (row.FastGrid) return (col - row.Start) * _charWidth;
        var segment = line[row.Start..row.End];
        var layout = LayoutFor(segment);
        if (layout is not null &&
            layout.TryGetCaretPosition(col - row.Start, out var position, out _))
            return position.X;
        return TextMeasure.Width(line[row.Start..col], _fontSize, fontFamily: "code");
    }

    private int XToCol(VisualRow row, float localX)
    {
        var line = _lines[row.Line];
        if (localX <= 0f || row.Start == row.End) return row.Start;
        if (row.FastGrid)
            return Math.Clamp(
                row.Start + (int)MathF.Round(localX / _charWidth),
                row.Start,
                row.End
            );
        var segment = line[row.Start..row.End];
        var layout = LayoutFor(segment);
        if (layout is not null) return row.Start + layout.HitTest(localX);

        var boundaries = TextNavigation.GraphemeBoundaries(segment);
        var previousWidth = 0f;
        for (var i = 1; i < boundaries.Length; i++)
        {
            var width = TextMeasure.Width(segment[..boundaries[i]], _fontSize, fontFamily: "code");
            if (localX < (previousWidth + width) / 2f) return row.Start + boundaries[i - 1];
            previousWidth = width;
        }

        return row.End;
    }

    private void RebuildVisualRows(float availableWidth)
    {
        _visualRows.Clear();
        var wrapWidth = MathF.Max(_charWidth, availableWidth);
        for (var lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
        {
            var line = _lines[lineIndex];
            var fastGrid = IsFastGridText(line);
            if (!SoftWrap || line.Length == 0)
            {
                // Unwrapped mode only needs a conservative scrollbar extent here. Precise caret and
                // hit geometry comes from the HarfBuzz layout on demand for visible rows.
                var width = line.Length * _charWidth;
                _visualRows.Add(
                    new VisualRow(
                        lineIndex,
                        0,
                        line.Length,
                        width,
                        fastGrid
                    )
                );
                continue;
            }

            var boundaries = TextNavigation.GraphemeBoundaries(line);
            var prefixWidths = new float[boundaries.Length];
            for (var i = 1; i < boundaries.Length; i++)
                prefixWidths[i] = prefixWidths[i - 1] +
                                  GraphemeAdvance(line, boundaries[i - 1], boundaries[i]);

            var startBoundary = 0;
            while (startBoundary < boundaries.Length - 1)
            {
                var endBoundary = startBoundary + 1;
                var accepted = endBoundary;
                var lastBreak = -1;
                while (endBoundary < boundaries.Length)
                {
                    var candidateWidth = prefixWidths[endBoundary] - prefixWidths[startBoundary];
                    if (candidateWidth > wrapWidth && endBoundary > startBoundary + 1) break;
                    accepted = endBoundary;
                    if (char.IsWhiteSpace(line[boundaries[endBoundary - 1]]))
                        lastBreak = endBoundary;
                    endBoundary++;
                }

                if (endBoundary < boundaries.Length && lastBreak > startBoundary)
                    accepted = lastBreak;

                var rowStart = boundaries[startBoundary];
                var rowEnd = boundaries[accepted];
                var width = prefixWidths[accepted] - prefixWidths[startBoundary];
                _visualRows.Add(
                    new VisualRow(
                        lineIndex,
                        rowStart,
                        rowEnd,
                        width,
                        fastGrid
                    )
                );
                startBoundary = accepted;
            }
        }

        if (_visualRows.Count == 0)
            _visualRows.Add(
                new VisualRow(
                    0,
                    0,
                    0,
                    0f,
                    true
                )
            );
    }

    private static bool IsFastGridText(string text)
    {
        foreach (var c in text)
            if (c is < ' ' or > '~')
                return false;
        return true;
    }

    private float GraphemeAdvance(string text, int start, int end)
    {
        // Iosevka's ASCII repertoire is a true fixed grid. Avoid shaping and allocating the common
        // code path; non-ASCII graphemes still go through HarfBuzz so CJK, combining text and emoji
        // receive their actual advance.
        if (end == start + 1)
        {
            var c = text[start];
            if (c == '\t') return _charWidth * TabWidth;
            if (c <= 0x7f) return _charWidth;
        }

        return TextMeasure.Width(text[start..end], _fontSize, fontFamily: "code");
    }

    private void EnsureCaretVisible()
    {
        if (Bounds.Width <= 0f || _lineHeight <= 0f) return;

        var innerW = Bounds.Width - _gutterWidth - Pad * 2f;
        var innerH = Bounds.Height - Pad * 2f;

        var caretRow = RowForCaret(_caretLine, _caretCol);
        var caretX = ColToX(_caretLine, _caretCol);
        if (caretX - _targetX > innerW - 2f) _targetX = caretX - innerW + 2f;
        else if (caretX - _targetX < 0f) _targetX = caretX;

        var caretY = caretRow * _lineHeight;
        if (caretY - _targetY > innerH - _lineHeight) _targetY = caretY - innerH + _lineHeight;
        else if (caretY - _targetY < 0f) _targetY = caretY;

        ClampTargets();
        SnapScroll(); // caret-follow is instant, not animated
    }

    // ── Smooth scrolling ──────────────────────────────────────────────────────

    private void ClampTargets()
    {
        if (_lineHeight <= 0f) return;
        var innerW = Bounds.Width - _gutterWidth - Pad * 2f;
        var innerH = Bounds.Height - Pad * 2f;
        _targetX = Math.Clamp(_targetX, 0f, Math.Max(0f, _contentWidth + _charWidth * 2f - innerW));
        var total = _visualRows.Count * _lineHeight;
        _targetY = Math.Clamp(_targetY, 0f, Math.Max(0f, total - innerH));
    }

    /// <summary>
    ///     Ease the rendered offset toward the target each frame; the GPU composites it via
    ///     PushTranslate.
    /// </summary>
    private void AnimateScroll()
    {
        _scrollTicker ??= new Ticker(TickScroll);
        _scrollTicker.Start();
    }

    private void TickScroll(float dt)
    {
        var k = 1f - MathF.Exp(-dt * ScrollEase); // frame-rate independent ease
        _scrollX += (_targetX - _scrollX) * k;
        _scrollY += (_targetY - _scrollY) * k;
        if (MathF.Abs(_targetX - _scrollX) < 0.4f && MathF.Abs(_targetY - _scrollY) < 0.4f)
        {
            _scrollX = _targetX;
            _scrollY = _targetY;
            _scrollTicker?.Stop();
        }

        MarkNeedsPaint();
    }

    /// <summary>Jump the rendered offset to the target immediately (caret-follow, scrollbar drag).</summary>
    private void SnapScroll()
    {
        _scrollX = _targetX;
        _scrollY = _targetY;
        _scrollTicker?.Stop();
    }

    // Scrollbar geometry (start, length, thumb length, max scroll, present?) — shared by Paint + drag.
    private (float Start, float Len, float Thumb, float Max, bool On) VBar()
    {
        var innerH = Bounds.Height - Pad * 2f;
        var total = _visualRows.Count * _lineHeight;
        var max = MathF.Max(0f, total - innerH);
        if (max <= 0f || total <= 0f) return (0f, 0f, 0f, 0f, false);
        var len = Bounds.Height - 4f;
        var thumb = MathF.Max(24f, len * (innerH / total));
        return (Bounds.Y + 2f, len, thumb, max, true);
    }

    private (float Start, float Len, float Thumb, float Max, bool On) HBar()
    {
        if (SoftWrap) return (0f, 0f, 0f, 0f, false);
        var innerW = Bounds.Width - _gutterWidth - Pad * 2f;
        var extent = _contentWidth + _charWidth * 2f;
        var max = MathF.Max(0f, extent - innerW);
        if (max <= 0f || extent <= 0f) return (0f, 0f, 0f, 0f, false);
        var len = Bounds.Width - _gutterWidth - 4f;
        var thumb = MathF.Max(24f, len * (innerW / extent));
        return (Bounds.X + _gutterWidth + 2f, len, thumb, max, true);
    }

    private void DragVBar(float pointerY)
    {
        var bar = VBar();
        if (!bar.On) return;
        var travel = bar.Len - bar.Thumb;
        var frac = travel > 0f ? (pointerY - bar.Start - _barGrab) / travel : 0f;
        _targetY = Math.Clamp(frac, 0f, 1f) * bar.Max;
        SnapScroll();
        MarkNeedsPaint();
    }

    private void DragHBar(float pointerX)
    {
        var bar = HBar();
        if (!bar.On) return;
        var travel = bar.Len - bar.Thumb;
        var frac = travel > 0f ? (pointerX - bar.Start - _barGrab) / travel : 0f;
        _targetX = Math.Clamp(frac, 0f, 1f) * bar.Max;
        SnapScroll();
        MarkNeedsPaint();
    }

    // ── Widget overrides ──────────────────────────────────────────────────────

    public override void Detach()
    {
        base.Detach();
        _scrollTicker?.Dispose();
        _scrollTicker = null;
        ClearLayouts(); // release native layout handles held for cached segments
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            _lines.Count,
            _caretLine,
            _caretCol,
            _anchorLine,
            _anchorCol,
            Focused,
            (int)(_scrollY * 0.1f),
            _barDrag
        );
    }

    /// <summary>
    ///     Editor font size in points; null (the default) uses the <see cref="Typography.Code" />
    ///     ramp size. Setting it drops cached native text layouts and relayouts.
    /// </summary>
    public float? FontSize
    {
        get => _fontSizeOverride;
        set
        {
            if (Nullable.Equals(_fontSizeOverride, value)) return;
            _fontSizeOverride = value;
            InvalidateTextLayouts();
        }
    }

    /// <summary>
    ///     Drop the cached native text layouts and re-measure. Call after the <c>"code"</c> face is
    ///     re-registered at runtime (editor-font swap) — cached layouts embed glyphs shaped with the
    ///     old face and would keep rendering it.
    /// </summary>
    public void InvalidateTextLayouts()
    {
        ClearLayouts();
        _layoutsUnavailable = false;
        MarkNeedsLayout();
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _fontSize = _fontSizeOverride ?? Typography.Code.Size;
        _lineHeight = _fontSize * Typography.Code.LineHeight;

        // Monospace advance drives every horizontal measurement (one cached measure, not per line).
        _charWidth = TextMeasure.Width("0", _fontSize, fontFamily: "code");

        // Gutter sized to the widest line number; digits are monospace, so width = digits × advance.
        var digits = Math.Max(2, _lines.Count.ToString().Length);
        _gutterWidth = GutterPadLeft + digits * _charWidth + GutterPadRight;

        _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
        var availableWidth = MathF.Max(_charWidth, _size.Width - _gutterWidth - Pad * 2f);
        RebuildVisualRows(availableWidth);
        _contentWidth = 0f;
        foreach (var row in _visualRows) _contentWidth = MathF.Max(_contentWidth, row.Width);
        if (SoftWrap) _scrollX = _targetX = 0f;
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    private Color ColorFor(TokenKind kind)
    {
        return kind switch {
            TokenKind.Keyword => _theme.Primary,
            TokenKind.Type => _theme.Accent,
            TokenKind.String => _theme.Success,
            TokenKind.Comment => _theme.Hint,
            TokenKind.Number => _theme.Error,
            TokenKind.Operator => _theme.Label2,
            TokenKind.Punctuation => _theme.Label2,
            _ => _theme.OnSurface,
        };
    }

    /// <summary>
    ///     Entering tokenizer state for <paramref name="target" /> via the cross-frame cache, extending
    ///     it forward from the last known line as needed. The cache is cleared on every content edit
    ///     (see <see cref="Commit" /> / <see cref="SetTextInternal" />), so it stays consistent with the
    ///     text while sparing a focused, scrolled editor a full re-lex from line 0 each frame.
    /// </summary>
    private int EnsureLineStates(ILineTokenizer tokenizer, int target)
    {
        target = Math.Min(target, _lines.Count - 1);
        if (_lineStates.Count == 0)
            _lineStates.Add(ILineTokenizer.StateDefault); // entering state of line 0
        while (_lineStates.Count <= target)
        {
            var l = _lineStates.Count - 1; // last line whose entering state is known
            var entering = _lineStates[l];
            int exiting;
            if (_tokenCache.TryGetValue(l, out var cached) && cached.EnteringState == entering)
            {
                exiting = cached.ExitingState;
            }
            else
            {
                _tokenBuffer.Clear();
                exiting = tokenizer.Tokenize(_lines[l], entering, _tokenBuffer);
                _tokenCache[l] = CacheLineTokens(l, entering, exiting);
            }

            _lineStates.Add(exiting);
        }

        return _lineStates[target];
    }

    private CachedLineTokens TokensForLine(ILineTokenizer tokenizer, int line)
    {
        var entering = EnsureLineStates(tokenizer, line);
        if (_tokenCache.TryGetValue(line, out var cached) && cached.EnteringState == entering)
            return cached;

        _tokenBuffer.Clear();
        var exiting = tokenizer.Tokenize(_lines[line], entering, _tokenBuffer);
        cached = CacheLineTokens(line, entering, exiting);
        _tokenCache[line] = cached;
        return cached;
    }

    private CachedLineTokens CacheLineTokens(int line, int entering, int exiting)
    {
        var tokens = _tokenBuffer.ToArray();
        var colored = new List<ColoredRun>();
        var text = _lines[line];
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Default || token.Length <= 0 ||
                token.Start >= text.Length) continue;
            var length = Math.Min(token.Length, text.Length - token.Start);
            colored.Add(new ColoredRun(token, text.Substring(token.Start, length)));
        }

        return new CachedLineTokens(
            entering,
            exiting,
            tokens,
            colored.ToArray()
        );
    }

    private string LineNo(int line)
    {
        while (_lineNoCache.Count <= line) _lineNoCache.Add((_lineNoCache.Count + 1).ToString());
        return _lineNoCache[line];
    }

    /// Draw one single-line segment in the code face via a cached native layout — so repeated strings
    /// skip re-shaping and the editor exercises the engine's atlas-reset re-shape path — falling back
    /// to immediate
    /// <see cref="PaintList.AddText" />
    /// when layouts are unavailable (headless tests, or a
    /// backend without layout support). The fallback is byte-for-byte the editor's original draw call.
    private void DrawSegment(PaintList paint, string s, float x, float baseline, Color color)
    {
        var layout = LayoutFor(s);
        if (layout is { IsValid: true })
            paint.AddTextLayout(
                layout.Handle,
                x,
                baseline,
                color
            );
        else
            paint.AddText(
                s,
                x,
                baseline,
                color,
                _fontSize,
                Typography.Code.LineHeight,
                fontFamily: "code"
            );
    }

    /// Resolve (creating + caching on first use) the native layout for a single-line code-face
    /// segment, or null when layouts are unavailable so the caller falls back to AddText.
    private TextLayout? LayoutFor(string s)
    {
        if (_layoutsUnavailable) return null;
        // Native text layouts belong to the MAIN window's glyph atlas — in a secondary OS window
        // they resolve against the wrong GpuUi (nothing renders). AddText fallback there.
        if (Owner is { NativeWindow: not null }) return null;
        if (_layouts.TryGetValue(s, out var existing)) return existing;

        var engine = ZigoteEngine.Instance;
        if (engine is null || engine.Handle == 0)
            return null; // headless: fall back without caching

        TextLayout layout;
        try
        {
            // Single-line segments never contain '\n', so lineHeight is immaterial here.
            layout = engine.CreateTextLayout(
                s,
                _fontSize,
                lineHeight: _lineHeight,
                fontFamily: "code"
            );
        }
        catch
        {
            // A backend without layout support (e.g. Metal: zigote_text_layout_create returns 0 →
            // CreateTextLayout throws). Latch it off so we don't throw/catch for every segment, every
            // frame, for the rest of the session — just use AddText.
            _layoutsUnavailable = true;
            return null;
        }

        if (_layouts.Count >= LayoutCacheCap) ClearLayouts();
        _layouts[s] = layout;
        return layout;
    }

    private void ClearLayouts()
    {
        foreach (var l in _layouts.Values) l.Dispose();
        _layouts.Clear();
    }

    public override void Paint(PaintList paint)
    {
        var radius = Radii.Sm;
        paint.AddRect(Bounds, _theme.Surface, radius);
        paint.AddBorder(Bounds, _theme.Separator, radius);

        // Gutter background.
        var gutterRect = new Rect(
            Bounds.X,
            Bounds.Y,
            _gutterWidth,
            Bounds.Height
        );
        paint.AddRect(gutterRect, _theme.Background);
        paint.AddRect(
            new Rect(
                Bounds.X + _gutterWidth - 0.5f,
                Bounds.Y,
                1f,
                Bounds.Height
            ),
            _theme.Separator
        );

        var innerH = Bounds.Height - Pad * 2f;

        // Visible visual-row window (a physical line may occupy several rows when soft-wrapped).
        var first = Math.Max(0, (int)(_scrollY / _lineHeight));
        var last = Math.Min(_visualRows.Count - 1, (int)((_scrollY + innerH) / _lineHeight) + 1);

        // Carried multi-line lexer state entering the first visible line — read from the cross-frame
        // cache (rebuilt lazily, cleared on edit) instead of re-lexing from line 0 every frame.
        var tokenizer = Tokenizer;
        // ── Selection highlight (clipped to the text area) ────────────────────
        paint.AddClipStart(
            new Rect(
                Bounds.X + _gutterWidth,
                Bounds.Y,
                Bounds.Width - _gutterWidth,
                Bounds.Height
            )
        );

        if (HasSelection)
        {
            var (s, e) = OrderedSelection();
            for (var visual = first; visual <= last; visual++)
            {
                var row = _visualRows[visual];
                if (row.Line < s.Line || row.Line > e.Line) continue;
                var c0 = Math.Max(row.Start, row.Line == s.Line ? s.Col : row.Start);
                var c1 = Math.Min(row.End, row.Line == e.Line ? e.Col : row.End);
                if (c1 < c0) continue;
                var x0 = TextLeft - _scrollX + ColToX(row, c0);
                var x1 = TextLeft - _scrollX + ColToX(row, c1);
                // Trailing newline selected lines get a small caret-width sliver.
                if (row.Line < e.Line && row.End == _lines[row.Line].Length) x1 += _fontSize * 0.4f;
                var y = TextTop - _scrollY + visual * _lineHeight;
                paint.AddRect(
                    new Rect(
                        MathF.Min(x0, x1),
                        y,
                        MathF.Max(2f, MathF.Abs(x1 - x0)),
                        _lineHeight
                    ),
                    _theme.Selection.WithAlpha(0.3f)
                );
            }
        }

        // ── Text per visible row ──────────────────────────────────────────────
        for (var visual = first; visual <= last; visual++)
        {
            var row = _visualRows[visual];
            var l = row.Line;
            var line = _lines[l];
            var cachedTokens = tokenizer is not null ? TokensForLine(tokenizer, l) : default;
            var tokens = cachedTokens.Tokens ?? [];
            var y = TextTop - _scrollY + visual * _lineHeight;
            var baseline = y + _fontSize * 0.8f;

            if (row.Start == row.End) continue;

            if (tokens.Length == 0)
            {
                DrawSegment(
                    paint,
                    line[row.Start..row.End],
                    TextLeft - _scrollX,
                    baseline,
                    _theme.OnSurface
                );
                continue;
            }

            // Shape the row once as a coherent run. This preserves ligatures across lexical
            // boundaries and gives default identifiers/whitespace a single cached layout. Colored
            // tokens are overpainted below; common keywords/operators then reuse tiny shared layouts
            // instead of creating a native layout for every identifier while scrolling.
            DrawSegment(
                paint,
                line[row.Start..row.End],
                TextLeft - _scrollX,
                baseline,
                _theme.OnSurface
            );
            foreach (var coloredRun in cachedTokens.ColoredRuns ?? [])
            {
                var tok = coloredRun.Token;
                var tokenEnd = Math.Min(line.Length, tok.Start + tok.Length);
                var start = Math.Max(row.Start, tok.Start);
                var end = Math.Min(row.End, tokenEnd);
                if (end <= start) continue;
                var slice = start == tok.Start && end == tokenEnd
                    ? coloredRun.Text
                    : line[start..end];
                if (slice.Length == 0) continue;
                var x = TextLeft - _scrollX + ColToX(row, start);
                DrawSegment(
                    paint,
                    slice,
                    x,
                    baseline,
                    ColorFor(tok.Kind)
                );
            }
        }

        // ── Caret ──────────────────────────────────────────────────────────────
        if (Focused && !ReadOnly)
        {
            var time = App.Active?.Time ?? 0f;
            if (time % 1.06f < 0.6f || _isDragging)
            {
                var caretRow = RowForCaret(_caretLine, _caretCol);
                var cx = TextLeft - _scrollX + ColToX(_caretLine, _caretCol);
                var cy = TextTop - _scrollY + caretRow * _lineHeight;
                if (_compositionText.Length > 0)
                {
                    DrawSegment(
                        paint,
                        _compositionText,
                        cx,
                        cy + _fontSize * 0.8f,
                        _theme.OnSurface
                    );
                    var compositionWidth = TextMeasure.Width(
                        _compositionText,
                        _fontSize,
                        fontFamily: "code"
                    );
                    paint.AddRect(
                        new Rect(
                            cx,
                            cy + _lineHeight - 2f,
                            MathF.Max(1f, compositionWidth),
                            1f
                        ),
                        _theme.Primary
                    );
                    var compositionCaret = TextNavigation.GraphemeBoundaryAtOrBefore(
                        _compositionText,
                        _compositionSelectionStart + _compositionSelectionLength
                    );
                    cx += TextMeasure.Width(
                        _compositionText[..compositionCaret],
                        _fontSize,
                        fontFamily: "code"
                    );
                }

                paint.AddRect(
                    new Rect(
                        cx,
                        cy,
                        1.5f,
                        _lineHeight
                    ),
                    _theme.Primary
                );
            }

            var imeRow = RowForCaret(_caretLine, _caretCol);
            var imeX = TextLeft - _scrollX + ColToX(_caretLine, _caretCol);
            var imeY = TextTop - _scrollY + imeRow * _lineHeight;
            if (_compositionText.Length > 0)
            {
                var compositionCaret = TextNavigation.GraphemeBoundaryAtOrBefore(
                    _compositionText,
                    _compositionSelectionStart + _compositionSelectionLength
                );
                imeX += TextMeasure.Width(
                    _compositionText[..compositionCaret],
                    _fontSize,
                    fontFamily: "code"
                );
            }

            ZigoteEngine.Instance?.SetTextInputArea(
                new Rect(
                    imeX,
                    imeY,
                    1.5f,
                    _lineHeight
                )
            );
        }

        paint.AddClipEnd();

        // ── Line numbers ────────────────────────────────────────────────────────
        paint.AddClipStart(gutterRect);
        for (var visual = first; visual <= last; visual++)
        {
            var row = _visualRows[visual];
            if (row.Start != 0) continue;
            var l = row.Line;
            var num = LineNo(l);
            var w = num.Length * _charWidth; // monospace
            var nx = Bounds.X + _gutterWidth - GutterPadRight - w;
            var ny = TextTop - _scrollY + visual * _lineHeight + _fontSize * 0.8f;
            var col = l == _caretLine ? _theme.Label2 : _theme.Label3;
            DrawSegment(
                paint,
                num,
                nx,
                ny,
                col
            );
        }

        paint.AddClipEnd();

        // ── Scrollbars (draggable; thicken + brighten while grabbed) ──────────────
        var hbar = HBar();
        if (hbar.On)
        {
            var thumbX = hbar.Start + (hbar.Len - hbar.Thumb) * (_scrollX / hbar.Max);
            var h = _barDrag == 2 ? 4f : 3f;
            paint.AddRect(
                new Rect(
                    thumbX,
                    Bounds.Bottom - h - 2f,
                    hbar.Thumb,
                    h
                ),
                _theme.OnSurface.WithAlpha(_barDrag == 2 ? 0.55f : 0.25f),
                h / 2f
            );
        }

        var vbar = VBar();
        if (vbar.On)
        {
            var thumbY = vbar.Start + (vbar.Len - vbar.Thumb) * (_scrollY / vbar.Max);
            var w = _barDrag == 1 ? 4f : 3f;
            paint.AddRect(
                new Rect(
                    Bounds.Right - w - 2f,
                    thumbY,
                    w,
                    vbar.Thumb
                ),
                _theme.OnSurface.WithAlpha(_barDrag == 1 ? 0.55f : 0.25f),
                w / 2f
            );
        }

        if (Focused)
            paint.AddFocusRing(Bounds, radius, _theme);
    }

    // ── Pointer input ─────────────────────────────────────────────────────────

    private (int Line, int Col) HitPosition(Offset point)
    {
        var visual = (int)((point.Y - (TextTop - _scrollY)) / _lineHeight);
        visual = Math.Clamp(visual, 0, _visualRows.Count - 1);
        var row = _visualRows[visual];
        var localX = point.X - (TextLeft - _scrollX);
        var col = XToCol(row, localX);
        return (row.Line, col);
    }

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);

        // Scrollbar thumbs take priority over caret placement (drag to scroll).
        var vbar = VBar();
        if (vbar.On && point.X >= Bounds.Right - BarHitWidth)
        {
            var thumbY = vbar.Start + (vbar.Len - vbar.Thumb) * (_scrollY / vbar.Max);
            _barGrab = point.Y >= thumbY && point.Y <= thumbY + vbar.Thumb
                ? point.Y - thumbY
                : vbar.Thumb / 2f;
            _barDrag = 1;
            DragVBar(point.Y);
            return;
        }

        var hbar = HBar();
        if (hbar.On && point.Y >= Bounds.Bottom - BarHitWidth && point.X >= Bounds.X + _gutterWidth)
        {
            var thumbX = hbar.Start + (hbar.Len - hbar.Thumb) * (_scrollX / hbar.Max);
            _barGrab = point.X >= thumbX && point.X <= thumbX + hbar.Thumb
                ? point.X - thumbX
                : hbar.Thumb / 2f;
            _barDrag = 2;
            DragHBar(point.X);
            return;
        }

        var (l, col) = HitPosition(point);

        // Double-click selects the word under the cursor.
        var now = App.Active?.Time ?? 0f;
        if (now - _lastClickTime < DoubleClickSeconds && l == _lastClickLine &&
            Math.Abs(col - _lastClickCol) <= 1)
        {
            _lastClickTime = -1f; // consume so a third click doesn't chain
            SelectWordAt(l, col);
            return;
        }

        _lastClickTime = now;
        _lastClickLine = l;
        _lastClickCol = col;

        _caretLine = l;
        _caretCol = col;
        _anchorLine = l;
        _anchorCol = col;
        _isDragging = true;
        MarkNeedsPaint();
    }

    /// <summary>Select the word (or single non-word char) at the given position.</summary>
    private void SelectWordAt(int line, int col)
    {
        var s = _lines[Math.Clamp(line, 0, _lines.Count - 1)];
        var (start, end) = TextNavigation.WordAt(s, col);
        col = Math.Clamp(col, 0, s.Length);

        _isDragging = false;
        if (start == end)
        {
            ClearSelection();
            _caretLine = line;
            _caretCol = col;
            MarkNeedsPaint();
            return;
        }

        _anchorLine = line;
        _anchorCol = start;
        _caretLine = line;
        _caretCol = end;
        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        if (_barDrag == 1)
        {
            DragVBar(point.Y);
            return;
        }

        if (_barDrag == 2)
        {
            DragHBar(point.X);
            return;
        }

        if (!_isDragging) return;
        var (l, col) = HitPosition(point);
        _caretLine = l;
        _caretCol = col;
        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        if (_barDrag != 0)
        {
            _barDrag = 0;
            MarkNeedsPaint();
            return;
        }

        _isDragging = false;
        if (_anchorLine == _caretLine && _anchorCol == _caretCol) ClearSelection();
    }

    public override void OnScroll(float dx, float dy)
    {
        // Shift+wheel scrolls horizontally — mice without a horizontal wheel emit only dy.
        var shift = App.Active?.CurrentModifiers.HasFlag(Modifiers.Shift) ?? false;
        if (shift && dx == 0f)
        {
            dx = dy;
            dy = 0f;
        }

        _targetY -= dy * _lineHeight * 3f;
        _targetX -= dx * _charWidth * 6f;
        ClampTargets();
        AnimateScroll(); // ease the rendered offset toward the new target
    }

    // Touch scrolling. Without these the editor is wheel-only: a finger drag inside it runs
    // OnPointerMove (text selection) and the document never moves. Deltas are already in logical
    // px — no wheel-tick conversion — and the drag tracks the finger 1:1.
    public override bool CanTouchScroll(bool vertical)
    {
        return vertical ? VBar().On : HBar().On;
    }

    public override void OnTouchScroll(float dx, float dy)
    {
        _targetX -= dx;
        _targetY -= dy;
        ClampTargets();
        SnapScroll();
        MarkNeedsPaint();
    }

    public override void OnTouchFling(float velocityX, float velocityY)
    {
        // Reuse the existing ease as the inertia: throw the target ahead and let it settle.
        _targetX -= velocityX * FlingSeconds;
        _targetY -= velocityY * FlingSeconds;
        ClampTargets();
        AnimateScroll();
    }

    public override void OnRightClick(Offset point)
    {
        var menu = new ContextMenu();
        menu.Items.Add(
            new ContextMenuItem("Cut", HasSelection && !ReadOnly ? CutAction : null, Shortcut: "⌘X")
        );
        menu.Items.Add(
            new ContextMenuItem("Copy", HasSelection ? CopyAction : null, Shortcut: "⌘C")
        );
        menu.Items.Add(new ContextMenuItem("Paste", ReadOnly ? null : PasteAction, Shortcut: "⌘V"));
        menu.Items.Add(new ContextMenuItem("", null, true));
        menu.Items.Add(new ContextMenuItem("Select All", SelectAll, Shortcut: "⌘A"));
        menu.ShowAt(point);
    }

    protected override void OnFocusChanged(bool focused)
    {
        MarkNeedsPaint();
    }

    // ── Keyboard input ────────────────────────────────────────────────────────

    public override void OnTextInput(string text)
    {
        if (ReadOnly) return;
        _compositionText = string.Empty;
        InsertText(text, EditKind.Typing);
    }

    public override void OnTextComposition(string text, int selectionStart, int selectionLength)
    {
        if (ReadOnly) return;
        _compositionText = text;
        _compositionSelectionStart = Math.Clamp(selectionStart, 0, text.Length);
        _compositionSelectionLength = Math.Clamp(
            selectionLength,
            0,
            text.Length - _compositionSelectionStart
        );
        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;

        const uint scBackspace = 42;
        const uint scDelete = 76;
        const uint scLeft = 80;
        const uint scRight = 79;
        const uint scUp = 82;
        const uint scDown = 81;
        const uint scHome = 74;
        const uint scEnd = 77;
        const uint scReturn = 40;
        const uint scKpEnter = 88;
        const uint scTab = 43;

        var shift = mods.HasFlag(Modifiers.Shift);
        var cmd = mods.HasCommand(); // Ctrl or ⌘

        if (cmd)
            switch (char.ToLowerInvariant(keyChar))
            {
                case 'a':
                    SelectAll();
                    return;
                case 'c':
                    CopyAction();
                    return;
                case 'x':
                    CutAction();
                    return;
                case 'v':
                    PasteAction();
                    return;
                case 's':
                    OnSubmit?.Invoke();
                    return;
                case 'z':
                    if (shift) Redo();
                    else Undo();
                    return;
                case 'y':
                    Redo();
                    return;
            }

        switch (scancode)
        {
            case scLeft:
                MoveCaret(shift, -1);
                return;
            case scRight:
                MoveCaret(shift, +1);
                return;
            case scUp:
                MoveCaretVertical(shift, -1);
                return;
            case scDown:
                MoveCaretVertical(shift, +1);
                return;
            case scHome:
                StartOrExtendSelection(shift);
                _caretCol = SoftWrap ? _visualRows[RowForCaret(_caretLine, _caretCol)].Start : 0;
                EnsureCaretVisible();
                MarkNeedsPaint();
                return;
            case scEnd:
                StartOrExtendSelection(shift);
                _caretCol = SoftWrap
                    ? _visualRows[RowForCaret(_caretLine, _caretCol)].End
                    : _lines[_caretLine].Length;
                EnsureCaretVisible();
                MarkNeedsPaint();
                return;
        }

        if (ReadOnly) return;

        switch (scancode)
        {
            case scReturn:
            case scKpEnter:
            {
                RecordEdit(EditKind.Other);
                if (HasSelection) DeleteSelection();
                var cur = _lines[_caretLine];
                var indent = LeadingWhitespace(cur);
                var before = cur[.._caretCol];
                var after = cur[_caretCol..];
                _lines[_caretLine] = before;
                _lines.Insert(_caretLine + 1, indent + after);
                _caretLine++;
                _caretCol = indent.Length;
                ClearSelection();
                Commit();
                return;
            }

            case scTab:
                InsertText(new string(' ', TabWidth));
                return;

            case scBackspace:
                RecordEdit(EditKind.Deleting);
                if (HasSelection)
                {
                    DeleteSelection();
                    Commit();
                    return;
                }

                if (_caretCol > 0)
                {
                    var previous = TextNavigation.PreviousGraphemeBoundary(
                        _lines[_caretLine],
                        _caretCol
                    );
                    _lines[_caretLine] =
                        _lines[_caretLine][..previous] + _lines[_caretLine][_caretCol..];
                    _caretCol = previous;
                }
                else if (_caretLine > 0)
                {
                    var prevLen = _lines[_caretLine - 1].Length;
                    _lines[_caretLine - 1] += _lines[_caretLine];
                    _lines.RemoveAt(_caretLine);
                    _caretLine--;
                    _caretCol = prevLen;
                }

                Commit();
                return;

            case scDelete:
                RecordEdit(EditKind.Deleting);
                if (HasSelection)
                {
                    DeleteSelection();
                    Commit();
                    return;
                }

                if (_caretCol < _lines[_caretLine].Length)
                {
                    var next = TextNavigation.NextGraphemeBoundary(_lines[_caretLine], _caretCol);
                    _lines[_caretLine] =
                        _lines[_caretLine][.._caretCol] + _lines[_caretLine][next..];
                }
                else if (_caretLine < _lines.Count - 1)
                {
                    _lines[_caretLine] += _lines[_caretLine + 1];
                    _lines.RemoveAt(_caretLine + 1);
                }

                Commit();
                return;
        }
    }

    private static string LeadingWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s[..i];
    }

    private void MoveCaret(bool extend, int dCol)
    {
        StartOrExtendSelection(extend);
        if (!extend && HasSelection)
        {
            // Collapse selection to the appropriate edge.
            var (s, e) = OrderedSelection();
            (_caretLine, _caretCol) = dCol < 0 ? s : e;
            ClearSelection();
            EnsureCaretVisible();
            MarkNeedsPaint();
            return;
        }

        var currentRowIndex = RowForCaret(_caretLine, _caretCol);
        var currentRow = _visualRows[currentRowIndex];
        var rowText = _lines[_caretLine][currentRow.Start..currentRow.End];
        var rowLayout = currentRow.FastGrid ? null : LayoutFor(rowText);
        var relative = _caretCol - currentRow.Start;
        var visualMoved = rowLayout?.MoveCaretVisual(relative, dCol) ??
                          (dCol < 0
                              ? TextNavigation.PreviousGraphemeBoundary(rowText, relative)
                              : TextNavigation.NextGraphemeBoundary(rowText, relative));
        if (visualMoved != relative)
        {
            _caretCol = currentRow.Start + visualMoved;
        }
        else if (dCol < 0)
        {
            if (currentRowIndex > 0)
            {
                var previousRow = _visualRows[currentRowIndex - 1];
                _caretLine = previousRow.Line;
                _caretCol = previousRow.End;
            }
            else if (_caretCol > 0)
            {
                _caretCol = TextNavigation.PreviousGraphemeBoundary(_lines[_caretLine], _caretCol);
            }
        }
        else
        {
            if (currentRowIndex < _visualRows.Count - 1)
            {
                var nextRow = _visualRows[currentRowIndex + 1];
                _caretLine = nextRow.Line;
                _caretCol = nextRow.Start;
            }
            else if (_caretCol < _lines[_caretLine].Length)
            {
                _caretCol = TextNavigation.NextGraphemeBoundary(_lines[_caretLine], _caretCol);
            }
        }

        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    private void MoveCaretVertical(bool extend, int dLine)
    {
        StartOrExtendSelection(extend);
        var current = RowForCaret(_caretLine, _caretCol);
        var target = Math.Clamp(current + dLine, 0, _visualRows.Count - 1);
        if (target == current) return;
        var desiredX = ColToX(_visualRows[current], _caretCol);
        var targetRow = _visualRows[target];
        _caretLine = targetRow.Line;
        _caretCol = XToCol(targetRow, desiredX);
        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    /// <summary>Classifies an edit for undo coalescing — same-kind runs collapse into one undo step.</summary>
    private enum EditKind
    {
        Other,
        Typing,
        Deleting,
    }

    private readonly record struct DocSnapshot(
        string[] Lines,
        int CaretLine,
        int CaretCol,
        int AnchorLine,
        int AnchorCol);

    private readonly record struct VisualRow(
        int Line,
        int Start,
        int End,
        float Width,
        bool FastGrid);

    private readonly record struct ColoredRun(Token Token, string Text);

    private readonly record struct CachedLineTokens(
        int EnteringState,
        int ExitingState,
        Token[] Tokens,
        ColoredRun[] ColoredRuns);
}