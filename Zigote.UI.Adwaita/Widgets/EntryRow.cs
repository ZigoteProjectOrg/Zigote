using Zigote.Core.Animation;
using Zigote.UI.Material;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwEntryRow — the libadwaita entry row: the whole row is the input. The title sits as the
///     placeholder (14px) while the row is empty and unfocused, and floats to a small dim caption
///     above the value once the row is focused or non-empty.
/// </summary>
public sealed class AdwEntryRow : ComposedWidget
{
    private Label? _caption;
    private SizedBox? _captionBox;
    private TextField? _field;
    private bool _floating;
    private bool _obscure;
    private string _text;
    private string _title;
    private Widget? _suffix;

    public AdwEntryRow(string title = "", string text = "", Action<string>? onChanged = null)
    {
        _title = title;
        _text = text;
        OnChanged = onChanged;
    }

    public string Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    public Action<string>? OnChanged { get; set; }

    public string Text
    {
        get => _field?.Text ?? _text;
        set
        {
            _text = value;
            if (_field is null) return;
            _field.Text = value;
            SyncFloat();
        }
    }

    /// <summary>Mask the value (password entry). Written through to the retained field.</summary>
    public bool Obscure
    {
        get => _obscure;
        set
        {
            _obscure = value;
            if (_field is not null) _field.Obscure = value;
        }
    }

    /// <summary>
    ///     Optional trailing widget (the password reveal eye, an apply or copy button). It lives
    ///     inside the row's padding, so callers must not bolt one on outside.
    ///     ponytail: no show-apply-button state machine; the slot is enough.
    /// </summary>
    public Widget? Suffix
    {
        get => _suffix;
        set => this.Set(ref _suffix, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        _field = new TextField(
            onChanged: v =>
            {
                _text = v;
                OnChanged?.Invoke(v);
                SyncFloat();
            }
        ) {
            ShowBackground = false,
            Text = _text,
            Obscure = _obscure,
        };
        _field.OnFocusChange = _ => SyncFloat();

        _caption = new Label("", AdwTypography.Caption, p.DimLabel) {
            MaxLines = 1,
            Overflow = TextOverflow.Ellipsis,
        };
        _captionBox = new SizedBox(height: 0f, child: _caption);

        // Seed the resting/floating visuals for the initial text.
        _floating = _text.Length > 0;
        ApplyFloat();

        var column = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min,
            mainAxisAlignment: MainAxisAlignment.Center
        ) {
            // AnimatedSize eases the caption slot between 0 and its natural height, clipping while
            // in flight — the ~150ms title float.
            Children = {
                new AnimatedSize(_captionBox, 0.15f, Curves.EaseOut),
                _field,
            },
        };

        var row = new Row(crossAxisAlignment: CrossAxisAlignment.Center);
        // Leading inset + min-height strut (see AdwActionRow).
        row.Children.Add(new SizedBox(AdwMetrics.RowPaddingX, AdwMetrics.RowMinHeight));
        row.Children.Add(new Expanded(new Padding(EdgeInsets.Symmetric(0f, Spacing.Xs), column)));
        if (Suffix is not null)
        {
            // `> .editable-area > .apply-button { margin-left: 6px }`.
            row.Children.Add(new SizedBox(AdwMetrics.RowSpacing));
            row.Children.Add(Suffix);
        }

        row.Children.Add(new SizedBox(AdwMetrics.RowPaddingX));
        return row;
    }

    private void SyncFloat()
    {
        var floating = _field!.Focused || _field.Text.Length > 0;
        if (floating == _floating) return;
        _floating = floating;
        ApplyFloat();
        MarkNeedsLayout();
    }

    private void ApplyFloat()
    {
        if (_floating)
        {
            _caption!.Text = Title;
            _captionBox!.Height = null;
            _field!.Hint = "";
        }
        else
        {
            _caption!.Text = "";
            _captionBox!.Height = 0f;
            _field!.Hint = Title;
        }
    }
}

/// <summary>
///     AdwPasswordEntryRow — an <see cref="AdwEntryRow" /> whose value is masked, with a reveal-eye
///     suffix toggling visibility.
/// </summary>
public sealed class AdwPasswordEntryRow : ComposedWidget
{
    private bool _revealed;
    private string _title;
    private string _text;
    private AdwEntryRow? _row;

    public AdwPasswordEntryRow(
        string title = "",
        string text = "",
        Action<string>? onChanged = null)
    {
        _title = title;
        _text = text;
        OnChanged = onChanged;
    }

    public string Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    /// <summary>
    ///     Written through to the retained inner row rather than rebuilt: a rebuild would hand back a
    ///     fresh <see cref="AdwEntryRow" /> and drop the caret mid-typing. Mirrors
    ///     <see cref="AdwEntryRow.Text" />.
    /// </summary>
    public string Text
    {
        get => _row?.Text ?? _text;
        set
        {
            _text = value;
            if (_row is not null) _row.Text = value;
        }
    }

    public Action<string>? OnChanged { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var row = _row = new AdwEntryRow(
            Title,
            _text,
            v =>
            {
                _text = v;
                OnChanged?.Invoke(v);
            }
        ) { Obscure = !_revealed };

        // A flat circular AdwButton is exactly the header-bar icon button this used to hand-roll,
        // fade and focus ring included; the label is icon-only but still announced. Seeded from
        // _revealed so a rebuild (a Title change) doesn't silently re-mask a revealed password.
        var eye = new AdwButton("Reveal password") {
            IconName = _revealed ? Icons.VisibilityOff : Icons.Visibility,
            Style = AdwButtonStyle.Flat,
            Circular = true,
        };
        eye.OnPressed = () =>
        {
            _revealed = !_revealed;
            eye.IconName = _revealed ? Icons.VisibilityOff : Icons.Visibility;
            row.Obscure = !_revealed;
        };

        row.Suffix = eye;
        return row;
    }
}
