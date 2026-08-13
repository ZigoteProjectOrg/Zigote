using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Material;

/// <summary>
///     Single-line, flat macOS-style text input. Opaque <see cref="ThemeData.Surface" /> fill with a
///     hairline <see cref="ThemeData.Separator" /> border; on focus it draws the theme focus ring
///     rather
///     than thickening the border. Supports text selection, copy/paste/cut, drag selection, and a
///     right-click context menu.
/// </summary>
public class TextField : Widget, ITextInputClient
{
    private const float CaretMargin = 2f;

    // Double-click (word select) detection — timing-based (no click-count in the event pipeline).
    private const float DoubleClickSeconds = 0.4f;
    private readonly TextEditingController? _controller;
    private float[] _adv = [0f]; // cumulative prefix advances: _adv[i] = width of Text[..i]
    private float _advFs;
    private string? _advText;

    private float
        _caretBase; // App.Time at the last caret activity — resets the blink so it's solid while typing

    private int _compositionEnd;
    private int _compositionSelectionLength;
    private int _compositionSelectionStart;
    private int _compositionStart;
    private string _compositionText = string.Empty;
    private ContextMenu? _contextMenu;
    private int _cursorPos;

    private int _desiredCol = -1; // sticky column for Up/Down navigation; -1 = recompute from caret
    private string _hint = string.Empty;
    private bool _hovered;
    private bool _isDragging;
    private int _lastClickPos;
    private float _lastClickTime = -1f;
    private TextLayout? _layout;
    private float _layoutFs;
    private string? _layoutText;
    private bool _multiline;
    private bool _obscure;
    private float _scrollX;
    private float _scrollY; // multi-line vertical scroll
    private int _selectionAnchor = -1; // -1 means no selection
    private Size _size;
    private string _text = string.Empty;
    private ThemeData _theme = ThemeData.Dark;

    /// <summary>
    ///     Named-argument constructor:
    ///     <c>
    ///         new TextField(controller: c, decoration: new
    ///         InputDecoration(hintText: "Name"), onChanged: (v) => …, onSubmitted: (v) => …, maxLines: 1)
    ///     </c>
    ///     .
    ///     A <paramref name="controller" /> seeds the text, receives edits, and pushes external
    ///     <see cref="TextEditingController.Text" /> assignments back into the field.
    ///     <paramref name="maxLines" />
    ///     = 1 is single-line; anything else (or null) enables the multi-line field.
    /// </summary>
    public TextField(
        TextEditingController? controller = null,
        Action<string>? onChanged = null,
        Action<string>? onSubmitted = null,
        InputDecoration? decoration = null,
        int? maxLines = 1,
        int? minLines = null,
        bool readOnly = false,
        bool obscureText = false)
    {
        _hint = decoration?.HintText ?? "";
        OnSubmitted = onSubmitted;
        ReadOnly = readOnly;

        if (maxLines is null || maxLines > 1)
        {
            _multiline = true;
            if (maxLines is { } ml && ml > 1) MaxLines = ml;
            if (minLines is { } mn) MinLines = mn;
        }

        if (controller is not null)
        {
            _controller = controller;
            _text = controller.Text;
            controller.Changed += OnControllerChanged;
            OnChanged = v =>
            {
                controller.SetTextSilently(v);
                onChanged?.Invoke(v);
            };
        }
        else
            OnChanged = onChanged;

        _obscure = obscureText;
    }

    public string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (value == _text) return;
            _text = value;

            // Caret, selection and composition are indices into _text, so any write that
            // changes it can leave them dangling — and the next keystroke would then index out
            // of range in OnTextInput. Clamping belongs here rather than at the call sites,
            // because Text is public and written from several directions: a bound "controlled
            // value" (the F# Ui.textField assigns Text on every reconcile, including when an
            // app clears its draft after sending), a TextEditingController, and app code
            // setting it directly.
            if (_cursorPos > _text.Length) _cursorPos = _text.Length;

            // The selection is dropped outright rather than clamped. A selection describes a
            // span of the *previous* text; once that text is replaced by someone else there is
            // no defensible way to carry it over — and merely bounds-checking the anchor is not
            // enough, because a selection whose anchor still happens to be in range (say, from
            // Select All) would survive and silently swallow the user's next keystroke.
            // Internal edits clear or re-establish the selection immediately after assigning
            // Text, so nothing here fights them.
            ClearSelection();

            if (_compositionText.Length > 0)
            {
                // An in-flight IME composition cannot survive the text changing underneath it.
                _compositionText = string.Empty;
                _compositionStart = _compositionEnd = _cursorPos;
            }

            _desiredCol = -1;
            MarkNeedsPaint();
        }
    }

    /// <summary>
    ///     When true the text is rendered as bullet characters (password entry). The real
    ///     <see cref="Text" /> is still what's edited and reported through <see cref="OnChanged" />.
    /// </summary>
    public bool Obscure
    {
        get => _obscure;
        set => SetPaint(field: ref _obscure, value: value);
    }

    /// <summary>What the field actually renders/measures — the masked form when <see cref="Obscure" />.</summary>
    private string Visible => Obscure ? new string(c: '•', count: Text.Length) : Text;

    public string Hint
    {
        get => _hint;
        set => SetPaint(field: ref _hint, value: value);
    }

    public Action<string>? OnChanged { get; set; }

    /// <summary>Fired with the current text when Enter/Return is pressed (commit affordance).</summary>
    public Action<string>? OnSubmitted { get; set; }

    [Obsolete("Renamed — use OnSubmitted.")]
    public Action<string>? OnSubmit
    {
        get => OnSubmitted;
        set => OnSubmitted = value;
    }

    /// <summary>
    ///     Fired when focus is gained (true) or lost (false). Used by composite controls like
    ///     autosuggest.
    /// </summary>
    public Action<bool>? OnFocusChange { get; set; }

    public float Height { get; set; } = ControlMetrics.RegularHeight;
    public float MinWidth { get; set; } = 140f;

    /// <summary>When true, the field is selectable/copyable but not editable (no caret, no text input).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    ///     When false the field paints no surface fill, hairline border, or focus ring — it renders only
    ///     text + caret. Composite hosts that own the chrome (e.g. <see cref="SearchField" />'s capsule)
    ///     set this so the inner field doesn't draw a second box/border/ring inside theirs.
    /// </summary>
    public bool ShowBackground { get; set; } = true;

    /// <summary>
    ///     When true the field accepts newlines and grows vertically between <see cref="MinLines" /> and
    ///     <see cref="MaxLines" /> (scrolling past that). Enter inserts a line break; ⌘/Ctrl+Enter fires
    ///     <see cref="OnSubmitted" />. Single-line (the default) keeps the original fixed-height
    ///     behaviour and Enter submits. For code/syntax editing prefer <see cref="CodeEditor" />.
    /// </summary>
    public bool Multiline
    {
        get => _multiline;
        set => SetLayout(field: ref _multiline, value: value);
    }

    /// <summary>
    ///     Minimum visible rows in <see cref="Multiline" /> mode (the field never shrinks below
    ///     this).
    /// </summary>
    public int MinLines { get; set; } = 3;

    /// <summary>Maximum visible rows before the field scrolls vertically in <see cref="Multiline" /> mode.</summary>
    public int MaxLines { get; set; } = 8;

    /// <summary>Accessible name announced for the field (falls back to <see cref="Hint" />).</summary>
    public string? SemanticsLabel { get; set; }

    public override bool Focusable => true;

    /// <summary>The field owns the arrow keys for caret movement, so they're never repurposed for focus.</summary>
    public override bool HandlesDirectionalKeys => true;

    // ── Selection helpers ─────────────────────────────────────────────────────

    private bool HasSelection => _selectionAnchor >= 0 && _selectionAnchor != _cursorPos;
    private int SelectionMin => Math.Min(val1: _selectionAnchor, val2: _cursorPos);
    private int SelectionMax => Math.Max(val1: _selectionAnchor, val2: _cursorPos);
    private string SelectedText => HasSelection ? Text[SelectionMin..SelectionMax] : string.Empty;

    // ── Multi-line line model ───────────────────────────────────────────────────
    // The caret stays a single flat index into Text (so selection / clipboard / grapheme logic is
    // shared with the single-line path); line geometry is derived on demand from the '\n' positions.

    private int LineCount
    {
        get
        {
            int n = 1;
            for (int i = 0; i < Text.Length; i++)
            {
                if (Text[i] == '\n')
                    n++;
            }

            return n;
        }
    }

    /// <summary>
    ///     Follows an external controller write (<c>controller.Text = …</c> / <c>Clear()</c>) into the
    ///     field. Edits typed in the field flow back via
    ///     <see cref="TextEditingController.SetTextSilently" />,
    ///     so this never re-enters.
    /// </summary>
    private void OnControllerChanged(string value) => Text = value;

    public override MouseCursor? GetCursor(Offset point) => MouseCursor.Text;

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.TextField;
        config.Label = SemanticsLabel ?? (Hint.Length > 0 ? Hint : null);
        config.Value = Text;
        config.Actions = SemanticsAction.Focus |
                         (ReadOnly ? SemanticsAction.None : SemanticsAction.SetValue);
        config.AddFlag(SemanticsFlags.Focusable)
            .AddFlag(flag: SemanticsFlags.Focused, on: Focused)
            .AddFlag(flag: SemanticsFlags.ReadOnly, on: ReadOnly)
            .AddFlag(flag: SemanticsFlags.Multiline, on: Multiline);
    }

    private void ClearSelection() => _selectionAnchor = -1;

    /// <summary>Make the caret solid now; it resumes blinking after the dwell interval.</summary>
    private void ResetCaretBlink() => _caretBase = App.Active?.Time ?? 0f;

    private void SelectAll()
    {
        _selectionAnchor = 0;
        _cursorPos = Text.Length;
        MarkNeedsPaint();
    }

    private void DeleteSelection()
    {
        if (ReadOnly || !HasSelection) return;
        int min = SelectionMin;
        int max = SelectionMax;
        Text = Text[..min] + Text[max..];
        _cursorPos = min;
        ClearSelection();
        OnChanged?.Invoke(Text);
        MarkNeedsPaint();
    }

    private void MoveCursor(int newPos, bool extendSelection)
    {
        if (extendSelection)
        {
            if (_selectionAnchor < 0) _selectionAnchor = _cursorPos;
        }
        else
            ClearSelection();

        _cursorPos = TextNavigation.GraphemeBoundaryAtOrBefore(text: Text, index: newPos);
        MarkNeedsPaint();
    }

    /// <summary>(line, column) of a flat index — column is the UTF-16 offset within its physical line.</summary>
    private (int Line, int Col) LineColAt(int index)
    {
        index = Math.Clamp(value: index, min: 0, max: Text.Length);
        int line = 0, lineStart = 0;
        for (int i = 0; i < index; i++)
        {
            if (Text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, index - lineStart);
    }

    /// <summary>Flat index of the first character of <paramref name="line" /> (clamped to the document).</summary>
    private int LineStartIndex(int line)
    {
        if (line <= 0) return 0;
        int seen = 0, i = 0;
        for (; i < Text.Length; i++)
        {
            if (Text[i] == '\n' && ++seen == line)
                return i + 1;
        }

        return Text.Length;
    }

    /// <summary>Exclusive end index of <paramref name="line" /> (the '\n' or end of text).</summary>
    private int LineEndIndex(int line)
    {
        int start = LineStartIndex(line);
        int nl = Text.IndexOf(value: '\n', startIndex: start);
        return nl < 0 ? Text.Length : nl;
    }

    /// <summary>Flat index for a (line, column), clamping the column to that line's length.</summary>
    private int IndexAt(int line, int col)
    {
        line = Math.Clamp(value: line, min: 0, max: LineCount - 1);
        int start = LineStartIndex(line);
        int end = LineEndIndex(line);
        return start + Math.Clamp(value: col, min: 0, max: end - start);
    }

    // ── Text measurement ──────────────────────────────────────────────────────

    /// <summary>
    ///     Build/refresh the cumulative prefix-advance table for the current Text/font. All caret,
    ///     selection and click-hit math reads from this one source so the painted caret and the click
    ///     hit-test never disagree (they previously re-measured prefixes independently). Rebuilds lazily
    ///     only when Text or font size changes, so external Text mutations need no explicit invalidation.
    /// </summary>
    private void EnsureAdvances(float fs)
    {
        string text = Visible;
        if (_advText == text && Math.Abs(_advFs - fs) < 0.01f &&
            _adv.Length == text.Length + 1) return;
        _advText = text;
        _advFs = fs;
        _adv = new float[text.Length + 1];
        int[] boundaries = TextNavigation.GraphemeBoundaries(text);
        for (int i = 1; i < boundaries.Length; i++)
        {
            int previous = boundaries[i - 1];
            int current = boundaries[i];
            _adv[current] = _adv[previous] + TextMeasure.Width(
                text: text[previous..current],
                fontSize: fs
            );
        }
    }

    private TextLayout? EnsureTextLayout(float fs)
    {
        // Native text layouts are shaped + cached against the MAIN window's glyph atlas; in a
        // secondary OS window the handle resolves against the wrong GpuUi and nothing renders.
        // Return null there so every caller takes its AddText / measured-advance fallback.
        if (Owner is { NativeWindow: not null }) return null;

        string text = Visible;
        if (_layoutText == text && Math.Abs(_layoutFs - fs) < 0.01f) return _layout;
        _layout?.Dispose();
        _layout = null;
        _layoutText = text;
        _layoutFs = fs;
        var engine = ZigoteEngine.Instance;
        if (engine is null || engine.Handle == 0 || text.Length == 0) return null;
        try
        {
            _layout = engine.CreateTextLayout(text: text, fontSize: fs);
        }
        catch
        {
            // Headless/unsupported native backends retain the linear grapheme fallback.
        }

        return _layout;
    }

    /// <summary>Pixel offset of the caret for a given character index, using real text metrics.</summary>
    private float CaretX(int index)
    {
        index = TextNavigation.GraphemeBoundaryAtOrBefore(text: Visible, index: index);
        var layout = EnsureTextLayout(_theme.FontSizeBody);
        if (layout is not null && layout.TryGetCaretPosition(
                textOffset: index,
                position: out var position,
                height: out _
            ))
            return position.X;
        EnsureAdvances(_theme.FontSizeBody);
        return _adv[index];
    }

    private int PositionAtX(float screenX)
    {
        EnsureAdvances(_theme.FontSizeBody);
        float relX = screenX - (Bounds.X + Spacing.Sm) + _scrollX;
        if (relX <= 0f || Text.Length == 0) return 0;
        var layout = EnsureTextLayout(_theme.FontSizeBody);
        if (layout is not null) return layout.HitTest(relX);

        // Pick the gap nearest the click via the midpoint between adjacent glyph advances.
        int[] boundaries = TextNavigation.GraphemeBoundaries(Visible);
        for (int i = 1; i < boundaries.Length; i++)
        {
            int previous = boundaries[i - 1];
            int current = boundaries[i];
            if (relX < (_adv[previous] + _adv[current]) / 2f)
                return previous;
        }

        return Text.Length;
    }

    /// <summary>
    ///     Keep the caret inside the padded content area with a small margin on BOTH edges, and clamp
    ///     the scroll so short text that fits is never pushed off-origin (the "stale scroll leaves the
    ///     caret in the wrong place" bug). Runs before paint and after any caret-moving input so the
    ///     painted offset and click hit-testing agree within a frame.
    /// </summary>
    private void UpdateScroll()
    {
        if (Bounds.Width <= 0f) return;
        float fs = _theme.FontSizeBody;
        float padX = Spacing.Sm;
        var layout = EnsureTextLayout(fs);
        if (layout is null) EnsureAdvances(fs);
        float caretX = CaretX(_cursorPos);
        float inner = Bounds.Width - (padX * 2f);
        float total = layout?.Measure().Width ?? _adv[Text.Length];
        float old = _scrollX;
        if (caretX - _scrollX > inner - CaretMargin) _scrollX = caretX - inner + CaretMargin;
        else if (caretX - _scrollX < CaretMargin)
            _scrollX = Math.Max(val1: 0f, val2: caretX - CaretMargin);
        _scrollX = Math.Clamp(
            value: _scrollX,
            min: 0f,
            max: Math.Max(val1: 0f, val2: total - inner)
        );
        if (Math.Abs(_scrollX - old) > 0.01f) MarkNeedsPaint();
    }

    // ── Clipboard actions ─────────────────────────────────────────────────────

    private void CopyAction()
    {
        if (HasSelection)
            ZigoteEngine.Instance?.SetClipboard(SelectedText);
    }

    private void CutAction()
    {
        if (ReadOnly)
        {
            CopyAction(); // readonly: behave as Copy
            return;
        }

        CopyAction();
        DeleteSelection();
    }

    private void PasteAction()
    {
        if (ReadOnly) return;
        string pasted = ZigoteEngine.Instance?.GetClipboard() ?? string.Empty;
        if (!Multiline)
        {
            pasted = pasted.Replace(oldValue: "\r", newValue: "")
                .Replace(oldValue: "\n", newValue: " ");
        }

        if (pasted.Length == 0) return;
        if (HasSelection) DeleteSelection();
        Text = Text[.._cursorPos] + pasted + Text[_cursorPos..];
        _cursorPos += pasted.Length;
        ClearSelection();
        OnChanged?.Invoke(Text);
        MarkNeedsPaint();
    }

    // ── Widget overrides ──────────────────────────────────────────────────────

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: Text,
            value2: Focused,
            value3: _hovered,
            value4: _cursorPos,
            value5: _selectionAnchor
        );
    }

    public override void Detach()
    {
        base.Detach();
        if (_controller is not null) _controller.Changed -= OnControllerChanged;
        _layout?.Dispose();
        _layout = null;
    }

    private float LineHeightPx(float fs) => fs * _theme.LineHeight;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        if (!Multiline)
        {
            // A form field is the primary target on a phone; 28pt is too shallow to tap reliably.
            // (A tight parent constraint still wins, so composed hosts keep their own geometry.)
            _size = c.Constrain(new Size(width: MinWidth, height: TouchMetrics.AtLeast(Height)));
            return _size;
        }

        float fs = _theme.FontSizeBody;
        int rows = Math.Clamp(
            value: LineCount,
            min: Math.Max(val1: 1, val2: MinLines),
            max: Math.Max(val1: MinLines, val2: MaxLines)
        );
        float height = (rows * LineHeightPx(fs)) + (Spacing.Xs * 2f);
        float width = float.IsFinite(c.MaxWidth) ? MathF.Max(x: MinWidth, y: c.MaxWidth) : MinWidth;
        _size = c.Constrain(new Size(width: width, height: height));
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
        if (Multiline)
        {
            PaintMultiline(paint);
            return;
        }

        float radius = _theme.InputRadius;

        // Flat opaque surface with a hairline border. On focus we keep the border subtle
        // and let the focus ring carry the emphasis.
        if (ShowBackground)
        {
            paint.AddRect(bounds: Bounds, color: _theme.Surface, radius: radius);
            paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: radius);
        }

        paint.AddClipStart(Bounds);

        float fs = _theme.FontSizeBody;
        float padX = Spacing.Sm;

        // Keep the caret visible (symmetric margin + clamp); shared with input so offsets agree.
        UpdateScroll();

        float bx = Bounds.X + padX - _scrollX;

        // Vertically centre the text on its measured height.
        float th = TextMeasure.Measure(text: Text.Length > 0 ? Text : "X", fontSize: fs).Height;
        float by = Bounds.Y + ((Bounds.Height - th) / 2f) + (fs * 0.8f);

        // Selection highlight.
        if (HasSelection)
        {
            float selX1 = bx + CaretX(SelectionMin);
            float selX2 = bx + CaretX(SelectionMax);
            paint.AddRect(
                bounds: new Rect(
                    x: selX1,
                    y: Bounds.Y + Spacing.Xs,
                    width: selX2 - selX1,
                    height: Bounds.Height - Spacing.Sm
                ),
                color: _theme.Selection,
                radius: Radii.Xs
            );
        }

        if (_compositionText.Length > 0)
        {
            string display = Text[.._compositionStart] + _compositionText + Text[_compositionEnd..];
            paint.AddText(
                text: display,
                baselineX: bx,
                baselineY: by,
                color: _theme.OnSurface,
                fontSize: fs
            );
            float compositionX = bx + CaretX(_compositionStart);
            float compositionWidth = TextMeasure.Width(text: _compositionText, fontSize: fs);
            paint.AddRect(
                bounds: new Rect(
                    x: compositionX,
                    y: Bounds.Bottom - Spacing.Xs - 1f,
                    width: MathF.Max(x: 1f, y: compositionWidth),
                    height: 1f
                ),
                color: _theme.Primary
            );
        }
        else if (Text.Length > 0)
        {
            var layout = EnsureTextLayout(fs);
            if (layout is { IsValid: true })
            {
                paint.AddTextLayout(
                    handle: layout.Handle,
                    x: bx,
                    y: by,
                    color: _theme.OnSurface
                );
            }
            else
            {
                paint.AddText(
                    text: Visible,
                    baselineX: bx,
                    baselineY: by,
                    color: _theme.OnSurface,
                    fontSize: fs
                );
            }
        }
        else if (Hint.Length > 0)
        {
            paint.AddText(
                text: Hint,
                baselineX: bx,
                baselineY: by,
                color: _theme.Hint,
                fontSize: fs
            );
        }

        // Blinking caret — solid for ~0.5 s after activity (reset via _caretBase), then blinks.
        // Read-only fields stay selectable/copyable but show no caret.
        if (Focused && !ReadOnly)
        {
            float time = App.Active?.Time ?? 0f;
            if ((time - _caretBase) % 1.06f < 0.6f)
            {
                float cx = bx + CaretX(_cursorPos);
                if (_compositionText.Length > 0)
                {
                    int selectedPrefix = TextNavigation.GraphemeBoundaryAtOrBefore(
                        text: _compositionText,
                        index: _compositionSelectionStart + _compositionSelectionLength
                    );
                    cx = bx + CaretX(_compositionStart) +
                         TextMeasure.Width(text: _compositionText[..selectedPrefix], fontSize: fs);
                }

                paint.AddRect(
                    bounds: new Rect(
                        x: cx,
                        y: Bounds.Y + Spacing.Xs,
                        width: 1.5f,
                        height: Bounds.Height - Spacing.Sm
                    ),
                    color: _theme.Primary
                );
            }

            float imeX = bx + CaretX(_cursorPos);
            if (_compositionText.Length > 0)
            {
                int selectedPrefix = TextNavigation.GraphemeBoundaryAtOrBefore(
                    text: _compositionText,
                    index: _compositionSelectionStart + _compositionSelectionLength
                );
                imeX = bx + CaretX(_compositionStart) +
                       TextMeasure.Width(text: _compositionText[..selectedPrefix], fontSize: fs);
            }

            ZigoteEngine.Instance?.SetTextInputArea(
                new Rect(
                    x: imeX,
                    y: Bounds.Y + Spacing.Xs,
                    width: 1.5f,
                    height: Bounds.Height - Spacing.Sm
                )
            );
        }

        paint.AddClipEnd();

        if (Focused && ShowBackground)
            paint.AddFocusRing(bounds: Bounds, radius: radius, theme: _theme);
    }

    // ── Multi-line paint ────────────────────────────────────────────────────────

    private static int CountLines(string s)
    {
        int n = 1;
        foreach (char ch in s)
        {
            if (ch == '\n')
                n++;
        }

        return n;
    }

    private static (int Line, int Col) DisplayLineCol(string s, int index)
    {
        index = Math.Clamp(value: index, min: 0, max: s.Length);
        int line = 0, start = 0;
        for (int i = 0; i < index; i++)
        {
            if (s[i] == '\n')
            {
                line++;
                start = i + 1;
            }
        }

        return (line, index - start);
    }

    private static string DisplayLine(string s, int line)
    {
        int start = 0, cur = 0;
        while (cur < line)
        {
            int nl = s.IndexOf(value: '\n', startIndex: start);
            if (nl < 0) return string.Empty;
            start = nl + 1;
            cur++;
        }

        int end = s.IndexOf(value: '\n', startIndex: start);
        if (end < 0) end = s.Length;
        return s[start..end];
    }

    private void PaintMultiline(PaintList paint)
    {
        float radius = _theme.InputRadius;
        if (ShowBackground)
        {
            paint.AddRect(bounds: Bounds, color: _theme.Surface, radius: radius);
            paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: radius);
        }

        float fs = _theme.FontSizeBody;
        float lineH = LineHeightPx(fs);
        float padX = Spacing.Sm;
        float padY = Spacing.Xs;
        float innerX = Bounds.X + padX;
        float innerTop = Bounds.Y + padY;
        float innerW = MathF.Max(x: 1f, y: Bounds.Width - (padX * 2f));
        float innerH = MathF.Max(x: 1f, y: Bounds.Height - (padY * 2f));

        // Splice the active IME pre-edit into the rendered string; the caret index follows the
        // composition's selected end so the candidate window anchors correctly.
        bool composing = _compositionText.Length > 0;
        string display = composing
            ? Text[.._compositionStart] + _compositionText + Text[_compositionEnd..]
            : Text;

        int caretDisplay;
        if (composing)
        {
            int selectedPrefix = TextNavigation.GraphemeBoundaryAtOrBefore(
                text: _compositionText,
                index: _compositionSelectionStart + _compositionSelectionLength
            );
            caretDisplay = _compositionStart + selectedPrefix;
        }
        else
            caretDisplay = _cursorPos;

        (int caretLine, int caretCol) = DisplayLineCol(s: display, index: caretDisplay);
        string caretLineText = DisplayLine(s: display, line: caretLine);
        float caretX = TextMeasure.Width(
            text: caretLineText[..Math.Min(val1: caretCol, val2: caretLineText.Length)],
            fontSize: fs
        );

        int totalLines = CountLines(display);

        // Keep the caret line and column inside the viewport.
        float caretTop = caretLine * lineH;
        if (caretTop - _scrollY < 0f) _scrollY = caretTop;
        else if (caretTop + lineH - _scrollY > innerH) _scrollY = caretTop + lineH - innerH;
        _scrollY = Math.Clamp(
            value: _scrollY,
            min: 0f,
            max: MathF.Max(x: 0f, y: (totalLines * lineH) - innerH)
        );

        if (caretX - _scrollX > innerW - CaretMargin) _scrollX = caretX - innerW + CaretMargin;
        else if (caretX - _scrollX < CaretMargin)
            _scrollX = MathF.Max(x: 0f, y: caretX - CaretMargin);
        _scrollX = MathF.Max(x: 0f, y: _scrollX);

        paint.AddClipStart(
            new Rect(
                x: innerX,
                y: innerTop,
                width: innerW,
                height: innerH
            )
        );

        int firstVisible = Math.Max(val1: 0, val2: (int)(_scrollY / lineH));
        int lastVisible = Math.Min(
            val1: totalLines - 1,
            val2: (int)((_scrollY + innerH) / lineH) + 1
        );

        if (!composing && HasSelection)
        {
            (int sLine, int sCol) = LineColAt(SelectionMin);
            (int eLine, int eCol) = LineColAt(SelectionMax);
            for (int ln = Math.Max(val1: firstVisible, val2: sLine);
                 ln <= Math.Min(val1: lastVisible, val2: eLine);
                 ln++)
            {
                string lt = DisplayLine(s: Text, line: ln);
                int c0 = ln == sLine ? sCol : 0;
                int c1 = ln == eLine ? eCol : lt.Length;
                float x0 = innerX - _scrollX + TextMeasure.Width(
                    text: lt[..Math.Min(val1: c0, val2: lt.Length)],
                    fontSize: fs
                );
                float x1 = innerX - _scrollX + TextMeasure.Width(
                    text: lt[..Math.Min(val1: c1, val2: lt.Length)],
                    fontSize: fs
                );
                if (ln < eLine) x1 += fs * 0.3f; // show the selected line break
                float y = innerTop + (ln * lineH) - _scrollY;
                paint.AddRect(
                    bounds: new Rect(
                        x: x0,
                        y: y,
                        width: MathF.Max(x: 1f, y: x1 - x0),
                        height: lineH
                    ),
                    color: _theme.Selection,
                    radius: Radii.Xs
                );
            }
        }

        if (display.Length == 0 && Hint.Length > 0)
        {
            paint.AddText(
                text: Hint,
                baselineX: innerX - _scrollX,
                baselineY: innerTop + (fs * 0.8f),
                color: _theme.Hint,
                fontSize: fs
            );
        }
        else
        {
            for (int ln = firstVisible; ln <= lastVisible; ln++)
            {
                string lt = DisplayLine(s: display, line: ln);
                if (lt.Length == 0) continue;
                float y = innerTop + (ln * lineH) - _scrollY + (fs * 0.8f);
                paint.AddText(
                    text: lt,
                    baselineX: innerX - _scrollX,
                    baselineY: y,
                    color: _theme.OnSurface,
                    fontSize: fs
                );
            }
        }

        if (composing)
        {
            (int cLine, int cCol) = DisplayLineCol(s: display, index: _compositionStart);
            string clt = DisplayLine(s: display, line: cLine);
            float ux0 = innerX - _scrollX + TextMeasure.Width(
                text: clt[..Math.Min(val1: cCol, val2: clt.Length)],
                fontSize: fs
            );
            float uw = TextMeasure.Width(text: _compositionText, fontSize: fs);
            float uy = innerTop + (cLine * lineH) - _scrollY + lineH - 2f;
            paint.AddRect(
                bounds: new Rect(
                    x: ux0,
                    y: uy,
                    width: MathF.Max(x: 1f, y: uw),
                    height: 1f
                ),
                color: _theme.Primary
            );
        }

        if (Focused && !ReadOnly)
        {
            float time = App.Active?.Time ?? 0f;
            float cx = innerX - _scrollX + caretX;
            float cy = innerTop + (caretLine * lineH) - _scrollY;
            if ((time - _caretBase) % 1.06f < 0.6f)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: cx,
                        y: cy + 1f,
                        width: 1.5f,
                        height: lineH - 2f
                    ),
                    color: _theme.Primary
                );
            }

            ZigoteEngine.Instance?.SetTextInputArea(
                new Rect(
                    x: cx,
                    y: cy + 1f,
                    width: 1.5f,
                    height: lineH - 2f
                )
            );
        }

        paint.AddClipEnd();

        if (Focused && ShowBackground)
            paint.AddFocusRing(bounds: Bounds, radius: radius, theme: _theme);
    }

    // ── Pointer input ─────────────────────────────────────────────────────────

    public override void OnPointerEnter()
    {
        if (_hovered) return;
        _hovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (!_hovered) return;
        _hovered = false;
        MarkNeedsPaint();
    }

    /// <summary>Flat caret index nearest a screen point — line + column in multi-line, x-only otherwise.</summary>
    private int IndexAtPoint(Offset p)
    {
        if (!Multiline) return PositionAtX(p.X);
        float fs = _theme.FontSizeBody;
        float lineH = LineHeightPx(fs);
        float relY = p.Y - (Bounds.Y + Spacing.Xs) + _scrollY;
        int line = Math.Clamp(value: (int)(relY / lineH), min: 0, max: LineCount - 1);
        string lt = DisplayLine(s: Text, line: line);
        float relX = p.X - (Bounds.X + Spacing.Sm) + _scrollX;
        return LineStartIndex(line) + ColAtX(line: lt, x: relX, fs: fs);
    }

    private static int ColAtX(string line, float x, float fs)
    {
        if (x <= 0f || line.Length == 0) return 0;
        int[] boundaries = TextNavigation.GraphemeBoundaries(line);
        float prev = 0f;
        for (int i = 1; i < boundaries.Length; i++)
        {
            int c = boundaries[i];
            float w = TextMeasure.Width(text: line[..c], fontSize: fs);
            if (x < (prev + w) / 2f) return boundaries[i - 1];
            prev = w;
        }

        return line.Length;
    }

    public override void OnPointerDown(Offset point)
    {
        int pos = IndexAtPoint(point);
        _desiredCol = -1;

        // Double-click selects the word under the cursor.
        float now = App.Active?.Time ?? 0f;
        if (now - _lastClickTime < DoubleClickSeconds && Math.Abs(pos - _lastClickPos) <= 1)
        {
            _lastClickTime = -1f; // consume so a third click doesn't chain
            SelectWordAt(pos);
            return;
        }

        _lastClickTime = now;
        _lastClickPos = pos;

        _selectionAnchor = pos;
        _cursorPos = pos;
        _isDragging = true;
        ResetCaretBlink();
        if (!Multiline) UpdateScroll();
        MarkNeedsPaint();
    }

    /// <summary>Select the word (or single non-word char) at the given character index.</summary>
    private void SelectWordAt(int pos)
    {
        (int start, int end) = TextNavigation.WordAt(text: Text, pos: pos);

        _isDragging = false;
        if (start == end)
        {
            ClearSelection();
            _cursorPos = Math.Clamp(value: pos, min: 0, max: Text.Length);
            MarkNeedsPaint();
            return;
        }

        _selectionAnchor = start;
        _cursorPos = end;
        ResetCaretBlink();
        UpdateScroll();
        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        if (!_isDragging) return;
        _cursorPos = IndexAtPoint(point);
        if (!Multiline) UpdateScroll();
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        _isDragging = false;
        if (_selectionAnchor == _cursorPos) ClearSelection();
    }

    public override void OnRightClick(Offset point)
    {
        _contextMenu ??= new ContextMenu();
        _contextMenu.Items.Clear();
        _contextMenu.Items.Add(
            new ContextMenuItem(
                Label: "Cut",
                OnSelect: HasSelection && !ReadOnly ? CutAction : null,
                Shortcut: "⌘X"
            )
        );
        _contextMenu.Items.Add(
            new ContextMenuItem(
                Label: "Copy",
                OnSelect: HasSelection ? CopyAction : null,
                Shortcut: "⌘C"
            )
        );
        _contextMenu.Items.Add(
            new ContextMenuItem(
                Label: "Paste",
                OnSelect: ReadOnly ? null : PasteAction,
                Shortcut: "⌘V"
            )
        );
        _contextMenu.Items.Add(new ContextMenuItem(Label: "", OnSelect: null, Separator: true));
        _contextMenu.Items.Add(
            new ContextMenuItem(Label: "Select All", OnSelect: SelectAll, Shortcut: "⌘A")
        );
        _contextMenu.ShowAt(point);
    }

    // ── Keyboard input ────────────────────────────────────────────────────────

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        ResetCaretBlink();

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

        if (scancode is scReturn or scKpEnter)
        {
            // Multi-line: Enter inserts a break; ⌘/Ctrl+Enter commits. Single-line: Enter commits.
            if (Multiline && !mods.HasCommand())
            {
                InsertNewline();
                return;
            }

            OnSubmitted?.Invoke(Text);
            return;
        }

        bool shift = mods.HasFlag(Modifiers.Shift);
        bool cmd = mods.HasCommand(); // Ctrl or ⌘

        if (cmd)
        {
            switch (char.ToLower(keyChar))
            {
                case 'a':
                    SelectAll();
                    return;
                case 'c':
                    CopyAction();
                    return;
                case 'x':
                    CutAction(); // self-guards ReadOnly (copies only)
                    return;
                case 'v':
                    PasteAction(); // self-guards ReadOnly
                    return;
            }
        }

        switch (scancode)
        {
            case scBackspace:
                if (ReadOnly) break;
                if (HasSelection)
                {
                    DeleteSelection();
                    break;
                }

                if (_cursorPos > 0)
                {
                    int previous = TextNavigation.PreviousGraphemeBoundary(
                        text: Text,
                        index: _cursorPos
                    );
                    Text = Text[..previous] + Text[_cursorPos..];
                    _cursorPos = previous;
                    OnChanged?.Invoke(Text);
                    MarkNeedsPaint();
                }

                break;

            case scDelete:
                if (ReadOnly) break;
                if (HasSelection)
                {
                    DeleteSelection();
                    break;
                }

                if (_cursorPos < Text.Length)
                {
                    int next = TextNavigation.NextGraphemeBoundary(text: Text, index: _cursorPos);
                    Text = Text[.._cursorPos] + Text[next..];
                    OnChanged?.Invoke(Text);
                    MarkNeedsPaint();
                }

                break;

            case scLeft:
                _desiredCol = -1;
                if (!shift && HasSelection)
                {
                    _cursorPos = SelectionMin;
                    ClearSelection();
                    MarkNeedsPaint();
                    break;
                }

                MoveCursor(
                    newPos: TextNavigation.PreviousGraphemeBoundary(text: Text, index: _cursorPos),
                    extendSelection: shift
                );
                break;

            case scRight:
                _desiredCol = -1;
                if (!shift && HasSelection)
                {
                    _cursorPos = SelectionMax;
                    ClearSelection();
                    MarkNeedsPaint();
                    break;
                }

                MoveCursor(
                    newPos: TextNavigation.NextGraphemeBoundary(text: Text, index: _cursorPos),
                    extendSelection: shift
                );
                break;

            case scUp when Multiline:
                MoveVertical(dir: -1, extend: shift);
                break;

            case scDown when Multiline:
                MoveVertical(dir: 1, extend: shift);
                break;

            case scHome:
                _desiredCol = -1;
                if (Multiline && !cmd)
                {
                    MoveCursor(
                        newPos: LineStartIndex(LineColAt(_cursorPos).Line),
                        extendSelection: shift
                    );
                }
                else MoveCursor(newPos: 0, extendSelection: shift);

                break;

            case scEnd:
                _desiredCol = -1;
                if (Multiline && !cmd)
                {
                    MoveCursor(
                        newPos: LineEndIndex(LineColAt(_cursorPos).Line),
                        extendSelection: shift
                    );
                }
                else MoveCursor(newPos: Text.Length, extendSelection: shift);

                break;
        }

        if (!Multiline) UpdateScroll();
    }

    private void InsertNewline()
    {
        if (ReadOnly) return;
        if (HasSelection) DeleteSelection();
        Text = Text[.._cursorPos] + "\n" + Text[_cursorPos..];
        _cursorPos += 1;
        _desiredCol = -1;
        ResetCaretBlink();
        OnChanged?.Invoke(Text);
        MarkNeedsPaint();
    }

    /// <summary>
    ///     Move the caret up/down a line, keeping a sticky target column (<see cref="_desiredCol" />
    ///     ).
    /// </summary>
    private void MoveVertical(int dir, bool extend)
    {
        (int line, int col) = LineColAt(_cursorPos);
        if (_desiredCol < 0) _desiredCol = col;

        int target = line + dir;
        int newIndex;
        if (target < 0) newIndex = 0;
        else if (target >= LineCount) newIndex = Text.Length;
        else newIndex = IndexAt(line: target, col: _desiredCol);

        if (extend)
        {
            if (_selectionAnchor < 0) _selectionAnchor = _cursorPos;
        }
        else
            ClearSelection();

        _cursorPos = TextNavigation.GraphemeBoundaryAtOrBefore(text: Text, index: newIndex);
        MarkNeedsPaint();
    }

    protected override void OnFocusChanged(bool focused)
    {
        OnFocusChange?.Invoke(focused);
        MarkNeedsPaint();
    }

    public override void OnTextInput(string text)
    {
        if (ReadOnly) return;
        if (_compositionText.Length > 0)
        {
            Text = Text[.._compositionStart] + text + Text[_compositionEnd..];
            _cursorPos = _compositionStart + text.Length;
            _compositionText = string.Empty;
            ClearSelection();
        }
        else
        {
            if (HasSelection) DeleteSelection();
            Text = Text[.._cursorPos] + text + Text[_cursorPos..];
            _cursorPos += text.Length;
        }

        _desiredCol = -1;
        ResetCaretBlink();
        OnChanged?.Invoke(Text);
        if (!Multiline) UpdateScroll();
        MarkNeedsPaint();
    }

    public override void OnTextComposition(string text, int selectionStart, int selectionLength)
    {
        if (ReadOnly) return;
        if (_compositionText.Length == 0)
        {
            _compositionStart = HasSelection ? SelectionMin : _cursorPos;
            _compositionEnd = HasSelection ? SelectionMax : _cursorPos;
        }

        _compositionText = text;
        _compositionSelectionStart = Math.Clamp(value: selectionStart, min: 0, max: text.Length);
        _compositionSelectionLength = Math.Clamp(
            value: selectionLength,
            min: 0,
            max: text.Length - _compositionSelectionStart
        );
        if (text.Length == 0)
            _compositionStart = _compositionEnd = _cursorPos;
        ResetCaretBlink();
        MarkNeedsPaint();
    }
}
