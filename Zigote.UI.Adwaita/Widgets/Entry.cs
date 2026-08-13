using Zigote.UI.Material;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwEntry — the GNOME text entry: a translucent <see cref="ThemeData.Fill3" /> box with no
///     border at rest and a 2px accent inner border when focused. Text editing is delegated to the
///     framework <see cref="TextField" /> (chrome-less); this widget owns only the Adwaita look.
/// </summary>
public class AdwEntry : Widget
{
    protected readonly TextField Field;
    protected ThemeData Theme = ThemeData.Dark;
    private bool _compact;
    private bool _enabled = true;
    private Size _size;
    private float? _width;

    public AdwEntry()
    {
        Field = new EntryField(this) {
            Height = AdwMetrics.EntryHeight,
            MinWidth = 60f,
            // This widget owns the box + focus border; the inner field renders text only.
            ShowBackground = false,
        };
        Field.OnChanged = s =>
        {
            OnChanged?.Invoke(s);
            OnTextChanged();
        };
        Field.OnSubmitted = s => OnSubmitted?.Invoke(s);
    }

    public string Text
    {
        get => Field.Text;
        set
        {
            Field.Text = value;
            OnTextChanged();
        }
    }

    public string Placeholder
    {
        get => Field.Hint;
        set => Field.Hint = value;
    }

    public Action<string>? OnChanged { get; set; }
    public Action<string>? OnSubmitted { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Field.ReadOnly = !value;
            MarkNeedsPaint();
        }
    }

    /// <summary>
    ///     Fixed width; null fills the available width. Re-lays out on change — it is read in
    ///     <see cref="Measure" />, and nothing else would schedule the pass that picks it up.
    /// </summary>
    public float? Width
    {
        get => _width;
        set => SetLayout(ref _width, value);
    }

    /// <summary>
    ///     Shrink to <see cref="AdwMetrics.CompactControlHeight" /> — the density a property grid
    ///     runs at, where a full-height entry per row would double the scroll length. Everything
    ///     else about the entry is unchanged, so a compact row still reads as Adwaita.
    /// </summary>
    public bool Compact
    {
        get => _compact;
        set => SetLayout(ref _compact, value);
    }

    /// <summary>The entry's resolved outer height; decorations inset by it to stay square.</summary>
    protected float BoxHeight =>
        Compact ? AdwMetrics.CompactControlHeight : AdwMetrics.EntryHeight;

    /// <summary>
    ///     Put the caret in this entry — the target of a Ctrl+F, a search bar revealing itself, or a
    ///     form focusing its first field. The editable widget is the inner field, so focusing the
    ///     entry itself would leave the caret nowhere. No-op while unmounted.
    /// </summary>
    public void Focus()
    {
        Owner?.RequestFocus(Field);
    }

    /// <summary>Corner radius — subclasses restyle (the search entry is a pill).</summary>
    protected virtual float Radius => AdwMetrics.ControlRadius;

    /// <summary>Horizontal space reserved before the text (leading icon area).</summary>
    protected virtual float LeadingInset => 0f;

    /// <summary>Horizontal space reserved after the text (trailing button area).</summary>
    protected virtual float TrailingInset => 0f;

    /// <summary>Called whenever the text changes (edit, external set, clear).</summary>
    protected virtual void OnTextChanged()
    {
    }

    /// <summary>Paint leading/trailing decorations (icons, buttons) over the box fill.</summary>
    protected virtual void PaintDecorations(PaintList paint)
    {
    }

    /// <summary>Does <paramref name="point" /> hit a decoration this widget handles itself?</summary>
    protected virtual bool HitsDecoration(Offset point)
    {
        return false;
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        Theme = ThemeProvider.Of(BuildContext.Current);
        var w = Width ?? (float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f);
        _size = c.Constrain(new Size(w, BoxHeight));

        var inner = MathF.Max(0f, _size.Width - LeadingInset - TrailingInset);
        Field.Height = _size.Height;
        Field.MinWidth = inner;
        Field.Measure(
            new Constraints(
                inner,
                inner,
                _size.Height,
                _size.Height
            )
        );
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
        Field.Layout(new Offset(origin.X + LeadingInset, origin.Y));
    }

    public override void Paint(PaintList paint)
    {
        if (!Enabled) paint.PushAlpha(AdwStyle.DisabledOpacity);

        // `entry { background-color: $button_color }` — an entry carries the same currentColor 10%
        // as a raised button, not a fainter view fill.
        paint.AddRect(Bounds, AdwPalette.For(Theme).ButtonFill, Radius);
        PaintDecorations(paint);
        Field.Paint(paint);

        // The Adwaita focus affordance: `@include focus-ring($focus-state: ':focus-within')` — a
        // 2px inset outline in the standalone accent at 50%, not a solid accent border.
        if (Field.Focused && Enabled)
            paint.AddBorder(
                Bounds,
                Theme.FocusRing,
                Radius,
                Theme.FocusRingWidth
            );

        if (!Enabled) paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Enabled || !Bounds.Contains(point.X, point.Y)) return null;
        // Every hit in the box — the text, the gaps, and the decorations outside the field's own
        // bounds — resolves to the field. App blurs whatever it hits unless the hit IS the focused
        // widget, so returning `this` for the reveal/clear buttons used to drop the caret and, on
        // touch, tear down the on-screen keyboard mid-typing. <see cref="EntryField" /> hands the
        // decoration presses back here.
        return Field.HitTest(point) ?? Field;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Field);
    }

    // Keep Tab traversal off the inner field while disabled.
    public override IEnumerable<Widget> GetVisibleChildren()
    {
        return Enabled ? GetChildren() : Array.Empty<Widget>();
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(Field.Text, Field.Focused, Enabled);
    }

    /// <summary>
    ///     The inner editor, with one addition: a press that landed on one of the entry's own
    ///     decorations is handed straight back to the entry instead of moving the caret. Hit-testing
    ///     has to name the field for those presses (see <see cref="HitTest" />), so this is the only
    ///     place left that can run the reveal/clear action.
    /// </summary>
    private sealed class EntryField(AdwEntry owner) : TextField
    {
        public override void OnPointerDown(Offset point)
        {
            if (owner.HitsDecoration(point)) owner.OnPointerDown(point);
            else base.OnPointerDown(point);
        }
    }
}

/// <summary>
///     AdwPasswordEntry — an <see cref="AdwEntry" /> that masks its text and carries a trailing
///     reveal-eye toggle.
/// </summary>
public sealed class AdwPasswordEntry : AdwEntry
{
    private bool _revealed;

    public AdwPasswordEntry()
    {
        Field.Obscure = true;
    }

    protected override float TrailingInset => BoxHeight;

    private Rect RevealBox => new(
        Bounds.Right - BoxHeight,
        Bounds.Y,
        AdwMetrics.EntryHeight,
        Bounds.Height
    );

    protected override void PaintDecorations(PaintList paint)
    {
        Icons.Draw(
            paint,
            _revealed ? Icons.VisibilityOff : Icons.Visibility,
            RevealBox,
            Theme.Hint,
            AdwMetrics.IconSize
        );
    }

    protected override bool HitsDecoration(Offset point)
    {
        return RevealBox.Contains(point.X, point.Y);
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled || !HitsDecoration(point)) return;
        _revealed = !_revealed;
        Field.Obscure = !_revealed;
        MarkNeedsPaint();
    }
}

/// <summary>
///     AdwSearchEntry — a pill-shaped <see cref="AdwEntry" /> with a leading search glyph and a
///     trailing clear button that appears once the field has text.
/// </summary>
public sealed class AdwSearchEntry : AdwEntry
{
    public AdwSearchEntry()
    {
        Placeholder = "Search";
    }

    protected override float Radius => AdwMetrics.Pill;
    protected override float LeadingInset => 28f;
    protected override float TrailingInset => BoxHeight;

    private bool ShowClear => Field.Text.Length > 0;

    private Rect ClearBox => new(
        Bounds.Right - BoxHeight,
        Bounds.Y,
        AdwMetrics.EntryHeight,
        Bounds.Height
    );

    protected override void OnTextChanged()
    {
        MarkNeedsPaint(); // show/hide the clear button
    }

    protected override void PaintDecorations(PaintList paint)
    {
        Icons.Draw(
            paint,
            Icons.Search,
            new Rect(
                Bounds.X,
                Bounds.Y,
                LeadingInset,
                Bounds.Height
            ),
            Theme.Hint,
            AdwMetrics.IconSize
        );
        if (ShowClear)
            Icons.Draw(
                paint,
                Icons.Close,
                ClearBox,
                Theme.Hint,
                AdwMetrics.IconSize
            );
    }

    protected override bool HitsDecoration(Offset point)
    {
        return ShowClear && ClearBox.Contains(point.X, point.Y);
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled || !HitsDecoration(point)) return;
        Field.Text = string.Empty;
        OnChanged?.Invoke(string.Empty);
        MarkNeedsPaint();
    }
}
