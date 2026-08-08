using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwComboRow — an <see cref="AdwActionRow" /> whose suffix shows the selected value (dim) and a
///     pan-down arrow; clicking anywhere on the row opens the Adwaita popover list (popover surface,
///     radius 12, 32px rows, check on the selected item).
/// </summary>
public sealed class AdwComboRow : StatelessWidget
{
    private Label? _valueLabel;
    private int _selectedIndex;
    private string _title;
    private string? _subtitle;
    private IReadOnlyList<string> _items;
    private bool _enabled = true;

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
        set => this.Set(ref _title, value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => this.Set(ref _subtitle, value);
    }

    public IReadOnlyList<string> Items
    {
        get => _items;
        set => this.Set(ref _items, value);
    }

    public Action<int>? OnSelected { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
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

        _valueLabel = new Label(SelectedText, AdwTypography.Body, p.DimLabel) {
            MaxLines = 1,
            Overflow = TextOverflow.Ellipsis,
        };

        Widget row = new AdwActionRow(Title, Subtitle) {
            Suffixes = {
                _valueLabel,
                new IconGlyph(Icons.DropDown, AdwMetrics.IconSize, p.DimLabel),
            },
            OnActivated = Enabled ? OpenPopup : null,
        };
        // Adwaita disabled rows dim wholesale.
        return Enabled ? row : new Opacity(AdwStyle.DisabledOpacity, row);
    }

    private void OpenPopup()
    {
        var app = App.Active;
        if (app is null || Items.Count == 0) return;
        new AdwPopover(
            app,
            Items,
            Bounds,
            i =>
            {
                SelectedIndex = i;
                OnSelected?.Invoke(i);
            },
            _selectedIndex,
            true,
            140f
        ).Show();
    }
}