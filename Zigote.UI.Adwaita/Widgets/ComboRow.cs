using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwComboRow — an <see cref="AdwActionRow" /> whose suffix shows the selected value (dim) and a
///     pan-down arrow; clicking anywhere on the row opens the Adwaita popover list (popover surface,
///     radius 12, 32px rows, check on the selected item).
/// </summary>
public sealed class AdwComboRow : ComposedWidget
{
    private bool _enabled = true;
    private IReadOnlyList<string> _items;
    private int _selectedIndex;
    private string? _subtitle;
    private string _title;
    private IconGlyph? _arrow;
    private Label? _valueLabel;

    public AdwComboRow(
        string title = "",
        IReadOnlyList<string>? items = null,
        int selectedIndex = 0,
        Action<int>? onSelected = null,
        string? subtitle = null)
    {
        _title = title;
        _items = items ?? [];
        _selectedIndex = selectedIndex;
        OnSelected = onSelected;
        _subtitle = subtitle;
    }

    public string Title
    {
        get => _title;
        set => this.Set(field: ref _title, value: value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => this.Set(field: ref _subtitle, value: value);
    }

    public IReadOnlyList<string> Items
    {
        get => _items;
        set => this.Set(field: ref _items, value: value);
    }

    public Action<int>? OnSelected { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set => this.Set(field: ref _enabled, value: value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            if (_valueLabel is not null) _valueLabel.Text = SelectedText;
        }
    }

    private string SelectedText =>
        _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : "";

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        _valueLabel = new Label(text: SelectedText, style: AdwTypography.Body, color: p.DimLabel) {
            MaxLines = 1,
            Overflow = TextOverflow.Ellipsis,
        };

        _arrow = new IconGlyph(glyph: Icons.DropDown, size: AdwMetrics.IconSize, color: p.DimLabel);

        Widget row = new AdwActionRow(title: Title, subtitle: Subtitle) {
            Suffixes = { _valueLabel, _arrow },
            OnActivated = Enabled ? OpenPopup : null,
        };
        // Adwaita disabled rows dim wholesale.
        return Enabled ? row : new Opacity(opacity: AdwStyle.DisabledOpacity, child: row);
    }

    private void OpenPopup()
    {
        var app = App.Active;
        if (app is null || Items.Count == 0) return;
        // libadwaita parents the popover to the value + arrow box at the row's end, not the row.
        var anchor = Bounds;
        if (_valueLabel is not null && _arrow is not null)
        {
            float left = MathF.Min(x: _valueLabel.Bounds.X, y: _arrow.Bounds.X);
            float right = MathF.Max(x: _valueLabel.Bounds.Right, y: _arrow.Bounds.Right);
            // Stop short of the row's bottom edge so the arrow tip tucks into the row padding.
            anchor = new Rect(x: left, y: Bounds.Y, width: right - left, height: Bounds.Height - 6f);
        }

        new AdwListPopover(
            app: app,
            items: Items,
            anchor: anchor,
            onPick: i =>
            {
                SelectedIndex = i;
                OnSelected?.Invoke(i);
            },
            selected: _selectedIndex,
            showCheck: true,
            minWidth: 140f,
            arrow: true
        ).Show();
    }
}
