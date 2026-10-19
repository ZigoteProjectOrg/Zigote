using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>How serious a <see cref="CodeMarker" /> is — it picks the squiggle and gutter colour.</summary>
public enum CodeMarkerSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>
///     A diagnostic shown in an <see cref="AdwCodeEditor" />: a squiggle under the range, a dot in the
///     gutter, and the message as a tooltip on hover. Lines and columns are zero-based; an empty range
///     marks one character.
/// </summary>
public readonly record struct CodeMarker(
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    CodeMarkerSeverity Severity,
    string Message);

/// <summary>What a completion inserts, as far as the popup's icon is concerned.</summary>
public enum CodeCompletionKind
{
    Other,
    Keyword,
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    EnumMember,
    Delegate,
    Method,
    Property,
    Field,
    Event,
    Local,
    Parameter,
    Snippet,
}

/// <summary>
///     One completion item. <see cref="Label" /> is what is shown and filtered on;
///     <see cref="InsertText" /> (default: the label) replaces the word being typed.
/// </summary>
public sealed record CodeCompletion(
    string Label,
    CodeCompletionKind Kind = CodeCompletionKind.Other,
    string? Detail = null,
    string? Description = null)
{
    public string InsertText { get; init; } = Label;
}

/// <summary>One replacement in the document: <see cref="Length" /> characters at <see cref="Start" /> become <see cref="NewText" />.</summary>
public readonly record struct CodeTextEdit(int Start, int Length, string NewText);

/// <summary>
///     One overload in a parameter hint. <see cref="Parameters" /> are (start, length) ranges in
///     <see cref="Label" />; <see cref="ActiveParameter" /> is the one being typed (-1 = none fits).
/// </summary>
public sealed record CodeSignature(string Label, IReadOnlyList<(int Start, int Length)> Parameters, string? Documentation = null)
{
    public int ActiveParameter { get; init; } = -1;

    /// <summary>Per-parameter docs, in <see cref="Parameters" /> order; null entries are undocumented.</summary>
    public IReadOnlyList<string?> ParameterDocs { get; init; } = [];
}

/// <summary>The overloads of the call around the caret, and which one is the best match.</summary>
public sealed record CodeSignatureHelp(IReadOnlyList<CodeSignature> Signatures, int ActiveSignature);

/// <summary>
///     The editing assists a language host plugs into the editor: diagnostics markers, a completion
///     popup fed by an asynchronous provider, programmatic caret moves, and a first-chance key hook.
///     The editor stays language-agnostic — it never parses anything; the host does.
/// </summary>
public sealed partial class AdwCodeEditor
{
    private const int CompletionVisibleRows = 8;
    private const float CompletionWidth = 420f;

    private IReadOnlyList<CodeCompletion>? _completionAll; // provider's answer for the current word
    private readonly List<CodeCompletion> _completionShown = [];
    private int _completionIndex;
    private int _completionTop; // first visible row of the shown list
    private int _completionLine = -1; // where the word being completed starts
    private int _completionCol;
    private Rect _completionRect;
    private CancellationTokenSource? _completionCts;
    private int _docVersion;
    private IReadOnlyList<CodeMarker> _markers = [];
    private CodeSignatureHelp? _signature;
    private int _signatureChoice = -1; // an overload picked with ↑/↓; -1 = the provider's
    private CancellationTokenSource? _signatureCts;
    private Rect _signatureRect;
    private Offset _hoverPoint;
    private bool _hovering;

    /// <summary>Diagnostics to draw: squiggles, gutter dots and hover messages. Replace the list to update.</summary>
    public IReadOnlyList<CodeMarker> Markers
    {
        get => _markers;
        set
        {
            _markers = value ?? [];
            MarkNeedsPaint();
        }
    }

    /// <summary>
    ///     Supplies completions for the word at a caret. Called with the whole document, the caret's
    ///     character offset in it, and a token that is cancelled when the user has moved on. The
    ///     editor filters the answer by what is typed after the call, so return every candidate for
    ///     the context, not just matches. Called off the critical path: typing never waits for it.
    /// </summary>
    public Func<string, int, CancellationToken, Task<IReadOnlyList<CodeCompletion>>>? CompletionProvider { get; set; }

    /// <summary>
    ///     First chance at every key press while focused (character, SDL scancode, modifiers). Return
    ///     true to consume it — how a host binds ⌘↵ to "run" without the editor knowing what run means.
    /// </summary>
    public Func<char, uint, Modifiers, bool>? OnKeyDown { get; set; }

    /// <summary>
    ///     Supplies parameter hints for the call around a caret (document, caret offset, cancel token);
    ///     null when the caret is in no argument list. Asked when <c>(</c> or <c>,</c> is typed and after
    ///     every edit while the hint shows.
    /// </summary>
    public Func<string, int, CancellationToken, Task<CodeSignatureHelp?>>? SignatureProvider { get; set; }

    /// <summary>Whether a parameter hint is showing.</summary>
    public bool IsSignatureHelpOpen => _signature is not null;

    /// <summary>The overload the parameter hint shows, or null.</summary>
    public CodeSignature? ActiveSignature => _signature is { } h ? h.Signatures[ShownSignature(h)] : null;

    /// <summary>Open the parameter hint for the call around the caret now (what Ctrl+Shift+Space does).</summary>
    public void ShowSignatureHelp() => RequestSignature();

    /// <summary>
    ///     Apply <paramref name="edits" /> (offsets into the current <see cref="Text" />, non-overlapping)
    ///     as ONE undoable edit. Carets move with the text around them — what a formatter needs, where
    ///     setting <see cref="Text" /> would drop the undo history and the caret.
    /// </summary>
    public void ApplyEdits(IReadOnlyList<CodeTextEdit> edits)
    {
        if (ReadOnly || edits.Count == 0) return;
        var lineStarts = new int[_lines.Count];
        for (int i = 1; i < _lines.Count; i++) lineStarts[i] = lineStarts[i - 1] + _lines[i - 1].Length + 1;
        (int, int) At(int offset)
        {
            int line = Array.BinarySearch(lineStarts, offset);
            if (line < 0) line = ~line - 1;
            line = Math.Clamp(value: line, min: 0, max: _lines.Count - 1);
            return (line, Math.Clamp(value: offset - lineStarts[line], min: 0, max: _lines[line].Length));
        }

        CloseCompletion();
        RecordEdit(EditKind.Other);
        // Back to front, so each edit's offsets still describe the text it was computed against.
        foreach (var edit in edits.OrderByDescending(e => e.Start))
            Splice(s: At(edit.Start), e: At(edit.Start + edit.Length), text: edit.NewText.Replace("\r\n", "\n"));
        ClampCaret();
        Commit();
    }

    /// <summary>The primary caret, zero-based.</summary>
    public (int Line, int Column) Caret => (_caretLine, _caretCol);

    /// <summary>Whether the completion popup is showing.</summary>
    public bool IsCompletionOpen => _completionShown.Count > 0;

    /// <summary>
    ///     Move the caret to a zero-based line and column (clamped), drop any selection or extra
    ///     carets, scroll it into view and take focus — "jump to error" from a problems list.
    /// </summary>
    public void GoTo(int line, int column)
    {
        _extra.Clear();
        ClearSelection();
        _caretLine = Math.Clamp(value: line, min: 0, max: _lines.Count - 1);
        _caretCol = Math.Clamp(value: column, min: 0, max: _lines[_caretLine].Length);
        CloseCompletion();
        CloseSignature();
        EnsureCaretVisible();
        Owner?.RequestFocus(this);
        MarkNeedsPaint();
    }

    /// <summary>Open the completion popup at the caret now (what Ctrl+Space does).</summary>
    public void ShowCompletion() => RequestCompletion(force: true);

    // ── Hooks the editor core calls ───────────────────────────────────────────

    // After every committed edit: keep the popup's filter in step with the word, or retire it.
    private void AfterEdit()
    {
        _docVersion++;
        if (_signature is not null || _signatureCts is not null) RequestSignature();
        if (_completionAll is null && _completionCts is null) return;
        if (!CompletionContextHolds(out string prefix)) CloseCompletion();
        else if (_completionAll is not null) FilterCompletion(prefix);
    }

    // After typed text: maybe open the popup — on '.', or on the first letter of a fresh word.
    private void AfterTextInput(string text)
    {
        if (text.Length != 1) return;
        char c = text[0];
        // ')' over an auto-closed paren moves the caret without an edit — refresh for it too.
        if (c is '(' or ',' || (c == ')' && _signature is not null)) RequestSignature();
        if (CompletionProvider is null) return;
        if (_completionAll is not null || _completionCts is not null) return; // AfterEdit refiltered
        if (c == '.' || (IsIdentChar(c) && !char.IsDigit(c) && WordStart() == _caretCol - 1))
            RequestCompletion(force: false);
    }

    // Keys the open popup owns. Returns true when consumed.
    private bool CompletionKey(uint scancode, Modifiers mods)
    {
        const uint scSpace = 44, scUp = 82, scDown = 81, scPageUp = 75, scPageDown = 78;
        const uint scReturn = 40, scKpEnter = 88, scTab = 43, scEscape = 41;
        const uint scLeft = 80, scRight = 79, scHome = 74, scEnd = 77;

        if (scancode == scSpace && mods.HasFlag(Modifiers.Ctrl))
        {
            if (mods.HasFlag(Modifiers.Shift)) RequestSignature();
            else RequestCompletion(force: true);
            return true;
        }

        if (!IsCompletionOpen) return SignatureKey(scancode, mods);
        switch (scancode)
        {
            case scUp:
                MoveCompletion(-1);
                return true;
            case scDown:
                MoveCompletion(1);
                return true;
            case scPageUp:
                MoveCompletion(-CompletionVisibleRows);
                return true;
            case scPageDown:
                MoveCompletion(CompletionVisibleRows);
                return true;
            case scReturn or scKpEnter or scTab when mods == Modifiers.None || mods == Modifiers.Shift:
                AcceptCompletion(_completionShown[_completionIndex]);
                return true;
            case scEscape:
                CloseCompletion();
                return true;
            case scLeft or scRight or scHome or scEnd:
                CloseCompletion();
                return false; // and let the caret move
            default:
                return false;
        }
    }

    // Keys the parameter hint owns while no completion list is up.
    private bool SignatureKey(uint scancode, Modifiers mods)
    {
        const uint scUp = 82, scDown = 81, scEscape = 41;
        const uint scLeft = 80, scRight = 79, scHome = 74, scEnd = 77, scPageUp = 75, scPageDown = 78;
        if (_signature is not { } help) return false;
        switch (scancode)
        {
            case scEscape:
                CloseSignature();
                return true;
            case scUp or scDown when help.Signatures.Count > 1 && mods == Modifiers.None:
                int n = help.Signatures.Count;
                _signatureChoice = (ShownSignature(help) + (scancode == scDown ? 1 : -1) + n) % n;
                MarkNeedsPaint();
                return true;
            case scLeft or scRight or scHome or scEnd or scUp or scDown or scPageUp or scPageDown:
                // The caret is about to move: re-ask once it has (the hint follows, or closes outside the call).
                Owner?.Post(() =>
                {
                    if (_signature is not null) RequestSignature();
                });
                return false;
            default:
                return false;
        }
    }

    // ── Parameter hints ───────────────────────────────────────────────────────

    private int ShownSignature(CodeSignatureHelp h) =>
        Math.Clamp(value: _signatureChoice >= 0 ? _signatureChoice : h.ActiveSignature, min: 0, max: h.Signatures.Count - 1);

    private int CaretOffset()
    {
        int offset = _caretCol;
        for (int i = 0; i < _caretLine; i++) offset += _lines[i].Length + 1;
        return offset;
    }

    private void RequestSignature()
    {
        if (SignatureProvider is not { } provider || _extra.Count > 0) return;
        _signatureCts?.Cancel();
        var cts = _signatureCts = new CancellationTokenSource();
        var task = provider(Text, CaretOffset(), cts.Token);
        void Deliver(CodeSignatureHelp? help)
        {
            if (!ReferenceEquals(_signatureCts, cts) || cts.IsCancellationRequested) return;
            _signatureCts = null;
            if (help is null || help.Signatures.Count == 0)
            {
                CloseSignature();
                return;
            }

            // Keep a hand-picked overload while the call keeps the same set of them.
            if (_signature is null || _signature.Signatures.Count != help.Signatures.Count) _signatureChoice = -1;
            _signature = help;
            MarkNeedsPaint();
        }

        if (task.IsCompletedSuccessfully) Deliver(task.Result);
        else
        {
            var app = Owner;
            task.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully) app?.Post(() => Deliver(t.Result));
                },
                TaskScheduler.Default
            );
        }
    }

    private void CloseSignature()
    {
        _signatureCts?.Cancel();
        _signatureCts = null;
        _signatureChoice = -1;
        if (_signature is null) return;
        _signature = null;
        MarkNeedsPaint();
    }

    // ── Completion state ──────────────────────────────────────────────────────

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private int WordStart()
    {
        string line = _lines[_caretLine];
        int start = Math.Min(_caretCol, line.Length);
        while (start > 0 && IsIdentChar(line[start - 1])) start--;
        return start;
    }

    private bool CompletionContextHolds(out string prefix)
    {
        prefix = "";
        if (_extra.Count > 0 || HasSelection || _caretLine != _completionLine || _caretCol < _completionCol) return false;
        string line = _lines[_caretLine];
        for (int i = _completionCol; i < _caretCol; i++)
            if (!IsIdentChar(line[i])) return false;
        prefix = line[_completionCol.._caretCol];
        return true;
    }

    private void RequestCompletion(bool force)
    {
        if (CompletionProvider is not { } provider || _extra.Count > 0 || HasSelection) return;
        CloseCompletion();
        _completionLine = _caretLine;
        _completionCol = WordStart();
        int version = _docVersion;
        var cts = _completionCts = new CancellationTokenSource();
        string text = Text;
        int offset = 0;
        for (int i = 0; i < _caretLine; i++) offset += _lines[i].Length + 1;
        offset += _caretCol;

        var task = provider(text, offset, cts.Token);
        void Deliver(IReadOnlyList<CodeCompletion> items)
        {
            if (!ReferenceEquals(_completionCts, cts) || cts.IsCancellationRequested) return;
            _completionCts = null;
            if (!CompletionContextHolds(out string prefix) || (!force && items.Count == 0 && version == _docVersion)) return;
            _completionAll = items;
            FilterCompletion(prefix);
        }

        if (task.IsCompletedSuccessfully) Deliver(task.Result);
        else
        {
            var app = Owner;
            task.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully) app?.Post(() => Deliver(t.Result));
                },
                TaskScheduler.Default
            );
        }
    }

    private void FilterCompletion(string prefix)
    {
        _completionShown.Clear();
        if (_completionAll is null) return;
        // Prefix matches first, then the rest that contain the text — both in the provider's order.
        foreach (var item in _completionAll)
            if (item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _completionShown.Add(item);
        if (prefix.Length > 1)
        {
            foreach (var item in _completionAll)
                if (!item.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    item.Label.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                    _completionShown.Add(item);
        }

        // Exactly what is typed already: nothing to complete.
        if (_completionShown.Count == 1 && _completionShown[0].InsertText == prefix) _completionShown.Clear();
        _completionIndex = 0;
        _completionTop = 0;
        if (_completionShown.Count == 0) _completionAll = _completionAll.Count == 0 ? null : _completionAll;
        MarkNeedsPaint();
    }

    private void MoveCompletion(int delta)
    {
        int n = _completionShown.Count;
        _completionIndex = delta is 1 or -1
            ? (_completionIndex + delta + n) % n // wrap at the ends, like every IDE
            : Math.Clamp(value: _completionIndex + delta, min: 0, max: n - 1);
        if (_completionIndex < _completionTop) _completionTop = _completionIndex;
        if (_completionIndex >= _completionTop + CompletionVisibleRows)
            _completionTop = _completionIndex - CompletionVisibleRows + 1;
        MarkNeedsPaint();
    }

    private void AcceptCompletion(CodeCompletion item)
    {
        var start = (_completionLine, _completionCol);
        var end = (_caretLine, _caretCol);
        CloseCompletion();
        Edit(kind: EditKind.Other, perCaret: () => Replace(s: start, e: end, text: item.InsertText));
    }

    private void CloseCompletion()
    {
        _completionCts?.Cancel();
        _completionCts = null;
        _completionAll = null;
        if (_completionShown.Count > 0)
        {
            _completionShown.Clear();
            MarkNeedsPaint();
        }
    }

    // ── Pointer ───────────────────────────────────────────────────────────────

    // A press on the popup picks a row; anywhere else closes it. True when the popup took the press.
    private bool CompletionPointerDown(Offset point)
    {
        if (_signature is not null && !_signatureRect.Contains(px: point.X, py: point.Y)) CloseSignature();
        if (!IsCompletionOpen) return false;
        if (!_completionRect.Contains(px: point.X, py: point.Y))
        {
            CloseCompletion();
            return false;
        }

        int row = (int)((point.Y - _completionRect.Y - 2f) / CompletionRowHeight);
        int index = _completionTop + row;
        if (index >= 0 && index < _completionShown.Count) AcceptCompletion(_completionShown[index]);
        return true;
    }

    private void TrackHover(Offset point)
    {
        _hoverPoint = point;
        _hovering = true;
    }

    public override void OnPointerExit()
    {
        _hovering = false;
        base.OnPointerExit();
    }

    /// <summary>The message of the marker under the pointer, shown by the app's tooltip.</summary>
    public override string? TooltipText
    {
        get
        {
            if (!_hovering || _markers.Count == 0 || _visualRows.Count == 0 || IsCompletionOpen) return null;
            if (_hoverPoint.X < Bounds.X + _gutterWidth)
            {
                // The gutter: any marker on that line.
                int visual = (int)((_hoverPoint.Y - (TextTop - _scrollY)) / _lineHeight);
                if (visual < 0 || visual >= _visualRows.Count) return null;
                int line = _visualRows[visual].Line;
                var onLine = _markers.Where(m => m.Line <= line && line <= Math.Max(m.Line, m.EndLine)).Select(m => m.Message).ToArray();
                return onLine.Length == 0 ? null : string.Join("\n", onLine);
            }

            var (l, c) = HitPosition(_hoverPoint);
            foreach (var m in _markers)
            {
                var (sl, sc, el, ec) = Normalised(m);
                bool after = l > sl || (l == sl && c >= sc);
                bool before = l < el || (l == el && c <= ec);
                if (after && before) return m.Message;
            }

            return null;
        }
    }

    // ── Painting ──────────────────────────────────────────────────────────────

    private float CompletionRowHeight => _lineHeight + 4f;

    private (int sl, int sc, int el, int ec) Normalised(CodeMarker m)
    {
        int sl = Math.Clamp(value: m.Line, min: 0, max: _lines.Count - 1);
        int el = Math.Clamp(value: Math.Max(m.Line, m.EndLine), min: sl, max: _lines.Count - 1);
        int sc = Math.Clamp(value: m.Column, min: 0, max: _lines[sl].Length);
        int ec = Math.Clamp(value: el == sl ? Math.Max(m.EndColumn, m.Column) : m.EndColumn, min: 0, max: _lines[el].Length);
        if (sl == el && ec <= sc) ec = Math.Min(sc + 1, Math.Max(sc, _lines[sl].Length)); // one character
        if (sl == el && ec <= sc && sc > 0) sc--; // an empty line end: mark the last character
        return (sl, sc, el, ec);
    }

    private Color SeverityColor(CodeMarkerSeverity s) => s switch {
        CodeMarkerSeverity.Error => _theme.Error,
        CodeMarkerSeverity.Warning => _theme.Warning,
        _ => _theme.Info,
    };

    private void PaintMarkers(PaintList paint, int first, int last)
    {
        if (_markers.Count == 0 || first > last) return;

        // Squiggles, clipped to the text area.
        paint.AddClipStart(new Rect(x: Bounds.X + _gutterWidth, y: Bounds.Y, width: Bounds.Width - _gutterWidth, height: Bounds.Height));
        foreach (var m in _markers)
        {
            var (sl, sc, el, ec) = Normalised(m);
            var color = SeverityColor(m.Severity);
            for (int visual = first; visual <= last; visual++)
            {
                var row = _visualRows[visual];
                if (row.Line < sl || row.Line > el) continue;
                int c0 = row.Line == sl ? Math.Max(row.Start, sc) : row.Start;
                int c1 = row.Line == el ? Math.Min(row.End, ec) : row.End;
                if (c1 <= c0) continue;
                float x0 = TextLeft - _scrollX + ColToX(row: row, col: c0);
                float x1 = TextLeft - _scrollX + ColToX(row: row, col: c1);
                float y = TextTop - _scrollY + (visual * _lineHeight) + _lineHeight - 3f;
                // A zigzag of straight segments: the conventional "this is wrong" underline.
                const float step = 2.5f;
                bool up = false;
                for (float x = x0; x < x1; x += step)
                {
                    float nx = MathF.Min(x + step, x1);
                    float ya = up ? y - 1.5f : y, yb = up ? y : y - 1.5f;
                    paint.AddBezier(x, ya, x, ya, nx, yb, nx, yb, color, 1.2f);
                    up = !up;
                }
            }
        }

        paint.AddClipEnd();

        // A dot in the gutter for each marked line (the worst severity wins).
        for (int visual = first; visual <= last; visual++)
        {
            var row = _visualRows[visual];
            if (row.Start != 0) continue;
            CodeMarkerSeverity? worst = null;
            foreach (var m in _markers)
            {
                if (row.Line < m.Line || row.Line > Math.Max(m.Line, m.EndLine)) continue;
                if (worst is null || m.Severity < worst) worst = m.Severity;
            }

            if (worst is not { } sev) continue;
            float cy = TextTop - _scrollY + (visual * _lineHeight) + (_lineHeight / 2f);
            paint.AddRect(new Rect(x: Bounds.X + 3f, y: cy - 3f, width: 6f, height: 6f), SeverityColor(sev), radius: 3f);
        }
    }

    /// <summary>The parameter hint: a strip above the caret line, the active parameter in the accent colour.</summary>
    private void PaintSignature(PaintList paint)
    {
        if (_signature is not { } help || _visualRows.Count == 0) return;
        var sig = help.Signatures[ShownSignature(help)];
        string? doc = sig.ActiveParameter >= 0 && sig.ActiveParameter < sig.ParameterDocs.Count && sig.ParameterDocs[sig.ActiveParameter] is { } pd
            ? $"{ParameterName(sig, sig.ActiveParameter)}: {pd}"
            : sig.Documentation;

        float rowH = CompletionRowHeight;
        float h = rowH + 6f + (doc is null ? 0f : 22f);
        string counter = help.Signatures.Count > 1 ? $"{ShownSignature(help) + 1}/{help.Signatures.Count}  " : "";
        // As wide as the signature needs (room for the doc line too), within the editor.
        float w = MathF.Min(MathF.Max(((counter.Length + sig.Label.Length) * _charWidth) + 24f, doc is null ? 160f : 320f), Bounds.Width - 8f);

        int caretRow = RowForCaret(line: _caretLine, col: _caretCol);
        float rowTop = TextTop - _scrollY + (caretRow * _lineHeight);
        float x = Math.Clamp(value: TextLeft - _scrollX + ColToX(line: _caretLine, col: _caretCol) - 40f, min: Bounds.X + 4f, max: MathF.Max(Bounds.X + 4f, Bounds.Right - w - 4f));
        float y = rowTop - h - 4f;
        if (y < Bounds.Y + 2f) y = rowTop + _lineHeight + 4f + (IsCompletionOpen ? (Math.Min(CompletionVisibleRows, _completionShown.Count) * rowH) + 40f : 0f);
        _signatureRect = new Rect(x: x, y: y, width: w, height: h);

        var panel = _theme.IsDark ? _theme.PanelRaised : _theme.Surface;
        paint.AddShadow(_signatureRect, new Color(0f, 0f, 0f, _theme.IsDark ? 0.45f : 0.18f), 9f, 14f);
        paint.AddRect(_signatureRect, panel, radius: 9f);
        paint.AddBorder(_signatureRect, _theme.OnSurface.WithAlpha(0.12f), radius: 9f);
        paint.AddClipStart(_signatureRect, radius: 9f);

        // Fit the label in the strip, sliding it left when the active parameter would fall off the end.
        int fit = Math.Max(8, (int)((w - 20f) / _charWidth) - counter.Length);
        string label = sig.Label;
        int shift = 0;
        if (label.Length > fit)
        {
            int focusEnd = sig.ActiveParameter >= 0 ? sig.Parameters[sig.ActiveParameter].Start + sig.Parameters[sig.ActiveParameter].Length : 0;
            shift = Math.Clamp(value: focusEnd - fit + 2, min: 0, max: label.Length - fit + 1);
            label = (shift > 0 ? "…" + label[(shift + 1)..] : label);
            if (label.Length > fit) label = label[..(fit - 1)] + "…";
        }

        float baseline = TextBaseline.Centered(y + 3f, rowH, _fontSize);
        float tx = x + 10f;
        if (counter.Length > 0)
        {
            DrawSegment(paint: paint, s: counter, x: tx, baseline: baseline, color: _theme.OnSurface.WithAlpha(0.5f));
            tx += counter.Length * _charWidth;
        }

        DrawSegment(paint: paint, s: label, x: tx, baseline: baseline, color: _theme.OnSurface.WithAlpha(0.8f));
        if (sig.ActiveParameter >= 0 && sig.ActiveParameter < sig.Parameters.Count)
        {
            // Overdraw the active parameter (the face is monospace, so its column is its x).
            var (ps, pl) = sig.Parameters[sig.ActiveParameter];
            int vs = ps - shift, ve = Math.Min(vs + pl, label.Length - (label.EndsWith('…') ? 1 : 0));
            vs = Math.Max(vs, shift > 0 ? 1 : 0);
            if (ve > vs)
            {
                float px = tx + (vs * _charWidth);
                paint.AddRect(new Rect(x: px - 2f, y: y + 5f, width: ((ve - vs) * _charWidth) + 4f, height: rowH - 4f), _theme.Accent.WithAlpha(0.18f), radius: 4f);
                DrawSegment(paint: paint, s: label[vs..ve], x: px, baseline: baseline, color: _theme.Accent);
            }
        }

        if (doc is not null)
        {
            float dy = y + rowH + 6f;
            paint.AddRect(new Rect(x: x, y: dy - 2f, width: w, height: 1f), _theme.OnSurface.WithAlpha(0.1f));
            int maxChars = (int)((w - 20f) / 6.4f);
            if (doc.Length > maxChars) doc = doc[..Math.Max(0, maxChars - 1)] + "…";
            paint.AddText(doc, x + 10f, dy + 15f, _theme.OnSurface.WithAlpha(0.72f), 12f);
        }

        paint.AddClipEnd();
    }

    private static string ParameterName(CodeSignature sig, int index)
    {
        var (s, l) = sig.Parameters[index];
        string text = sig.Label.Substring(s, l);
        int eq = text.IndexOf(" = ", StringComparison.Ordinal);
        if (eq >= 0) text = text[..eq];
        return text[(text.LastIndexOf(' ') + 1)..];
    }

    private void PaintCompletion(PaintList paint)
    {
        if (!IsCompletionOpen || _visualRows.Count == 0) return;

        int rows = Math.Min(CompletionVisibleRows, _completionShown.Count);
        float rowH = CompletionRowHeight;
        var selected = _completionShown[_completionIndex];
        bool hasDoc = !string.IsNullOrEmpty(selected.Description);
        float docH = hasDoc ? 28f : 0f;
        float h = (rows * rowH) + 4f + docH;
        float w = MathF.Min(CompletionWidth, Bounds.Width - 8f);

        int caretRow = RowForCaret(line: _completionLine, col: _completionCol);
        float rowTop = TextTop - _scrollY + (caretRow * _lineHeight);
        float x = TextLeft - _scrollX + ColToX(line: _completionLine, col: _completionCol) - 28f;
        x = Math.Clamp(value: x, min: Bounds.X + 4f, max: MathF.Max(Bounds.X + 4f, Bounds.Right - w - 4f));
        float y = rowTop + _lineHeight + 2f;
        if (y + h > Bounds.Bottom - 2f && rowTop - h - 2f >= Bounds.Y) y = rowTop - h - 2f; // flip above
        _completionRect = new Rect(x: x, y: y, width: w, height: h);

        var panel = _theme.IsDark ? _theme.PanelRaised : _theme.Surface;
        paint.AddShadow(_completionRect, new Color(0f, 0f, 0f, _theme.IsDark ? 0.45f : 0.18f), 9f, 14f);
        paint.AddRect(_completionRect, panel, radius: 9f);
        paint.AddBorder(_completionRect, _theme.OnSurface.WithAlpha(0.12f), radius: 9f);
        paint.AddClipStart(_completionRect, radius: 9f);

        for (int i = 0; i < rows; i++)
        {
            int index = _completionTop + i;
            var item = _completionShown[index];
            float ry = y + 2f + (i * rowH);
            if (index == _completionIndex)
                paint.AddRect(new Rect(x: x + 3f, y: ry, width: w - 6f, height: rowH), _theme.Accent.WithAlpha(0.22f), radius: 6f);

            // The kind badge: one letter on its colour.
            var (letter, badge) = KindBadge(item.Kind);
            float bs = rowH - 8f;
            paint.AddRect(new Rect(x: x + 8f, y: ry + 4f, width: bs, height: bs), badge.WithAlpha(0.22f), radius: 4f);
            DrawSegment(paint: paint, s: letter, x: x + 8f + ((bs - _charWidth) / 2f), baseline: TextBaseline.Centered(ry, rowH, _fontSize), color: badge);

            float tx = x + 14f + bs;
            DrawSegment(paint: paint, s: item.Label, x: tx, baseline: TextBaseline.Centered(ry, rowH, _fontSize), color: _theme.OnSurface);
            if (item.Detail is { Length: > 0 } detail)
            {
                // Right-aligned, and cut to the room left after the label.
                float room = w - (tx - x) - ((item.Label.Length + 2) * _charWidth) - 12f;
                int fit = (int)(room / _charWidth);
                if (fit > 3)
                {
                    string d = detail.Length > fit ? detail[..(fit - 1)] + "…" : detail;
                    DrawSegment(paint: paint, s: d, x: x + w - 10f - (d.Length * _charWidth), baseline: TextBaseline.Centered(ry, rowH, _fontSize), color: _theme.OnSurface.WithAlpha(0.5f));
                }
            }
        }

        if (hasDoc)
        {
            float dy = y + 2f + (rows * rowH);
            paint.AddRect(new Rect(x: x, y: dy, width: w, height: 1f), _theme.OnSurface.WithAlpha(0.1f));
            int maxChars = (int)((w - 20f) / 6.4f);
            string doc = selected.Description!.ReplaceLineEndings(" ");
            if (doc.Length > maxChars) doc = doc[..Math.Max(0, maxChars - 1)] + "…";
            paint.AddText(doc, x + 10f, dy + 18f, _theme.OnSurface.WithAlpha(0.72f), 12f);
        }

        paint.AddClipEnd();
    }

    private (string Letter, Color Color) KindBadge(CodeCompletionKind kind) => kind switch {
        CodeCompletionKind.Method => ("M", new Color(0xFF9141AC)),
        CodeCompletionKind.Property => ("P", new Color(0xFF3584E4)),
        CodeCompletionKind.Field => ("F", new Color(0xFF3584E4)),
        CodeCompletionKind.Event => ("E", new Color(0xFFE66100)),
        CodeCompletionKind.Class => ("C", new Color(0xFF26A269)),
        CodeCompletionKind.Struct => ("S", new Color(0xFF26A269)),
        CodeCompletionKind.Interface => ("I", new Color(0xFF26A269)),
        CodeCompletionKind.Enum => ("E", new Color(0xFFC88800)),
        CodeCompletionKind.EnumMember => ("V", new Color(0xFFC88800)),
        CodeCompletionKind.Delegate => ("D", new Color(0xFF26A269)),
        CodeCompletionKind.Namespace => ("N", _theme.OnSurface.WithAlpha(0.6f)),
        CodeCompletionKind.Keyword => ("K", _theme.OnSurface.WithAlpha(0.6f)),
        CodeCompletionKind.Local or CodeCompletionKind.Parameter => ("L", new Color(0xFF2190A4)),
        CodeCompletionKind.Snippet => ("⁘", _theme.OnSurface.WithAlpha(0.6f)),
        _ => ("·", _theme.OnSurface.WithAlpha(0.6f)),
    };
}
