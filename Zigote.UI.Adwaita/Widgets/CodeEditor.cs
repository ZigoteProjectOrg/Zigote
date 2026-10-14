using System.Text;
using Zigote.Core.Animation;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     A multiline, monospace code editor. Syntax highlighting is delegated to an injected
///     <see cref="Tokenizer" /> (no LSP, no colour schemes) — the language parsers themselves live
///     outside <c>Zigote.UI</c>, in the XParsec-based <c>Zigote.Modules.UI.CodeEditor</c> module.
///     Renders in the bundled Iosevka face (<see cref="Typography.Code" />, family
///     <c>"code"</c>) with a right-aligned line-number gutter, vertical + horizontal scrolling, caret
///     /
///     selection, clipboard, and the standard editing keys.
///     <para>
///         Implements <see cref="ITextInputClient" /> so the app's focus gate routes printable input
///         to
///         <see cref="OnTextInput" /> even though it isn't a <c>AdwTextField</c>.
///     </para>
/// </summary>
public sealed class AdwCodeEditor : Widget, ITextInputClient, IKeyboardTrap
{
    private const int TabWidth = 4;
    private const string Openers = "([{\"'`";
    private const string Closers = ")]}\"'`";

    // Bracket matching gives up after this many lines so an unbalanced bracket can't scan the file.
    private const int BracketScanLines = 2000;

    // Undo: cap the snapshot history and coalesce same-kind edits made within this window into one step
    // (so undo walks back by word-ish runs, not per keystroke).
    private const int UndoDepth = 256;

    // Memory bound on the undo stack alongside UndoDepth: snapshots are full line arrays, so 256
    // of them on a 10k-line file retained ~20 MB. Oldest snapshots trim first when the total line
    // count exceeds this (~10 MB worst case). ponytail: full snapshots; line-range deltas if undo
    // memory ever matters more.
    private const int UndoMaxRetainedLines = 131_072;
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

    // Scratch for EvictLayouts.
    private List<string>? _layoutEvictScratch;

    // Soft-wrap memo keyed by line content — see RebuildVisualRows. Ordinal keying means lines
    // with identical text share one entry, which is exactly right (same text + width ⇒ same rows).
    private readonly Dictionary<string, CachedWrap> _wrapCache = new(StringComparer.Ordinal);

    private readonly record struct CachedWrap(
        float WrapWidth,
        float CharWidth,
        (int Start, int End, float Width)[] Rows);

    // Line-number strings cached by line index (index l always renders "l+1") so the gutter doesn't
    // allocate a string per visible line per frame.
    private readonly List<string> _lineNoCache = [];

    // Entering tokenizer state per line (index = line). Cached across frames, rebuilt lazily by
    // EnsureLineStates, and cleared on any content edit — so a focused editor no longer re-lexes from
    // line 0 every frame while scrolled down.
    private readonly List<int> _lineStates = [];

    private readonly List<string> _lines = [""];
    private readonly List<DocSnapshot> _redo = [];
    private readonly List<Token> _tokenBuffer = []; // reused per painted line — no per-frame alloc
    private readonly Dictionary<int, CachedLineTokens> _tokenCache = [];

    // Undo/redo: snapshot stacks of the whole document + caret/selection. Cleared when a new document
    // is loaded (SetTextInternal). See RecordEdit / Undo / Redo.
    private readonly List<DocSnapshot> _undo = [];

    private readonly List<VisualRow> _visualRows = [];

    // Multi-cursor: the primary caret lives in the _caret*/_anchor* fields, so everything written
    // for one caret keeps working untouched; the additional carets park here and ForEachCaret swaps
    // them through those fields one at a time.
    private readonly List<Sel> _extra = [];
    private int _anchorCol;
    private int _anchorLine = -1;
    private int _barDrag; // 0 = none, 1 = vertical scrollbar, 2 = horizontal scrollbar
    private float _barGrab; // pointer offset within the grabbed thumb
    private int _caretCol;

    // Caret + selection as (line, col). Anchor < 0 means no selection.
    private int _caretLine;
    private int _clickCount; // consecutive clicks on one spot: 2 = word, 3 = line
    private float _desiredX = float.NaN; // sticky column for ↑/↓ (NaN = take it from the caret)

    // Incremental find (⌘F): the query while the bar is open, null when closed. Typed text goes to
    // the query instead of the document.
    private string? _find;

    private string?
        _findLabel; // "3/17" — rebuilt lazily after the query, selection or text changes

    // The whole-line text last put on the clipboard by a selection-less copy/cut; pasting that exact
    // text inserts it as a line above the caret rather than splitting the current line.
    private string? _lineClip;

    // ⌘D started from a bare caret (it selected the word) → further matches must be whole words.
    private bool _occWholeWord;
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

    public AdwCodeEditor(string text = "") => SetTextInternal(text);

    public string Text
    {
        get => string.Join(separator: '\n', values: _lines);
        set => SetTextInternal(value);
    }

    /// <summary>
    ///     The syntax highlighter. <c>null</c> renders plain, unhighlighted text. Concrete tokenizers
    ///     (C#, JSON, WGSL, Zig) are produced by <c>Zigote.Modules.UI.CodeEditor</c>'s XParsec parsers.
    /// </summary>
    public ILineTokenizer? Tokenizer
    {
        get => _tokenizer;
        set
        {
            if (ReferenceEquals(objA: _tokenizer, objB: value)) return;
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

    /// <summary>The line-comment prefix ⌘/ toggles; <c>null</c> disables the command.</summary>
    public string? LineComment { get; set; } = "//";

    /// <summary>Wrap long physical lines into viewport-width visual rows without changing the document.</summary>
    public bool SoftWrap
    {
        get => _softWrap;
        set => SetLayout(field: ref _softWrap, value: value);
    }

    public override bool Focusable => true;

    /// <summary>The editor owns the arrow keys for caret movement, so they're never repurposed for focus.</summary>
    public override bool HandlesDirectionalKeys => true;

    /// <summary>Optional accessible name for the editor (e.g. the file or field it edits).</summary>
    public string? SemanticsLabel { get; set; }

    // ── Selection helpers ─────────────────────────────────────────────────────

    private bool HasSelection =>
        _anchorLine >= 0 && (_anchorLine != _caretLine || _anchorCol != _caretCol);

    private Sel Primary
    {
        get => new(
            AnchorLine: _anchorLine,
            AnchorCol: _anchorCol,
            CaretLine: _caretLine,
            CaretCol: _caretCol,
            DesiredX: _desiredX
        );
        set => (_anchorLine, _anchorCol, _caretLine, _caretCol, _desiredX) = value;
    }

    /// <summary>Content origin (top-left of the text area, after the gutter), excluding scroll.</summary>
    private float TextLeft => Bounds.X + _gutterWidth + Pad;

    private float TextTop => Bounds.Y + Pad;

    /// <summary>
    ///     Editor font size in points; null (the default) uses the <see cref="Typography.Code" />
    ///     ramp size. Setting it drops cached native text layouts and relayouts.
    /// </summary>
    public float? FontSize
    {
        get => _fontSizeOverride;
        set
        {
            if (Nullable.Equals(n1: _fontSizeOverride, n2: value)) return;
            _fontSizeOverride = value;
            InvalidateTextLayouts();
        }
    }

    // Only an editable, focused editor blinks a caret; a read-only docked viewer doesn't, so the frame
    // loop can stop repainting it every frame (see App's focus repaint gate / ITextInputClient).
    public bool WantsCaretBlink => Focused && (!ReadOnly || _find is not null);

    // A drag holds the caret solid, but its pointer events repaint the editor anyway.
    public float? CaretBlinkBase => 0f;

    public override MouseCursor? GetCursor(Offset point) => MouseCursor.Text;

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.TextField;
        config.Label = SemanticsLabel;
        config.Value = Text;
        config.Actions = SemanticsAction.Focus |
                         (ReadOnly ? SemanticsAction.None : SemanticsAction.SetValue);
        config.AddFlag(SemanticsFlags.Focusable)
            .AddFlag(flag: SemanticsFlags.Focused, on: Focused)
            .AddFlag(SemanticsFlags.Multiline)
            .AddFlag(flag: SemanticsFlags.ReadOnly, on: ReadOnly);
    }

    private void SetTextInternal(string text)
    {
        _lines.Clear();
        string[] split = (text ?? string.Empty).Replace(oldValue: "\r\n", newValue: "\n")
            .Replace(oldChar: '\r', newChar: '\n').Split('\n');
        foreach (string l in split) _lines.Add(l);
        if (_lines.Count == 0) _lines.Add("");
        _lineStates.Clear(); // whole document changed — drop the entering-state cache
        _tokenCache.Clear();
        ClearLayouts(); // and the cached glyph layouts — the old lines' strings may never recur
        _undo.Clear(); // a freshly loaded document has no edit history to step back into
        _redo.Clear();
        _lastEditKind = EditKind.Other;
        _extra.Clear();
        _find = null;
        ClampCaret();
        ClearSelection();
        _scrollX = _scrollY = _targetX = _targetY = 0f; // new document starts at the top
        _scrollTicker?.Stop();
        MarkNeedsPaint();
    }

    private void ClearSelection() => _anchorLine = -1;

    /// <summary>
    ///     Ordered (start, end) selection bounds as (line, col), start &lt;= end — both the caret
    ///     when nothing is selected.
    /// </summary>
    private ((int Line, int Col) Start, (int Line, int Col) End) OrderedSelection() =>
        Range(Primary);

    private static ((int Line, int Col) Start, (int Line, int Col) End) Range(Sel x)
    {
        var b = (x.CaretLine, x.CaretCol);
        if (x.AnchorLine < 0) return (b, b);
        var a = (x.AnchorLine, x.AnchorCol);
        return Before(a: a, b: b) ? (a, b) : (b, a);
    }

    // ── Multi-cursor ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Run a single-caret operation once per caret, in document order, with each caret loaded
    ///     into the primary fields in turn. Edits go through <see cref="Splice" />, which keeps the
    ///     carets still waiting their turn (and those already done) pointing at the same text.
    /// </summary>
    private void ForEachCaret(Action op)
    {
        if (_extra.Count == 0)
        {
            op();
            return;
        }

        var primary = Primary;
        _extra.Add(primary);
        _extra.Sort(static (a, b) =>
            {
                var (sa, sb) = (Range(a).Start, Range(b).Start);
                return sa.Line != sb.Line ? sa.Line.CompareTo(sb.Line) : sa.Col.CompareTo(sb.Col);
            }
        );
        int primaryIndex = _extra.IndexOf(primary);
        for (int i = 0; i < _extra.Count; i++)
        {
            Primary = _extra[i];
            op();
            _extra[i] = Primary;
        }

        Primary = _extra[primaryIndex];
        _extra.RemoveAt(primaryIndex);
        MergeCarets();
    }

    /// <summary>Drop carets that landed on, or selections that grew into, another one.</summary>
    private void MergeCarets()
    {
        // ponytail: O(n²) overlap sweep; sort + one linear pass if thousands of carets ever lag.
        for (int i = _extra.Count - 1; i >= 0; i--)
        {
            bool dup = Overlaps(a: _extra[i], b: Primary);
            for (int j = 0; j < i && !dup; j++) dup = Overlaps(a: _extra[i], b: _extra[j]);
            if (dup) _extra.RemoveAt(i);
        }
    }

    private static bool Overlaps(Sel a, Sel b)
    {
        var (sa, ea) = Range(a);
        var (sb, eb) = Range(b);
        // Two selections may touch end-to-start (adjacent ⌘D matches); a bare caret may not sit
        // on another selection's edge.
        return sa == ea || sb == eb
            ? Before(a: sa, b: eb) && Before(a: sb, b: ea)
            : !Before(a: eb, b: sa) && !Before(a: ea, b: sb);
    }

    /// <summary>Pass every caret and anchor (primary and parked) through <paramref name="f" />.</summary>
    private void MapCarets(Func<(int Line, int Col), (int Line, int Col)> f)
    {
        (_caretLine, _caretCol) = f((_caretLine, _caretCol));
        if (_anchorLine >= 0) (_anchorLine, _anchorCol) = f((_anchorLine, _anchorCol));
        for (int i = 0; i < _extra.Count; i++)
        {
            var x = _extra[i];
            (int cl, int cc) = f((x.CaretLine, x.CaretCol));
            (int al, int ac) = x.AnchorLine < 0 ? (-1, 0) : f((x.AnchorLine, x.AnchorCol));
            _extra[i] = x with {
                AnchorLine = al,
                AnchorCol = ac,
                CaretLine = cl,
                CaretCol = cc,
            };
        }
    }

    private void ResetStickyColumn()
    {
        _desiredX = float.NaN;
        for (int i = 0; i < _extra.Count; i++) _extra[i] = _extra[i] with { DesiredX = float.NaN };
    }

    /// <summary>Every line any caret or selection touches, ascending, each once.</summary>
    private List<int> TouchedLines()
    {
        var set = new SortedSet<int>();
        for (int i = -1; i < _extra.Count; i++)
        {
            var (s, e) = Range(i < 0 ? Primary : _extra[i]);
            // A selection that ends at column 0 doesn't reach into that last line.
            int last = e.Col == 0 && e.Line > s.Line ? e.Line - 1 : e.Line;
            for (int l = s.Line; l <= last; l++) set.Add(l);
        }

        return [.. set];
    }

    /// <summary><see cref="TouchedLines" /> grouped into contiguous (first, last) blocks.</summary>
    private List<(int First, int Last)> TouchedRuns()
    {
        var runs = new List<(int First, int Last)>();
        foreach (int l in TouchedLines())
        {
            if (runs.Count > 0 && runs[^1].Last == l - 1) runs[^1] = (runs[^1].First, l);
            else runs.Add((l, l));
        }

        return runs;
    }

    private static bool Before((int Line, int Col) a, (int Line, int Col) b) =>
        a.Line < b.Line || (a.Line == b.Line && a.Col <= b.Col);

    private void ClampCaret()
    {
        _caretLine = Math.Clamp(value: _caretLine, min: 0, max: _lines.Count - 1);
        _caretCol = TextNavigation.GraphemeBoundaryAtOrBefore(
            text: _lines[_caretLine],
            index: Math.Clamp(value: _caretCol, min: 0, max: _lines[_caretLine].Length)
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
            ClearSelection();
    }

    // ── Edit primitives ───────────────────────────────────────────────────────

    /// <summary>
    ///     The one place the document changes: replace <c>[s, e)</c> with <paramref name="text" />
    ///     and shift every caret and anchor so it still points at the same text. Returns the end of
    ///     the inserted text.
    /// </summary>
    private (int Line, int Col) Splice((int Line, int Col) s, (int Line, int Col) e, string text)
    {
        // ponytail: O(lines) list surgery + O(carets) remap per call, so an edit at N carets is
        // O(N·(lines + N)) — fine into the low thousands; batch the splices if select-all-
        // occurrences on huge files ever stutters.
        string[] parts = text.Split('\n');
        (int Line, int Col) end = (s.Line + parts.Length - 1,
            (parts.Length == 1 ? s.Col : 0) + parts[^1].Length);
        parts[0] = _lines[s.Line][..s.Col] + parts[0];
        parts[^1] += _lines[e.Line][e.Col..];
        _lines.RemoveRange(index: s.Line, count: e.Line - s.Line + 1);
        _lines.InsertRange(index: s.Line, collection: parts);
        MapCarets(p =>
            !Before(a: e, b: p) // p strictly before e: untouched, or swallowed by the edit
                ? Before(a: p, b: s) ? p : end
                : p.Line == e.Line
                    ? (end.Line, end.Col + p.Col - e.Col)
                    : (p.Line + end.Line - e.Line, p.Col)
        );
        return end;
    }

    /// <summary><see cref="Splice" />, leaving the (collapsed) caret after the inserted text.</summary>
    private void Replace((int Line, int Col) s, (int Line, int Col) e, string text)
    {
        (_caretLine, _caretCol) = Splice(s: s, e: e, text: text);
        ClearSelection();
    }

    private void ReplaceSelection(string text)
    {
        var (s, e) = OrderedSelection();
        Replace(s: s, e: e, text: text);
    }

    private void DeleteSelection()
    {
        if (HasSelection) ReplaceSelection(string.Empty);
    }

    /// <summary>One undoable user edit: snapshot, run the per-caret operation at every caret, commit.</summary>
    private void Edit(EditKind kind, Action perCaret)
    {
        if (ReadOnly) return;
        RecordEdit(kind);
        ForEachCaret(perCaret);
        Commit();
    }

    private void InsertText(string text, EditKind kind = EditKind.Other)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = text.Replace(oldValue: "\r\n", newValue: "\n").Replace(oldChar: '\r', newChar: '\n');
        Edit(kind: kind, perCaret: () => ReplaceSelection(text));
    }

    private void Commit()
    {
        // The entering-state chain is prefix-dependent, so it rebuilds lazily — but the rebuild
        // walk now reuses every token-cache entry whose (entering state, line instance) still
        // match, so a keystroke costs re-tokenizing the edited lines, not the whole file above
        // the viewport. Entries for shifted/removed indices self-invalidate by reference compare;
        // bound the stragglers a shrink leaves behind.
        _lineStates.Clear();
        _findLabel = null;
        if (_tokenCache.Count > (_lines.Count * 2) + 64) _tokenCache.Clear();
        if (Bounds.Width > 0f)
        {
            RebuildVisualRows(
                MathF.Max(x: _charWidth, y: Bounds.Width - _gutterWidth - (Pad * 2f))
            );
            _contentWidth = 0f;
            foreach (var row in _visualRows)
                _contentWidth = MathF.Max(x: _contentWidth, y: row.Width);
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
        float now = App.Active?.Time ?? 0f;
        bool coalesce = kind != EditKind.Other && kind == _lastEditKind
                                               && _undo.Count > 0 && now - _lastEditTime <
                                               UndoCoalesceSeconds;
        if (!coalesce)
        {
            _undo.Add(CaptureSnapshot());
            if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
            // Trim oldest while the stack retains too many total lines (large files).
            long retained = 0;
            for (int i = 0; i < _undo.Count; i++) retained += _undo[i].Lines.Length;
            while (retained > UndoMaxRetainedLines && _undo.Count > 1)
            {
                retained -= _undo[0].Lines.Length;
                _undo.RemoveAt(0);
            }

            _redo.Clear(); // a fresh edit invalidates the redo branch
        }

        _lastEditKind = kind;
        _lastEditTime = now;
    }

    private DocSnapshot CaptureSnapshot()
    {
        return new DocSnapshot(
            Lines: _lines.ToArray(),
            CaretLine: _caretLine,
            CaretCol: _caretCol,
            AnchorLine: _anchorLine,
            AnchorCol: _anchorCol,
            Extra: _extra.Count == 0 ? [] : _extra.ToArray()
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
        _extra.Clear();
        _extra.AddRange(s.Extra);
        ClampCaret();
        Commit(); // rebuild caches/rows, fire OnChanged, keep the caret in view
    }

    // ── Clipboard ─────────────────────────────────────────────────────────────

    private string SelectedText()
    {
        var (s, e) = OrderedSelection();
        if (s.Line == e.Line) return _lines[s.Line][s.Col..e.Col];
        var sb = new StringBuilder();
        sb.Append(_lines[s.Line][s.Col..]).Append('\n');
        for (int l = s.Line + 1; l < e.Line; l++) sb.Append(_lines[l]).Append('\n');
        sb.Append(_lines[e.Line][..e.Col]);
        return sb.ToString();
    }

    private void CopyAction()
    {
        // Every caret contributes its selection — or, with nothing selected, its whole line.
        var parts = new List<string>();
        bool lines = !HasSelection && _extra.Count == 0;
        ForEachCaret(() => parts.Add(HasSelection ? SelectedText() : _lines[_caretLine]));
        string text = string.Join(separator: '\n', values: parts) + (lines ? "\n" : "");
        _lineClip = lines ? text : null;
        ZigoteEngine.Instance?.SetClipboard(text);
    }

    private void CutAction()
    {
        bool wholeLine = !HasSelection && _extra.Count == 0;
        CopyAction();
        if (wholeLine) DeleteLines();
        else Edit(kind: EditKind.Other, perCaret: DeleteSelection);
    }

    private void PasteAction()
    {
        string pasted = (ZigoteEngine.Instance?.GetClipboard() ?? string.Empty)
            .Replace(oldValue: "\r\n", newValue: "\n").Replace(oldChar: '\r', newChar: '\n');
        if (pasted.Length == 0) return;
        if (pasted == _lineClip && !HasSelection && _extra.Count == 0)
        {
            // A line copied without a selection goes back in as a line, above the caret's.
            Edit(
                kind: EditKind.Other,
                perCaret: () => Splice(s: (_caretLine, 0), e: (_caretLine, 0), text: pasted)
            );
            return;
        }

        // One clipboard line per caret (the shape a multi-cursor copy produces) → deal them out.
        string[] parts = pasted.Split('\n');
        if (_extra.Count > 0 && parts.Length == _extra.Count + 1)
        {
            int n = 0;
            Edit(kind: EditKind.Other, perCaret: () => ReplaceSelection(parts[n++]));
        }
        else
            InsertText(pasted);
    }

    private void SelectAll()
    {
        _extra.Clear();
        _anchorLine = 0;
        _anchorCol = 0;
        _caretLine = _lines.Count - 1;
        _caretCol = _lines[_caretLine].Length;
        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    // ── Occurrences + find ────────────────────────────────────────────────────

    /// <summary>First match of <paramref name="needle" /> in <paramref name="s" /> at or after <paramref name="from" />, or -1.</summary>
    private static int NextMatch(
        string s,
        ReadOnlySpan<char> needle,
        int from,
        bool wholeWord,
        StringComparison cmp)
    {
        for (int i = from; i <= s.Length - needle.Length; i++)
        {
            int found = s.AsSpan(i).IndexOf(value: needle, comparisonType: cmp);
            if (found < 0) return -1;
            i += found;
            int end = i + needle.Length;
            if (!wholeWord ||
                ((i == 0 || !TextNavigation.IsWordChar(s[i - 1])) &&
                 (end == s.Length || !TextNavigation.IsWordChar(s[end]))))
                return i;
        }

        return -1;
    }

    /// <summary>
    ///     The next match starting at or after <paramref name="from" /> (forward), or the last one
    ///     starting before it (backward), wrapping around the document.
    /// </summary>
    private (int Line, int Col)? FindNext(
        string needle,
        (int Line, int Col) from,
        bool forward,
        bool wholeWord,
        StringComparison cmp)
    {
        int n = _lines.Count;
        for (int k = 0; k <= n; k++) // k == n revisits the starting line for the wrapped half
        {
            int l = (((from.Line + (forward ? k : -k)) % n) + n) % n;
            string s = _lines[l];
            if (forward)
            {
                int i = NextMatch(
                    s: s,
                    needle: needle,
                    from: k == 0 ? from.Col : 0,
                    wholeWord: wholeWord,
                    cmp: cmp
                );
                if (i >= 0) return (l, i);
                continue;
            }

            int limit = k == 0 ? from.Col : int.MaxValue;
            int last = -1;
            for (int i = NextMatch(
                     s: s,
                     needle: needle,
                     from: 0,
                     wholeWord: wholeWord,
                     cmp: cmp
                 );
                 i >= 0 && i < limit;
                 i = NextMatch(
                     s: s,
                     needle: needle,
                     from: i + 1,
                     wholeWord: wholeWord,
                     cmp: cmp
                 ))
                last = i;
            if (last >= 0) return (l, last);
        }

        return null;
    }

    // Smart case, as in Zed/ripgrep: a query with no capitals matches any case.
    private StringComparison FindComparison =>
        _find is { } q && q.Any(char.IsUpper)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    ///     ⌘D. With a bare caret, select the word under it; with a selection, add a cursor on its
    ///     next occurrence (wrapping), which becomes the primary so the view follows it.
    /// </summary>
    public void SelectNextOccurrence()
    {
        if (!HasSelection)
        {
            (int ws, int we) = TextNavigation.WordAt(text: _lines[_caretLine], pos: _caretCol);
            if (ws == we) return;
            (_anchorLine, _anchorCol, _caretCol) = (_caretLine, ws, we);
            _occWholeWord = true;
        }
        else
        {
            var (s, e) = OrderedSelection();
            if (s.Line != e.Line) return; // ponytail: single-line needles only
            string needle = _lines[s.Line][s.Col..e.Col];
            var from = e;
            for (int guard = 0; guard <= _extra.Count + 1; guard++)
            {
                var m = FindNext(
                    needle: needle,
                    from: from,
                    forward: true,
                    wholeWord: _occWholeWord,
                    cmp: StringComparison.Ordinal
                );
                if (m is not { } hit || hit == s) break; // wrapped back onto ourselves
                from = (hit.Line, hit.Col + needle.Length);
                if (_extra.Exists(x => Range(x).Start == hit)) continue; // already has a cursor
                _extra.Add(Primary);
                Primary = new Sel(
                    AnchorLine: hit.Line,
                    AnchorCol: hit.Col,
                    CaretLine: hit.Line,
                    CaretCol: from.Col
                );
                break;
            }
        }

        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    /// <summary>
    ///     ⌘⇧L. A cursor on every occurrence of the find query, the selection, or the word under
    ///     the caret — type to replace them all.
    /// </summary>
    public void SelectAllOccurrences()
    {
        string needle;
        bool wholeWord = false;
        var cmp = StringComparison.Ordinal;
        var (s, e) = OrderedSelection();
        if (!string.IsNullOrEmpty(_find))
        {
            needle = _find;
            cmp = FindComparison;
        }
        else if (HasSelection)
        {
            if (s.Line != e.Line) return;
            needle = _lines[s.Line][s.Col..e.Col];
            wholeWord = _occWholeWord;
        }
        else
        {
            (int ws, int we) = TextNavigation.WordAt(text: _lines[_caretLine], pos: _caretCol);
            if (ws == we) return;
            needle = _lines[_caretLine][ws..we];
            wholeWord = true;
            s = (_caretLine, ws);
        }

        var all = new List<Sel>();
        int primary = 0;
        for (int l = 0; l < _lines.Count; l++)
        {
            for (int i = NextMatch(
                     s: _lines[l],
                     needle: needle,
                     from: 0,
                     wholeWord: wholeWord,
                     cmp: cmp
                 );
                 i >= 0;
                 i = NextMatch(
                     s: _lines[l],
                     needle: needle,
                     from: i + needle.Length,
                     wholeWord: wholeWord,
                     cmp: cmp
                 ))
            {
                if ((l, i) == s) primary = all.Count;
                all.Add(
                    new Sel(
                        AnchorLine: l,
                        AnchorCol: i,
                        CaretLine: l,
                        CaretCol: i + needle.Length
                    )
                );
            }
        }

        if (all.Count == 0) return;
        _find = null;
        Primary = all[primary];
        all.RemoveAt(primary);
        _extra.Clear();
        _extra.AddRange(all);
        MarkNeedsPaint();
    }

    private void OpenFind()
    {
        var (s, e) = OrderedSelection();
        _find = HasSelection && s.Line == e.Line ? _lines[s.Line][s.Col..e.Col] : string.Empty;
        _findLabel = null;
        MarkNeedsPaint();
    }

    /// <summary>
    ///     Select the next/previous match of the find query. <paramref name="refine" /> (the query
    ///     just changed) searches from the current match's start so it stays put while it still fits.
    /// </summary>
    private void FindStep(bool forward, bool refine = false)
    {
        _findLabel = null;
        MarkNeedsPaint();
        if (string.IsNullOrEmpty(_find)) return;
        var (s, e) = OrderedSelection();
        var m = FindNext(
            needle: _find,
            from: forward && !refine ? e : s,
            forward: forward,
            wholeWord: false,
            cmp: FindComparison
        );
        if (m is not { } hit) return;
        _extra.Clear();
        Primary = new Sel(
            AnchorLine: hit.Line,
            AnchorCol: hit.Col,
            CaretLine: hit.Line,
            CaretCol: hit.Col + _find.Length
        );
        EnsureCaretVisible();
    }

    /// <summary>"3/17" for the find bar: which match is selected, of how many.</summary>
    private string FindLabel()
    {
        if (_findLabel is not null) return _findLabel;
        if (string.IsNullOrEmpty(_find)) return _findLabel = string.Empty;
        var start = OrderedSelection().Start;
        var cmp = FindComparison;
        int total = 0, current = 0;
        for (int l = 0; l < _lines.Count; l++)
        {
            for (int i = NextMatch(
                     s: _lines[l],
                     needle: _find,
                     from: 0,
                     wholeWord: false,
                     cmp: cmp
                 );
                 i >= 0;
                 i = NextMatch(
                     s: _lines[l],
                     needle: _find,
                     from: i + _find.Length,
                     wholeWord: false,
                     cmp: cmp
                 ))
            {
                total++;
                if ((l, i) == start) current = total;
            }
        }

        return _findLabel = total == 0 ? "no results" : $"{current}/{total}";
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    private int RowForCaret(int line, int col)
    {
        if (_visualRows.Count == 0)
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                _visualRows.Add(
                    new VisualRow(
                        Line: i,
                        Start: 0,
                        End: _lines[i].Length,
                        Width: 0f,
                        FastGrid: IsFastGridText(_lines[i])
                    )
                );
            }
        }

        // Unwrapped: one row per line, so the row IS the line — no scan (matters with many carets).
        if (!SoftWrap && line < _visualRows.Count && _visualRows[line].Line == line) return line;

        for (int i = 0; i < _visualRows.Count; i++)
        {
            var row = _visualRows[i];
            if (row.Line == line && col >= row.Start && col <= row.End) return i;
        }

        return Math.Clamp(value: line, min: 0, max: Math.Max(val1: 0, val2: _visualRows.Count - 1));
    }

    private float ColToX(int line, int col)
    {
        if (_visualRows.Count == 0) return 0f;
        return ColToX(row: _visualRows[RowForCaret(line: line, col: col)], col: col);
    }

    private float ColToX(VisualRow row, int col)
    {
        string line = _lines[row.Line];
        col = TextNavigation.GraphemeBoundaryAtOrBefore(
            text: line,
            index: Math.Clamp(value: col, min: row.Start, max: row.End)
        );
        if (row.FastGrid) return (col - row.Start) * _charWidth;
        string segment = line[row.Start..row.End];
        var layout = LayoutFor(segment);
        if (layout is not null &&
            layout.TryGetCaretPosition(
                textOffset: col - row.Start,
                position: out var position,
                height: out _
            ))
            return position.X;
        return TextMeasure.Width(
            text: line[row.Start..col],
            fontSize: _fontSize,
            fontFamily: "code"
        );
    }

    private int XToCol(VisualRow row, float localX)
    {
        string line = _lines[row.Line];
        if (localX <= 0f || row.Start == row.End) return row.Start;
        if (row.FastGrid)
        {
            return Math.Clamp(
                value: row.Start + (int)MathF.Round(localX / _charWidth),
                min: row.Start,
                max: row.End
            );
        }

        string segment = line[row.Start..row.End];
        var layout = LayoutFor(segment);
        if (layout is not null) return row.Start + layout.HitTest(localX);

        int[] boundaries = TextNavigation.GraphemeBoundaries(segment);
        float previousWidth = 0f;
        for (int i = 1; i < boundaries.Length; i++)
        {
            float width = TextMeasure.Width(
                text: segment[..boundaries[i]],
                fontSize: _fontSize,
                fontFamily: "code"
            );
            if (localX < (previousWidth + width) / 2f) return row.Start + boundaries[i - 1];
            previousWidth = width;
        }

        return row.End;
    }

    private void RebuildVisualRows(float availableWidth)
    {
        _visualRows.Clear();
        float wrapWidth = MathF.Max(x: _charWidth, y: availableWidth);
        // Soft-wrap results are memoized by line CONTENT (+ wrap inputs): a keystroke re-wraps
        // the edited lines only, instead of re-measuring every grapheme of the whole document.
        if (_wrapCache.Count > (_lines.Count * 2) + 64) _wrapCache.Clear();
        for (int lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
        {
            string line = _lines[lineIndex];
            bool fastGrid = IsFastGridText(line);
            if (!SoftWrap || line.Length == 0)
            {
                // Unwrapped mode only needs a conservative scrollbar extent here. Precise caret and
                // hit geometry comes from the HarfBuzz layout on demand for visible rows.
                float width = line.Length * _charWidth;
                _visualRows.Add(
                    new VisualRow(
                        Line: lineIndex,
                        Start: 0,
                        End: line.Length,
                        Width: width,
                        FastGrid: fastGrid
                    )
                );
                continue;
            }

            if (_wrapCache.TryGetValue(key: line, value: out var cachedWrap) &&
                cachedWrap.WrapWidth == wrapWidth && cachedWrap.CharWidth == _charWidth)
            {
                foreach ((int cs, int ce, float cw) in cachedWrap.Rows)
                {
                    _visualRows.Add(
                        new VisualRow(
                            Line: lineIndex,
                            Start: cs,
                            End: ce,
                            Width: cw,
                            FastGrid: fastGrid
                        )
                    );
                }

                continue;
            }

            int wrapRowsStart = _visualRows.Count;
            int[] boundaries = TextNavigation.GraphemeBoundaries(line);
            float[] prefixWidths = new float[boundaries.Length];
            for (int i = 1; i < boundaries.Length; i++)
            {
                prefixWidths[i] = prefixWidths[i - 1] +
                                  GraphemeAdvance(
                                      text: line,
                                      start: boundaries[i - 1],
                                      end: boundaries[i]
                                  );
            }

            int startBoundary = 0;
            while (startBoundary < boundaries.Length - 1)
            {
                int endBoundary = startBoundary + 1;
                int accepted = endBoundary;
                int lastBreak = -1;
                while (endBoundary < boundaries.Length)
                {
                    float candidateWidth = prefixWidths[endBoundary] - prefixWidths[startBoundary];
                    if (candidateWidth > wrapWidth && endBoundary > startBoundary + 1) break;
                    accepted = endBoundary;
                    if (char.IsWhiteSpace(line[boundaries[endBoundary - 1]]))
                        lastBreak = endBoundary;
                    endBoundary++;
                }

                if (endBoundary < boundaries.Length && lastBreak > startBoundary)
                    accepted = lastBreak;

                int rowStart = boundaries[startBoundary];
                int rowEnd = boundaries[accepted];
                float width = prefixWidths[accepted] - prefixWidths[startBoundary];
                _visualRows.Add(
                    new VisualRow(
                        Line: lineIndex,
                        Start: rowStart,
                        End: rowEnd,
                        Width: width,
                        FastGrid: fastGrid
                    )
                );
                startBoundary = accepted;
            }

            // Store this line's freshly computed rows (line-index-free) for the next rebuild.
            var rows = new (int Start, int End, float Width)[_visualRows.Count - wrapRowsStart];
            for (int r = 0; r < rows.Length; r++)
            {
                var vr = _visualRows[wrapRowsStart + r];
                rows[r] = (vr.Start, vr.End, vr.Width);
            }

            _wrapCache[line] = new CachedWrap(
                WrapWidth: wrapWidth,
                CharWidth: _charWidth,
                Rows: rows
            );
        }

        if (_visualRows.Count == 0)
        {
            _visualRows.Add(
                new VisualRow(
                    Line: 0,
                    Start: 0,
                    End: 0,
                    Width: 0f,
                    FastGrid: true
                )
            );
        }
    }

    private static bool IsFastGridText(string text)
    {
        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
                return false;
        }

        return true;
    }

    private float GraphemeAdvance(string text, int start, int end)
    {
        // Iosevka's ASCII repertoire is a true fixed grid. Avoid shaping and allocating the common
        // code path; non-ASCII graphemes still go through HarfBuzz so CJK, combining text and emoji
        // receive their actual advance.
        if (end == start + 1)
        {
            char c = text[start];
            if (c == '\t') return _charWidth * TabWidth;
            if (c <= 0x7f) return _charWidth;
        }

        return TextMeasure.Width(text: text[start..end], fontSize: _fontSize, fontFamily: "code");
    }

    private void EnsureCaretVisible()
    {
        if (Bounds.Width <= 0f || _lineHeight <= 0f) return;

        float innerW = Bounds.Width - _gutterWidth - (Pad * 2f);
        float innerH = Bounds.Height - (Pad * 2f);

        int caretRow = RowForCaret(line: _caretLine, col: _caretCol);
        float caretX = ColToX(line: _caretLine, col: _caretCol);
        if (caretX - _targetX > innerW - 2f) _targetX = caretX - innerW + 2f;
        else if (caretX - _targetX < 0f) _targetX = caretX;

        float caretY = caretRow * _lineHeight;
        if (caretY - _targetY > innerH - _lineHeight) _targetY = caretY - innerH + _lineHeight;
        else if (caretY - _targetY < 0f) _targetY = caretY;

        ClampTargets();
        SnapScroll(); // caret-follow is instant, not animated
    }

    // ── Smooth scrolling ──────────────────────────────────────────────────────

    private void ClampTargets()
    {
        if (_lineHeight <= 0f) return;
        float innerW = Bounds.Width - _gutterWidth - (Pad * 2f);
        float innerH = Bounds.Height - (Pad * 2f);
        _targetX = Math.Clamp(
            value: _targetX,
            min: 0f,
            max: Math.Max(val1: 0f, val2: _contentWidth + (_charWidth * 2f) - innerW)
        );
        float total = _visualRows.Count * _lineHeight;
        _targetY = Math.Clamp(
            value: _targetY,
            min: 0f,
            max: Math.Max(val1: 0f, val2: total - innerH)
        );
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
        float k = 1f - MathF.Exp(-dt * ScrollEase); // frame-rate independent ease
        _scrollX += (_targetX - _scrollX) * k;
        _scrollY += (_targetY - _scrollY) * k;
        if (App.DebugScrollLog)
        {
            Console.Error.WriteLine(
                FormattableString.Invariant(
                    $"[ce.tick] dt={dt:F4} scroll=({_scrollX:F1},{_scrollY:F1}) target=({_targetX:F1},{_targetY:F1})"
                )
            );
        }

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
        float innerH = Bounds.Height - (Pad * 2f);
        float total = _visualRows.Count * _lineHeight;
        float max = MathF.Max(x: 0f, y: total - innerH);
        if (max <= 0f || total <= 0f) return (0f, 0f, 0f, 0f, false);
        float len = Bounds.Height - 4f;
        float thumb = MathF.Max(x: 24f, y: len * (innerH / total));
        return (Bounds.Y + 2f, len, thumb, max, true);
    }

    private (float Start, float Len, float Thumb, float Max, bool On) HBar()
    {
        if (SoftWrap) return (0f, 0f, 0f, 0f, false);
        float innerW = Bounds.Width - _gutterWidth - (Pad * 2f);
        float extent = _contentWidth + (_charWidth * 2f);
        float max = MathF.Max(x: 0f, y: extent - innerW);
        if (max <= 0f || extent <= 0f) return (0f, 0f, 0f, 0f, false);
        float len = Bounds.Width - _gutterWidth - 4f;
        float thumb = MathF.Max(x: 24f, y: len * (innerW / extent));
        return (Bounds.X + _gutterWidth + 2f, len, thumb, max, true);
    }

    private void DragVBar(float pointerY)
    {
        var bar = VBar();
        if (!bar.On) return;
        float travel = bar.Len - bar.Thumb;
        float frac = travel > 0f ? (pointerY - bar.Start - _barGrab) / travel : 0f;
        _targetY = Math.Clamp(value: frac, min: 0f, max: 1f) * bar.Max;
        SnapScroll();
        MarkNeedsPaint();
    }

    private void DragHBar(float pointerX)
    {
        var bar = HBar();
        if (!bar.On) return;
        float travel = bar.Len - bar.Thumb;
        float frac = travel > 0f ? (pointerX - bar.Start - _barGrab) / travel : 0f;
        _targetX = Math.Clamp(value: frac, min: 0f, max: 1f) * bar.Max;
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
            value1: _lines.Count,
            value2: _caretLine,
            value3: _caretCol,
            value4: _anchorLine,
            value5: _anchorCol,
            value6: Focused,
            value7: (int)(_scrollY * 0.1f),
            value8: _barDrag
        );
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
        _charWidth = TextMeasure.Width(text: "0", fontSize: _fontSize, fontFamily: "code");

        // Gutter sized to the widest line number; digits are monospace, so width = digits × advance.
        int digits = Math.Max(val1: 2, val2: _lines.Count.ToString().Length);
        _gutterWidth = GutterPadLeft + (digits * _charWidth) + GutterPadRight;

        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        float availableWidth = MathF.Max(x: _charWidth, y: _size.Width - _gutterWidth - (Pad * 2f));
        RebuildVisualRows(availableWidth);
        _contentWidth = 0f;
        foreach (var row in _visualRows) _contentWidth = MathF.Max(x: _contentWidth, y: row.Width);
        if (SoftWrap) _scrollX = _targetX = 0f;
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

    private Color ColorFor(TokenKind kind)
    {
        return kind switch {
            // The GNOME source-view scheme, drawn from the Adwaita accent hues in their standalone
            // (readable-as-text) variants so it holds in both appearances: orange keywords, teal
            // types, green strings, purple literals, and comments simply dimmed.
            TokenKind.Keyword => AdwAccentColors.Standalone(
                accent: AdwAccent.Orange,
                dark: _theme.IsDark
            ),
            TokenKind.Type => AdwAccentColors.Standalone(
                accent: AdwAccent.Teal,
                dark: _theme.IsDark
            ),
            TokenKind.String => AdwAccentColors.Standalone(
                accent: AdwAccent.Green,
                dark: _theme.IsDark
            ),
            TokenKind.Number => AdwAccentColors.Standalone(
                accent: AdwAccent.Purple,
                dark: _theme.IsDark
            ),
            TokenKind.Comment => _theme.OnSurface.WithAlpha(AdwStyle.DimOpacity),
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
        target = Math.Min(val1: target, val2: _lines.Count - 1);
        if (_lineStates.Count == 0)
            _lineStates.Add(ILineTokenizer.StateDefault); // entering state of line 0
        while (_lineStates.Count <= target)
        {
            int l = _lineStates.Count - 1; // last line whose entering state is known
            int entering = _lineStates[l];
            int exiting;
            if (_tokenCache.TryGetValue(key: l, value: out var cached) &&
                cached.EnteringState == entering &&
                ReferenceEquals(objA: cached.SourceText, objB: _lines[l]))
                exiting = cached.ExitingState;
            else
            {
                _tokenBuffer.Clear();
                exiting = tokenizer.Tokenize(
                    line: _lines[l],
                    state: entering,
                    output: _tokenBuffer
                );
                _tokenCache[l] = CacheLineTokens(line: l, entering: entering, exiting: exiting);
            }

            _lineStates.Add(exiting);
        }

        return _lineStates[target];
    }

    private CachedLineTokens TokensForLine(ILineTokenizer tokenizer, int line)
    {
        int entering = EnsureLineStates(tokenizer: tokenizer, target: line);
        if (_tokenCache.TryGetValue(key: line, value: out var cached) &&
            cached.EnteringState == entering &&
            ReferenceEquals(objA: cached.SourceText, objB: _lines[line]))
            return cached;

        _tokenBuffer.Clear();
        int exiting = tokenizer.Tokenize(line: _lines[line], state: entering, output: _tokenBuffer);
        cached = CacheLineTokens(line: line, entering: entering, exiting: exiting);
        _tokenCache[line] = cached;
        return cached;
    }

    private CachedLineTokens CacheLineTokens(int line, int entering, int exiting)
    {
        var tokens = _tokenBuffer.ToArray();
        var runs = new List<TokenRun>();
        string text = _lines[line];
        foreach (var token in tokens)
        {
            if (token.Length <= 0 || token.Start >= text.Length) continue;
            int length = Math.Min(val1: token.Length, val2: text.Length - token.Start);
            runs.Add(
                new TokenRun(
                    Token: token,
                    Text: text.Substring(startIndex: token.Start, length: length)
                )
            );
        }

        return new CachedLineTokens(
            EnteringState: entering,
            ExitingState: exiting,
            SourceText: text,
            Tokens: tokens,
            Runs: runs.ToArray()
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
        {
            paint.AddTextLayout(
                handle: layout.Handle,
                x: x,
                y: baseline,
                color: color
            );
        }
        else
        {
            paint.AddText(
                text: s,
                baselineX: x,
                baselineY: baseline,
                color: color,
                fontSize: _fontSize,
                lineHeight: Typography.Code.LineHeight,
                fontFamily: "code"
            );
        }
    }

    /// Resolve (creating + caching on first use) the native layout for a single-line code-face
    /// segment, or null when layouts are unavailable so the caller falls back to AddText.
    private TextLayout? LayoutFor(string s)
    {
        if (_layoutsUnavailable) return null;
        // Native text layouts belong to the MAIN window's glyph atlas — in a secondary OS window
        // they resolve against the wrong GpuUi (nothing renders). AddText fallback there.
        if (Owner is { NativeWindow: not null }) return null;
        if (_layouts.TryGetValue(key: s, value: out var existing)) return existing;

        var engine = ZigoteEngine.Instance;
        if (engine is null || engine.Handle == 0)
            return null; // headless: fall back without caching

        TextLayout layout;
        try
        {
            // Single-line segments never contain '\n', so lineHeight is immaterial here.
            layout = engine.CreateTextLayout(
                text: s,
                fontSize: _fontSize,
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

        // Evict a quarter instead of flushing everything: a full clear at the cap re-shaped every
        // visible line on the frame that crossed the threshold — a one-frame hitch mid-typing.
        if (_layouts.Count >= LayoutCacheCap) EvictLayouts(LayoutCacheCap / 4);
        _layouts[s] = layout;
        return layout;
    }

    private void EvictLayouts(int count)
    {
        _layoutEvictScratch ??= new List<string>(count);
        _layoutEvictScratch.Clear();
        foreach (string key in _layouts.Keys)
        {
            _layoutEvictScratch.Add(key);
            if (_layoutEvictScratch.Count >= count) break;
        }

        foreach (string key in _layoutEvictScratch)
        {
            _layouts[key].Dispose();
            _layouts.Remove(key);
        }
    }

    private void ClearLayouts()
    {
        foreach (var l in _layouts.Values) l.Dispose();
        _layouts.Clear();
    }

    public override void Paint(PaintList paint)
    {
        // A source view is a `.view`: the view background edge to edge, with no frame or radius of
        // its own (whatever holds it — a card, a pane — supplies those). The gutter shares that
        // background and is set apart only by its dimmed numerals, as in GNOME Text Editor.
        paint.AddRect(bounds: Bounds, color: AdwPalette.For(_theme).ViewBg);
        var gutterRect = new Rect(
            x: Bounds.X,
            y: Bounds.Y,
            width: _gutterWidth,
            height: Bounds.Height
        );

        float innerH = Bounds.Height - (Pad * 2f);

        // Visible visual-row window (a physical line may occupy several rows when soft-wrapped).
        int first = Math.Max(val1: 0, val2: (int)(_scrollY / _lineHeight));
        int last = Math.Min(
            val1: _visualRows.Count - 1,
            val2: (int)((_scrollY + innerH) / _lineHeight) + 1
        );

        // Carried multi-line lexer state entering the first visible line — read from the cross-frame
        // cache (rebuilt lazily, cleared on edit) instead of re-lexing from line 0 every frame.
        var tokenizer = Tokenizer;
        // ── Selection highlight (clipped to the text area) ────────────────────
        paint.AddClipStart(
            new Rect(
                x: Bounds.X + _gutterWidth,
                y: Bounds.Y,
                width: Bounds.Width - _gutterWidth,
                height: Bounds.Height
            )
        );

        int firstLine = first <= last ? _visualRows[first].Line : 0;
        int lastLine = first <= last ? _visualRows[last].Line : -1;

        // Tint columns [c0, c1) of one visual row.
        void Band(int visual, int c0, int c1, Color color, bool newline = false)
        {
            var row = _visualRows[visual];
            float x0 = TextLeft - _scrollX + ColToX(row: row, col: c0);
            float x1 = TextLeft - _scrollX + ColToX(row: row, col: c1);
            // Trailing newline selected lines get a small caret-width sliver.
            if (newline) x1 += _fontSize * 0.4f;
            paint.AddRect(
                bounds: new Rect(
                    x: MathF.Min(x: x0, y: x1),
                    y: TextTop - _scrollY + (visual * _lineHeight),
                    width: MathF.Max(x: 2f, y: MathF.Abs(x1 - x0)),
                    height: _lineHeight
                ),
                color: color
            );
        }

        bool lone = Focused && !HasSelection && _extra.Count == 0;

        // Current line: a faint band under the caret's row while nothing is selected.
        if (lone && _find is null && _caretLine >= firstLine && _caretLine <= lastLine)
        {
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X + _gutterWidth,
                    y: TextTop - _scrollY +
                       (RowForCaret(line: _caretLine, col: _caretCol) * _lineHeight),
                    width: Bounds.Width - _gutterWidth,
                    height: _lineHeight
                ),
                color: _theme.OnSurface.WithAlpha(0.04f)
            );
        }

        // Other occurrences of the find query, or of a single-line selection.
        {
            var (s, e) = OrderedSelection();
            var needle = !string.IsNullOrEmpty(_find)
                ? _find
                : HasSelection && s.Line == e.Line
                    ? _lines[s.Line].AsSpan()[s.Col..e.Col]
                    : default;
            var cmp = !string.IsNullOrEmpty(_find) ? FindComparison : StringComparison.Ordinal;
            if (!needle.IsWhiteSpace())
            {
                for (int visual = first; visual <= last; visual++)
                {
                    var row = _visualRows[visual];
                    string text = _lines[row.Line];
                    for (int i = NextMatch(
                             s: text,
                             needle: needle,
                             from: 0,
                             wholeWord: false,
                             cmp: cmp
                         );
                         i >= 0 && i < row.End;
                         i = NextMatch(
                             s: text,
                             needle: needle,
                             from: i + needle.Length,
                             wholeWord: false,
                             cmp: cmp
                         ))
                    {
                        int c0 = Math.Max(val1: row.Start, val2: i);
                        int c1 = Math.Min(val1: row.End, val2: i + needle.Length);
                        if (c1 > c0)
                        {
                            Band(
                                visual: visual,
                                c0: c0,
                                c1: c1,
                                color: _theme.OnSurface.WithAlpha(0.12f)
                            );
                        }
                    }
                }
            }
        }

        // The bracket at the caret and its partner.
        if (lone && TryMatchBracket(
                line: _caretLine,
                col: _caretCol,
                bracket: out var bracket,
                match: out var partner
            ))
        {
            foreach ((int bl, int bc) in (ReadOnlySpan<(int, int)>)[bracket, partner])
            {
                if (bl < firstLine || bl > lastLine) continue;
                Band(
                    visual: RowForCaret(line: bl, col: bc),
                    c0: bc,
                    c1: bc + 1,
                    color: _theme.OnSurface.WithAlpha(0.18f)
                );
            }
        }

        for (int n = -1; n < _extra.Count; n++)
        {
            var (s, e) = Range(n < 0 ? Primary : _extra[n]);
            if (s == e || e.Line < firstLine || s.Line > lastLine) continue;
            for (int visual = first; visual <= last; visual++)
            {
                var row = _visualRows[visual];
                if (row.Line < s.Line || row.Line > e.Line) continue;
                int c0 = Math.Max(val1: row.Start, val2: row.Line == s.Line ? s.Col : row.Start);
                int c1 = Math.Min(val1: row.End, val2: row.Line == e.Line ? e.Col : row.End);
                if (c1 < c0) continue;
                Band(
                    visual: visual,
                    c0: c0,
                    c1: c1,
                    color: _theme.Accent.WithAlpha(0.3f),
                    newline: row.Line < e.Line && row.End == _lines[row.Line].Length
                );
            }
        }

        // ── Text per visible row ──────────────────────────────────────────────
        for (int visual = first; visual <= last; visual++)
        {
            var row = _visualRows[visual];
            int l = row.Line;
            string line = _lines[l];
            var cachedTokens = tokenizer is not null
                ? TokensForLine(tokenizer: tokenizer, line: l)
                : default;
            var tokens = cachedTokens.Tokens ?? [];
            float y = TextTop - _scrollY + (visual * _lineHeight);
            float baseline = y + (_fontSize * 0.8f);

            if (row.Start == row.End) continue;

            if (tokens.Length == 0)
            {
                DrawSegment(
                    paint: paint,
                    s: line[row.Start..row.End],
                    x: TextLeft - _scrollX,
                    baseline: baseline,
                    color: _theme.OnSurface
                );
                continue;
            }

            // Each token draws exactly once at its monospace column — never as an overpaint on top
            // of a base-color row, which double-blends antialiased glyph edges into bold, fringed
            // text. Runs are cached by content, so keywords/identifiers reuse shared layouts.
            foreach (var run in cachedTokens.Runs ?? [])
            {
                var tok = run.Token;
                int tokenEnd = Math.Min(val1: line.Length, val2: tok.Start + tok.Length);
                int start = Math.Max(val1: row.Start, val2: tok.Start);
                int end = Math.Min(val1: row.End, val2: tokenEnd);
                if (end <= start) continue;
                string slice = start == tok.Start && end == tokenEnd
                    ? run.Text
                    : line[start..end];
                if (slice.Length == 0) continue;
                float x = TextLeft - _scrollX + ColToX(row: row, col: start);
                DrawSegment(
                    paint: paint,
                    s: slice,
                    x: x,
                    baseline: baseline,
                    color: ColorFor(tok.Kind)
                );
            }
        }

        // ── Caret ──────────────────────────────────────────────────────────────
        if (Focused && !ReadOnly)
        {
            float time = App.Active?.Time ?? 0f;
            if (CaretBlink.Shown(time: time, blinkBase: 0f) || _isDragging)
            {
                int caretRow = RowForCaret(line: _caretLine, col: _caretCol);
                float cx = TextLeft - _scrollX + ColToX(line: _caretLine, col: _caretCol);
                float cy = TextTop - _scrollY + (caretRow * _lineHeight);
                if (_compositionText.Length > 0)
                {
                    DrawSegment(
                        paint: paint,
                        s: _compositionText,
                        x: cx,
                        baseline: cy + (_fontSize * 0.8f),
                        color: _theme.OnSurface
                    );
                    float compositionWidth = TextMeasure.Width(
                        text: _compositionText,
                        fontSize: _fontSize,
                        fontFamily: "code"
                    );
                    paint.AddRect(
                        bounds: new Rect(
                            x: cx,
                            y: cy + _lineHeight - 2f,
                            width: MathF.Max(x: 1f, y: compositionWidth),
                            height: 1f
                        ),
                        color: _theme.Primary
                    );
                    int compositionCaret = TextNavigation.GraphemeBoundaryAtOrBefore(
                        text: _compositionText,
                        index: _compositionSelectionStart + _compositionSelectionLength
                    );
                    cx += TextMeasure.Width(
                        text: _compositionText[..compositionCaret],
                        fontSize: _fontSize,
                        fontFamily: "code"
                    );
                }

                paint.AddRect(
                    bounds: new Rect(
                        x: cx,
                        y: cy,
                        width: 1.5f,
                        height: _lineHeight
                    ),
                    color: _theme.Primary
                );

                foreach (var x in _extra)
                {
                    if (x.CaretLine < firstLine || x.CaretLine > lastLine) continue;
                    paint.AddRect(
                        bounds: new Rect(
                            x: TextLeft - _scrollX + ColToX(line: x.CaretLine, col: x.CaretCol),
                            y: TextTop - _scrollY +
                               (RowForCaret(line: x.CaretLine, col: x.CaretCol) * _lineHeight),
                            width: 1.5f,
                            height: _lineHeight
                        ),
                        color: _theme.Primary
                    );
                }
            }

            int imeRow = RowForCaret(line: _caretLine, col: _caretCol);
            float imeX = TextLeft - _scrollX + ColToX(line: _caretLine, col: _caretCol);
            float imeY = TextTop - _scrollY + (imeRow * _lineHeight);
            if (_compositionText.Length > 0)
            {
                int compositionCaret = TextNavigation.GraphemeBoundaryAtOrBefore(
                    text: _compositionText,
                    index: _compositionSelectionStart + _compositionSelectionLength
                );
                imeX += TextMeasure.Width(
                    text: _compositionText[..compositionCaret],
                    fontSize: _fontSize,
                    fontFamily: "code"
                );
            }

            ZigoteEngine.Instance?.SetTextInputArea(
                new Rect(
                    x: imeX,
                    y: imeY,
                    width: 1.5f,
                    height: _lineHeight
                )
            );
        }

        paint.AddClipEnd();

        // ── Line numbers ────────────────────────────────────────────────────────
        paint.AddClipStart(gutterRect);
        for (int visual = first; visual <= last; visual++)
        {
            var row = _visualRows[visual];
            if (row.Start != 0) continue;
            int l = row.Line;
            string num = LineNo(l);
            float w = num.Length * _charWidth; // monospace
            float nx = Bounds.X + _gutterWidth - GutterPadRight - w;
            float ny = TextTop - _scrollY + (visual * _lineHeight) + (_fontSize * 0.8f);
            var col = l == _caretLine
                ? _theme.OnSurface
                : _theme.OnSurface.WithAlpha(AdwStyle.DimOpacity * 0.7f);
            DrawSegment(
                paint: paint,
                s: num,
                x: nx,
                baseline: ny,
                color: col
            );
        }

        paint.AddClipEnd();

        // ── Scrollbars (draggable; thicken + brighten while grabbed) ──────────────
        var hbar = HBar();
        if (hbar.On)
        {
            float thumbX = hbar.Start + ((hbar.Len - hbar.Thumb) * (_scrollX / hbar.Max));
            float h = _barDrag == 2 ? 4f : 3f;
            paint.AddRect(
                bounds: new Rect(
                    x: thumbX,
                    y: Bounds.Bottom - h - 2f,
                    width: hbar.Thumb,
                    height: h
                ),
                color: _theme.OnSurface.WithAlpha(_barDrag == 2 ? 0.55f : 0.25f),
                radius: h / 2f
            );
        }

        var vbar = VBar();
        if (vbar.On)
        {
            float thumbY = vbar.Start + ((vbar.Len - vbar.Thumb) * (_scrollY / vbar.Max));
            float w = _barDrag == 1 ? 4f : 3f;
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.Right - w - 2f,
                    y: thumbY,
                    width: w,
                    height: vbar.Thumb
                ),
                color: _theme.OnSurface.WithAlpha(_barDrag == 1 ? 0.55f : 0.25f),
                radius: w / 2f
            );
        }

        if (_find is not null) PaintFindBar(paint);

        // No focus ring: a text view's focus cue is its caret.
    }

    /// <summary>The ⌘F strip, floating at the top right: query, its caret, and "3/17".</summary>
    private void PaintFindBar(PaintList paint)
    {
        string query = _find!;
        string label = FindLabel();
        var dim = _theme.OnSurface.WithAlpha(AdwStyle.DimOpacity);
        float queryW = IsFastGridText(query)
            ? query.Length * _charWidth
            : TextMeasure.Width(text: query, fontSize: _fontSize, fontFamily: "code");
        float w = MathF.Min(
            x: Bounds.Width - _gutterWidth - 24f,
            y: MathF.Max(x: queryW, y: _charWidth * 16f) + ((label.Length + 3) * _charWidth) +
               (Pad * 2f)
        );
        var box = new Rect(
            x: Bounds.Right - w - 16f,
            y: Bounds.Y + 6f,
            width: w,
            height: _lineHeight + Pad
        );
        // A hairline ring, then the fill — popover and view backgrounds are the same white in light.
        paint.AddRect(
            bounds: new Rect(
                x: box.X - 1f,
                y: box.Y - 1f,
                width: box.Width + 2f,
                height: box.Height + 2f
            ),
            color: _theme.OnSurface.WithAlpha(0.2f),
            radius: 7f
        );
        paint.AddRect(bounds: box, color: AdwPalette.For(_theme).PopoverBg, radius: 6f);

        paint.AddClipStart(box);
        float x = box.X + Pad;
        float baseline = box.Y + (Pad / 2f) + (_fontSize * 0.8f);
        DrawSegment(
            paint: paint,
            s: query.Length > 0 ? query : "Find",
            x: x,
            baseline: baseline,
            color: query.Length > 0 ? _theme.OnSurface : dim
        );
        if (CaretBlink.Shown(time: App.Active?.Time ?? 0f, blinkBase: 0f))
        {
            paint.AddRect(
                bounds: new Rect(
                    x: x + queryW,
                    y: box.Y + (Pad / 2f),
                    width: 1.5f,
                    height: _lineHeight
                ),
                color: _theme.Primary
            );
        }

        if (label.Length > 0)
        {
            DrawSegment(
                paint: paint,
                s: label,
                x: box.Right - Pad - (label.Length * _charWidth),
                baseline: baseline,
                color: dim
            );
        }

        paint.AddClipEnd();
    }

    /// <summary>
    ///     The bracket touching the caret (the one after it first, then the one before) and the
    ///     bracket that pairs with it.
    /// </summary>
    private bool TryMatchBracket(
        int line,
        int col,
        out (int Line, int Col) bracket,
        out (int Line, int Col) match)
    {
        bracket = match = default;
        string s = _lines[line];
        for (int c = col; c >= col - 1 && c >= 0; c--)
        {
            if (c >= s.Length) continue;
            int open = "([{".IndexOf(s[c]);
            int close = ")]}".IndexOf(s[c]);
            if (open < 0 && close < 0) continue;
            char self = s[c];
            char other = open >= 0 ? ")]}"[open] : "([{"[close];
            int dir = open >= 0 ? 1 : -1;
            int depth = 0;
            // ponytail: counts brackets inside strings and comments too; skip String/Comment
            // tokens from _tokenCache if that ever mis-pairs in practice.
            for (int l = line, n = 0;
                 l >= 0 && l < _lines.Count && n < BracketScanLines;
                 l += dir, n++)
            {
                string t = _lines[l];
                for (int i = l == line ? c : dir > 0 ? 0 : t.Length - 1;
                     i >= 0 && i < t.Length;
                     i += dir)
                {
                    if (t[i] == self) depth++;
                    else if (t[i] == other && --depth == 0)
                    {
                        bracket = (line, c);
                        match = (l, i);
                        return true;
                    }
                }
            }

            return false;
        }

        return false;
    }

    // ── Pointer input ─────────────────────────────────────────────────────────

    private (int Line, int Col) HitPosition(Offset point)
    {
        int visual = (int)((point.Y - (TextTop - _scrollY)) / _lineHeight);
        visual = Math.Clamp(value: visual, min: 0, max: _visualRows.Count - 1);
        var row = _visualRows[visual];
        float localX = point.X - (TextLeft - _scrollX);
        int col = XToCol(row: row, localX: localX);
        return (row.Line, col);
    }

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);

        // Scrollbar thumbs take priority over caret placement (drag to scroll).
        var vbar = VBar();
        if (vbar.On && point.X >= Bounds.Right - BarHitWidth)
        {
            float thumbY = vbar.Start + ((vbar.Len - vbar.Thumb) * (_scrollY / vbar.Max));
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
            float thumbX = hbar.Start + ((hbar.Len - hbar.Thumb) * (_scrollX / hbar.Max));
            _barGrab = point.X >= thumbX && point.X <= thumbX + hbar.Thumb
                ? point.X - thumbX
                : hbar.Thumb / 2f;
            _barDrag = 2;
            DragHBar(point.X);
            return;
        }

        (int l, int col) = HitPosition(point);
        var mods = App.Active?.CurrentModifiers ?? Modifiers.None;
        ResetStickyColumn();
        _occWholeWord = false;

        // Double-click selects the word under the cursor; a third click, the whole line.
        float now = App.Active?.Time ?? 0f;
        bool repeat = now - _lastClickTime < DoubleClickSeconds && l == _lastClickLine &&
                      Math.Abs(col - _lastClickCol) <= 1;
        _clickCount = repeat ? _clickCount + 1 : 1;
        _lastClickTime = now;
        _lastClickLine = l;
        _lastClickCol = col;
        if (_clickCount == 2)
        {
            SelectWordAt(line: l, col: col);
            return;
        }

        if (_clickCount == 3)
        {
            _clickCount = 0; // a fourth click starts over
            _isDragging = false;
            _caretLine = _anchorLine = l;
            _anchorCol = 0;
            _caretCol = _lines[l].Length;
            MarkNeedsPaint();
            return;
        }

        // Alt+click plants an additional cursor; a plain click goes back to one.
        if (mods.HasFlag(Modifiers.Alt)) _extra.Add(Primary);
        else _extra.Clear();

        // Shift+click extends the selection from where it was anchored.
        if (mods.HasFlag(Modifiers.Shift) && !mods.HasFlag(Modifiers.Alt))
        {
            if (_anchorLine < 0) (_anchorLine, _anchorCol) = (_caretLine, _caretCol);
        }
        else
            (_anchorLine, _anchorCol) = (l, col);

        _caretLine = l;
        _caretCol = col;
        MergeCarets();
        _isDragging = true;
        MarkNeedsPaint();
    }

    /// <summary>Select the word (or single non-word char) at the given position.</summary>
    private void SelectWordAt(int line, int col)
    {
        string s = _lines[Math.Clamp(value: line, min: 0, max: _lines.Count - 1)];
        (int start, int end) = TextNavigation.WordAt(text: s, pos: col);
        col = Math.Clamp(value: col, min: 0, max: s.Length);

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
        (int l, int col) = HitPosition(point);
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
        bool shift = App.Active?.CurrentModifiers.HasFlag(Modifiers.Shift) ?? false;
        if (shift && dx == 0f)
        {
            dx = dy;
            dy = 0f;
        }

        _targetY -= dy * _lineHeight * 3f;
        _targetX -= dx * _charWidth * 6f;
        ClampTargets();
        if (App.DebugScrollLog)
        {
            Console.Error.WriteLine(
                FormattableString.Invariant(
                    $"[ce.scroll] d=({dx:F3},{dy:F3}) target=({_targetX:F1},{_targetY:F1}) scroll=({_scrollX:F1},{_scrollY:F1}) rows={_visualRows.Count} lineH={_lineHeight:F1} bounds={Bounds.Height:F0}"
                )
            );
        }

        AnimateScroll(); // ease the rendered offset toward the new target
    }

    // Touch scrolling. Without these the editor is wheel-only: a finger drag inside it runs
    // OnPointerMove (text selection) and the document never moves. Deltas are already in logical
    // px — no wheel-tick conversion — and the drag tracks the finger 1:1.
    public override bool CanTouchScroll(bool vertical) => vertical ? VBar().On : HBar().On;

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
            new ContextMenuItem(
                Label: "Cut",
                OnSelect: HasSelection && !ReadOnly ? CutAction : null,
                Shortcut: "⌘X"
            )
        );
        menu.Items.Add(
            new ContextMenuItem(
                Label: "Copy",
                OnSelect: HasSelection ? CopyAction : null,
                Shortcut: "⌘C"
            )
        );
        menu.Items.Add(
            new ContextMenuItem(
                Label: "Paste",
                OnSelect: ReadOnly ? null : PasteAction,
                Shortcut: "⌘V"
            )
        );
        menu.Items.Add(new ContextMenuItem(Label: "", OnSelect: null, Separator: true));
        menu.Items.Add(
            new ContextMenuItem(Label: "Select All", OnSelect: SelectAll, Shortcut: "⌘A")
        );
        menu.ShowAt(point);
    }

    protected override void OnFocusChanged(bool focused) => MarkNeedsPaint();

    // ── Keyboard input ────────────────────────────────────────────────────────

    public override void OnTextInput(string text)
    {
        if (_find is not null)
        {
            _find += text;
            FindStep(forward: true, refine: true);
            return;
        }

        if (ReadOnly) return;
        _compositionText = string.Empty;
        ResetStickyColumn();
        _occWholeWord = false;
        if (text.Length == 1)
        {
            char c = text[0];
            Edit(kind: EditKind.Typing, perCaret: () => TypeChar(c));
        }
        else
            InsertText(text: text, kind: EditKind.Typing);
    }

    /// <summary>
    ///     One typed character at one caret, with the bracket/quote conveniences: wrap a selection,
    ///     auto-close a pair, type over a closer that is already there, and pull a lone <c>}</c> back
    ///     one indent level.
    /// </summary>
    private void TypeChar(char c)
    {
        string line = _lines[_caretLine];
        int open = Openers.IndexOf(c);
        if (HasSelection)
        {
            if (open < 0)
            {
                ReplaceSelection(c.ToString());
                return;
            }

            // Wrap the selection and keep it selected inside the new pair.
            var (s, e) = OrderedSelection();
            bool caretLast = (_caretLine, _caretCol) == e;
            Replace(s: s, e: e, text: c + SelectedText() + Closers[open]);
            (int Line, int Col) innerStart = (s.Line, s.Col + 1);
            (int Line, int Col) innerEnd = (_caretLine, _caretCol - 1);
            (_anchorLine, _anchorCol) = caretLast ? innerStart : innerEnd;
            (_caretLine, _caretCol) = caretLast ? innerEnd : innerStart;
            return;
        }

        var caret = (_caretLine, _caretCol);
        char prev = _caretCol > 0 ? line[_caretCol - 1] : ' ';
        char next = _caretCol < line.Length ? line[_caretCol] : ' ';

        // ponytail: types over ANY matching closer, not only ones we auto-inserted; track
        // inserted closers if stepping over a pre-existing one ever surprises.
        if (next == c && Closers.Contains(c))
        {
            _caretCol++;
            return;
        }

        if (c == '}' && _caretCol >= TabWidth &&
            line.AsSpan(start: 0, length: _caretCol).IsWhiteSpace())
        {
            Replace(s: (_caretLine, _caretCol - TabWidth), e: caret, text: "}");
            return;
        }

        // Auto-close only where the closer can't glue onto following text, and never turn an
        // apostrophe inside a word into a pair.
        bool quote = open >= 3;
        if (open >= 0 && (char.IsWhiteSpace(next) || ")]}".Contains(next)) &&
            !(quote && TextNavigation.IsWordChar(prev)))
        {
            Replace(s: caret, e: caret, text: $"{c}{Closers[open]}");
            _caretCol--;
            return;
        }

        Replace(s: caret, e: caret, text: c.ToString());
    }

    public override void OnTextComposition(string text, int selectionStart, int selectionLength)
    {
        if (ReadOnly) return;
        // IMEs (IBus on GNOME) echo empty preedit updates whenever the text-input rect moves —
        // which happens every frame while a focused editor scrolls, since the rect tracks the
        // caret's on-screen position. An empty→empty update changes nothing and must not
        // EnsureCaretVisible, or wheel scrolling rubber-bands back to the caret.
        if (text.Length == 0 && _compositionText.Length == 0) return;
        _compositionText = text;
        _compositionSelectionStart = Math.Clamp(value: selectionStart, min: 0, max: text.Length);
        _compositionSelectionLength = Math.Clamp(
            value: selectionLength,
            min: 0,
            max: text.Length - _compositionSelectionStart
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
        const uint scPageUp = 75;
        const uint scPageDown = 78;
        const uint scReturn = 40;
        const uint scKpEnter = 88;
        const uint scEscape = 41;
        const uint scTab = 43;
        const uint scF3 = 60;
        const uint scSlash = 56;
        const uint scLeftBracket = 47;
        const uint scRightBracket = 48;
        const uint scBackslash = 49;

        // A modifier going down on its own is not a command (and must not close the find bar on
        // the way to Shift+Enter).
        if (scancode is >= 224 and <= 231) return;

        bool shift = mods.HasFlag(Modifiers.Shift);
        bool alt = mods.HasFlag(Modifiers.Alt);
        bool cmd = mods.HasCommand(); // Ctrl or ⌘
        // ⌘ jumps to line/document edges (macOS); Alt — or Ctrl where ⌘ doesn't exist — moves by word.
        bool meta = mods.HasFlag(Modifiers.Cmd);
        bool word = alt || mods.HasFlag(Modifiers.Ctrl);
        char key = char.ToLowerInvariant(keyChar);
        bool enter = scancode is scReturn or scKpEnter;

        if (scancode is not (scUp or scDown or scPageUp or scPageDown)) ResetStickyColumn();
        if (!(cmd && key == 'd')) _occWholeWord = false;

        // We hold Tab and Escape (IKeyboardTrap), so Escape has to peel back our own state before
        // it finally lets go of focus.
        if (scancode == scEscape)
        {
            if (_find is not null) _find = null;
            else if (_extra.Count > 0) _extra.Clear();
            else if (HasSelection) ClearSelection();
            else App.Active?.ClearFocus();
            MarkNeedsPaint();
            return;
        }

        if (_find is not null)
        {
            if (enter || scancode == scF3 || (cmd && key == 'g'))
            {
                FindStep(forward: !shift);
                return;
            }

            if (scancode == scBackspace)
            {
                _find = _find[..TextNavigation.PreviousGraphemeBoundary(
                    text: _find,
                    index: _find.Length
                )];
                FindStep(forward: true, refine: true);
                return;
            }

            if (cmd && key == 'v')
            {
                string clip = ZigoteEngine.Instance?.GetClipboard() ?? string.Empty;
                _find += clip.Split('\r', '\n')[0];
                FindStep(forward: true, refine: true);
                return;
            }

            // Printable keys are followed by their text input, which is what feeds the query —
            // only commands and navigation leave the bar and act on the document.
            bool leaves = (cmd && key != 'f') || scancode is scLeft or scRight or scUp or scDown
                or scHome or scEnd or scPageUp or scPageDown or scTab or scDelete;
            if (!leaves) return;
            // ⌘⇧L still needs the query, so it closes the bar itself.
            if (!(cmd && shift && key == 'l')) _find = null;
            MarkNeedsPaint();
        }

        if (cmd)
        {
            switch (key)
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
                    if (!ReadOnly) PasteAction();
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
                case 'd':
                    SelectNextOccurrence();
                    return;
                case 'l':
                    if (shift) SelectAllOccurrences();
                    else Navigate(SelectLine);
                    return;
                case 'f':
                    OpenFind();
                    return;
                case 'g':
                    OpenFind();
                    FindStep(forward: !shift);
                    return;
                case 'k' when shift:
                    DeleteLines();
                    return;
            }

            if (key == '/' || scancode == scSlash)
            {
                ToggleLineComment();
                return;
            }

            if (key == ']' || scancode == scRightBracket)
            {
                IndentLines(+1);
                return;
            }

            if (key == '[' || scancode == scLeftBracket)
            {
                IndentLines(-1);
                return;
            }

            if (shift && (key is '\\' or '|' || scancode == scBackslash))
            {
                Navigate(() =>
                    {
                        if (!TryMatchBracket(
                                line: _caretLine,
                                col: _caretCol,
                                bracket: out _,
                                match: out var m
                            )) return;
                        ClearSelection();
                        (_caretLine, _caretCol) = m;
                    }
                );
                return;
            }

            if (enter)
            {
                Edit(kind: EditKind.Other, perCaret: shift ? OpenLineAbove : OpenLineBelow);
                return;
            }
        }

        if (scancode == scF3)
        {
            OpenFind();
            FindStep(forward: !shift);
            return;
        }

        switch (scancode)
        {
            case scLeft:
            case scRight:
            {
                int dir = scancode == scLeft ? -1 : +1;
                if (meta) Navigate(() => MoveLineEdge(extend: shift, dir: dir));
                else if (word) Navigate(() => MoveWord(extend: shift, dir: dir));
                else Navigate(() => MoveCaret(extend: shift, dCol: dir));
                return;
            }

            case scUp:
            case scDown:
            {
                int dir = scancode == scUp ? -1 : +1;
                if (cmd && alt) AddCaretVertical(dir);
                else if (alt && shift) DuplicateLines(up: dir < 0);
                else if (alt) MoveLines(dir);
                else if (meta) Navigate(() => MoveDocumentEdge(extend: shift, dir: dir));
                else Navigate(() => MoveCaretVertical(extend: shift, dLine: dir));
                return;
            }

            case scPageUp:
            case scPageDown:
            {
                int page = Math.Max(
                    val1: 1,
                    val2: (int)((Bounds.Height - (Pad * 2f)) / MathF.Max(x: 1f, y: _lineHeight)) - 1
                );
                int rows = scancode == scPageUp ? -page : page;
                Navigate(() => MoveCaretVertical(extend: shift, dLine: rows));
                return;
            }

            case scHome:
            case scEnd:
            {
                int dir = scancode == scHome ? -1 : +1;
                if (cmd) Navigate(() => MoveDocumentEdge(extend: shift, dir: dir));
                else Navigate(() => MoveLineEdge(extend: shift, dir: dir));
                return;
            }
        }

        if (ReadOnly)
        {
            // Nothing to indent in a viewer: hand Tab back to focus traversal.
            if (scancode == scTab) App.Active?.MoveFocusByTab(shift);
            return;
        }

        if (enter)
        {
            Edit(kind: EditKind.Other, perCaret: NewLine);
            return;
        }

        switch (scancode)
        {
            case scTab:
                if (shift) IndentLines(-1);
                else if (SpansLines()) IndentLines(+1);
                else
                {
                    // To the next tab stop, not a fixed four.
                    Edit(
                        kind: EditKind.Other,
                        perCaret: () =>
                        {
                            DeleteSelection();
                            ReplaceSelection(
                                new string(c: ' ', count: TabWidth - (_caretCol % TabWidth))
                            );
                        }
                    );
                }

                return;

            case scBackspace:
                Edit(
                    kind: EditKind.Deleting,
                    perCaret: () => DeleteBackward(word: word, toLineStart: meta)
                );
                return;

            case scDelete:
                Edit(kind: EditKind.Deleting, perCaret: () => DeleteForward(word));
                return;
        }
    }

    // ── Per-caret editing operations (run through Edit / ForEachCaret) ────────

    private void NewLine()
    {
        DeleteSelection();
        string line = _lines[_caretLine];
        string indent = LeadingWhitespace(line);
        if (indent.Length > _caretCol) indent = indent[.._caretCol]; // caret inside the indent
        var caret = (_caretLine, _caretCol);
        int open = _caretCol > 0 ? "([{".IndexOf(line[_caretCol - 1]) : -1;
        if (open < 0)
        {
            Replace(s: caret, e: caret, text: "\n" + indent);
            return;
        }

        // After an opening bracket the new line goes one level deeper; between a pair, the closer
        // drops to its own line below.
        string inner = indent + new string(c: ' ', count: TabWidth);
        if (_caretCol < line.Length && line[_caretCol] == ")]}"[open])
        {
            Replace(s: caret, e: caret, text: "\n" + inner + "\n" + indent);
            _caretLine--;
            _caretCol = inner.Length;
        }
        else
            Replace(s: caret, e: caret, text: "\n" + inner);
    }

    private void OpenLineBelow()
    {
        ClearSelection();
        _caretCol = _lines[_caretLine].Length;
        NewLine();
    }

    private void OpenLineAbove()
    {
        string indent = LeadingWhitespace(_lines[_caretLine]);
        Replace(s: (_caretLine, 0), e: (_caretLine, 0), text: indent + "\n");
        _caretLine--;
        _caretCol = indent.Length;
    }

    private void DeleteBackward(bool word, bool toLineStart)
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }

        string line = _lines[_caretLine];
        var caret = (_caretLine, _caretCol);
        if (_caretCol == 0)
        {
            if (_caretLine > 0)
                Replace(s: (_caretLine - 1, _lines[_caretLine - 1].Length), e: caret, text: "");
            return;
        }

        int start;
        if (toLineStart) start = 0;
        else if (word)
            start = TextNavigation.WordBoundary(text: line, index: _caretCol, direction: -1);
        else if (_caretCol < line.Length &&
                 Openers.IndexOf(line[_caretCol - 1]) is >= 0 and var o &&
                 line[_caretCol] == Closers[o])
        {
            // An empty pair goes as one.
            Replace(s: (_caretLine, _caretCol - 1), e: (_caretLine, _caretCol + 1), text: "");
            return;
        }
        else if (line.AsSpan(start: 0, length: _caretCol).TrimStart(' ').IsEmpty)
            start = (_caretCol - 1) / TabWidth * TabWidth; // in the indent: back one tab stop
        else start = TextNavigation.PreviousGraphemeBoundary(text: line, index: _caretCol);

        Replace(s: (_caretLine, start), e: caret, text: "");
    }

    private void DeleteForward(bool word)
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }

        string line = _lines[_caretLine];
        var caret = (_caretLine, _caretCol);
        if (_caretCol == line.Length)
        {
            if (_caretLine < _lines.Count - 1) Replace(s: caret, e: (_caretLine + 1, 0), text: "");
            return;
        }

        int end = word
            ? TextNavigation.WordBoundary(text: line, index: _caretCol, direction: +1)
            : TextNavigation.NextGraphemeBoundary(text: line, index: _caretCol);
        Replace(s: caret, e: (_caretLine, end), text: "");
    }

    // ── Line commands (act once on every line any caret touches) ──────────────

    private bool SpansLines()
    {
        if (_anchorLine >= 0 && _anchorLine != _caretLine) return true;
        return _extra.Exists(static x => x.AnchorLine >= 0 && x.AnchorLine != x.CaretLine);
    }

    private void IndentLines(int direction)
    {
        if (ReadOnly) return;
        RecordEdit(EditKind.Other);
        foreach (int l in TouchedLines())
        {
            if (direction > 0)
            {
                if (_lines[l].Length > 0)
                    Splice(s: (l, 0), e: (l, 0), text: new string(c: ' ', count: TabWidth));
                continue;
            }

            int n = 0;
            while (n < TabWidth && n < _lines[l].Length && _lines[l][n] == ' ') n++;
            if (n == 0 && _lines[l].StartsWith('\t')) n = 1;
            if (n > 0) Splice(s: (l, 0), e: (l, n), text: "");
        }

        Commit();
    }

    /// <summary>⌘/. Comment the touched lines out, or back in when every one of them already is.</summary>
    public void ToggleLineComment()
    {
        if (ReadOnly || LineComment is not { Length: > 0 } prefix) return;
        var lines = TouchedLines();
        if (lines.Count > 1) lines.RemoveAll(l => string.IsNullOrWhiteSpace(_lines[l]));
        if (lines.Count == 0) return;
        bool remove = lines.TrueForAll(l => _lines[l].AsSpan().TrimStart().StartsWith(prefix));
        // Comments line up at the shallowest indent of the block, as in every IDE.
        int column = lines.Min(l => LeadingWhitespace(_lines[l]).Length);
        RecordEdit(EditKind.Other);
        foreach (int l in lines)
        {
            if (!remove)
            {
                Splice(s: (l, column), e: (l, column), text: prefix + " ");
                continue;
            }

            int at = LeadingWhitespace(_lines[l]).Length;
            int end = at + prefix.Length;
            if (end < _lines[l].Length && _lines[l][end] == ' ') end++;
            Splice(s: (l, at), e: (l, end), text: "");
        }

        Commit();
    }

    private void DeleteLines()
    {
        if (ReadOnly) return;
        RecordEdit(EditKind.Other);
        var runs = TouchedRuns();
        for (int r = runs.Count - 1; r >= 0; r--)
        {
            (int a, int b) = runs[r];
            if (b < _lines.Count - 1) Splice(s: (a, 0), e: (b + 1, 0), text: "");
            else if (a > 0)
                Splice(s: (a - 1, _lines[a - 1].Length), e: (b, _lines[b].Length), text: "");
            else Splice(s: (0, 0), e: (b, _lines[b].Length), text: "");
        }

        _anchorLine = -1;
        for (int i = 0; i < _extra.Count; i++) _extra[i] = _extra[i] with { AnchorLine = -1 };
        MergeCarets();
        Commit();
    }

    private void MoveLines(int direction)
    {
        if (ReadOnly) return;
        var runs = TouchedRuns();
        if (direction < 0 ? runs[0].First == 0 : runs[^1].Last == _lines.Count - 1) return;
        RecordEdit(EditKind.Other);
        // Each block trades places with the line it moves past. Blocks are walked away from the
        // direction of travel so one never lands on a line the next still has to read.
        for (int r = 0; r < runs.Count; r++)
        {
            (int a, int b) = runs[direction < 0 ? r : runs.Count - 1 - r];
            int from = direction < 0 ? a - 1 : b + 1; // the line being jumped over
            int to = direction < 0 ? b : a; // where it ends up
            string jumped = _lines[from];
            _lines.RemoveAt(from);
            _lines.Insert(index: to, item: jumped);
            MapCarets(p => p.Line >= a && p.Line <= b ? (p.Line + direction, p.Col)
                : p.Line == from ? (to, Math.Min(val1: p.Col, val2: jumped.Length))
                : p
            );
        }

        Commit();
    }

    private void DuplicateLines(bool up)
    {
        if (ReadOnly) return;
        RecordEdit(EditKind.Other);
        var runs = TouchedRuns();
        for (int r = runs.Count - 1; r >= 0; r--)
        {
            (int a, int b) = runs[r];
            int n = b - a + 1;
            // The copy goes below when duplicating up (carets stay put), above when duplicating
            // down (carets ride down onto the lower copy).
            int at = up ? b + 1 : a;
            _lines.InsertRange(index: at, collection: _lines.GetRange(index: a, count: n));
            MapCarets(p => p.Line >= at ? (p.Line + n, p.Col) : p);
        }

        Commit();
    }

    // ── Caret movement (run per caret through Navigate) ───────────────────────

    /// <summary>Run a caret movement at every caret, then bring the primary one into view.</summary>
    private void Navigate(Action perCaret)
    {
        ForEachCaret(perCaret);
        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    private void SelectLine()
    {
        var (s, e) = OrderedSelection();
        (_anchorLine, _anchorCol) = (s.Line, 0);
        (_caretLine, _caretCol) = e.Line < _lines.Count - 1
            ? (e.Line + 1, 0)
            : (e.Line, _lines[e.Line].Length);
    }

    private void MoveWord(bool extend, int dir)
    {
        StartOrExtendSelection(extend);
        string line = _lines[_caretLine];
        if (dir < 0 && _caretCol == 0)
        {
            if (_caretLine > 0) _caretCol = _lines[--_caretLine].Length;
        }
        else if (dir > 0 && _caretCol == line.Length)
        {
            if (_caretLine < _lines.Count - 1) (_caretLine, _caretCol) = (_caretLine + 1, 0);
        }
        else
            _caretCol = TextNavigation.WordBoundary(text: line, index: _caretCol, direction: dir);
    }

    /// <summary>
    ///     Home / End. Home is "smart": it stops at the first non-blank character, and goes on to
    ///     column 0 when pressed there.
    /// </summary>
    private void MoveLineEdge(bool extend, int dir)
    {
        StartOrExtendSelection(extend);
        string line = _lines[_caretLine];
        var row = SoftWrap
            ? _visualRows[RowForCaret(line: _caretLine, col: _caretCol)]
            : new VisualRow(
                Line: _caretLine,
                Start: 0,
                End: line.Length,
                Width: 0f,
                FastGrid: true
            );
        if (dir > 0)
        {
            _caretCol = row.End;
            return;
        }

        int indent = LeadingWhitespace(line).Length;
        _caretCol = row.Start == 0 && _caretCol != indent ? indent : row.Start;
    }

    private void MoveDocumentEdge(bool extend, int dir)
    {
        StartOrExtendSelection(extend);
        (_caretLine, _caretCol) = dir < 0 ? (0, 0) : (_lines.Count - 1, _lines[^1].Length);
    }

    /// <summary>⌘⌥↑/↓: plant another cursor on the row above/below the outermost one.</summary>
    private void AddCaretVertical(int dir)
    {
        var edge = Primary;
        foreach (var x in _extra)
            if (dir < 0 ? x.CaretLine < edge.CaretLine : x.CaretLine > edge.CaretLine)
                edge = x;

        int row = RowForCaret(line: edge.CaretLine, col: edge.CaretCol);
        int target = row + dir;
        if (target < 0 || target >= _visualRows.Count) return;
        float x0 = float.IsNaN(edge.DesiredX)
            ? ColToX(row: _visualRows[row], col: edge.CaretCol)
            : edge.DesiredX;
        var targetRow = _visualRows[target];
        _extra.Add(Primary);
        Primary = new Sel(
            AnchorLine: -1,
            AnchorCol: 0,
            CaretLine: targetRow.Line,
            CaretCol: XToCol(row: targetRow, localX: x0),
            DesiredX: x0
        );
        MergeCarets();
        EnsureCaretVisible();
        MarkNeedsPaint();
    }

    private static string LeadingWhitespace(string s)
    {
        int i = 0;
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
            return;
        }

        int currentRowIndex = RowForCaret(line: _caretLine, col: _caretCol);
        var currentRow = _visualRows[currentRowIndex];
        string rowText = _lines[_caretLine][currentRow.Start..currentRow.End];
        var rowLayout = currentRow.FastGrid ? null : LayoutFor(rowText);
        int relative = _caretCol - currentRow.Start;
        int visualMoved = rowLayout?.MoveCaretVisual(textOffset: relative, direction: dCol) ??
                          (dCol < 0
                              ? TextNavigation.PreviousGraphemeBoundary(
                                  text: rowText,
                                  index: relative
                              )
                              : TextNavigation.NextGraphemeBoundary(
                                  text: rowText,
                                  index: relative
                              ));
        if (visualMoved != relative)
            _caretCol = currentRow.Start + visualMoved;
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
                _caretCol = TextNavigation.PreviousGraphemeBoundary(
                    text: _lines[_caretLine],
                    index: _caretCol
                );
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
                _caretCol = TextNavigation.NextGraphemeBoundary(
                    text: _lines[_caretLine],
                    index: _caretCol
                );
            }
        }
    }

    private void MoveCaretVertical(bool extend, int dLine)
    {
        StartOrExtendSelection(extend);
        int current = RowForCaret(line: _caretLine, col: _caretCol);
        int target = Math.Clamp(value: current + dLine, min: 0, max: _visualRows.Count - 1);
        if (target == current) return;
        // The column the run of ↑/↓ started from sticks, so passing through a short line doesn't
        // drag the caret left for good.
        if (float.IsNaN(_desiredX)) _desiredX = ColToX(row: _visualRows[current], col: _caretCol);
        var targetRow = _visualRows[target];
        _caretLine = targetRow.Line;
        _caretCol = XToCol(row: targetRow, localX: _desiredX);
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
        int AnchorCol,
        Sel[] Extra);

    /// <summary>One caret + selection; <c>AnchorLine &lt; 0</c> means no selection.</summary>
    private readonly record struct Sel(
        int AnchorLine,
        int AnchorCol,
        int CaretLine,
        int CaretCol,
        float DesiredX = float.NaN);

    private readonly record struct VisualRow(
        int Line,
        int Start,
        int End,
        float Width,
        bool FastGrid);

    private readonly record struct TokenRun(Token Token, string Text);

    // SourceText is the line string INSTANCE the entry was tokenized from: line strings are
    // immutable and replaced on edit, so a reference compare is an exact content-validity check.
    // That lets an edit keep the whole cache (entries self-invalidate) instead of clearing it —
    // previously one keystroke at the bottom of a 5000-line file re-tokenized 5000 lines.
    private readonly record struct CachedLineTokens(
        int EnteringState,
        int ExitingState,
        string SourceText,
        Token[] Tokens,
        TokenRun[] Runs);
}
