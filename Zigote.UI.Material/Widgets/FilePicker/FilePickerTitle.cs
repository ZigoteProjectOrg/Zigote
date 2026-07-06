namespace Zigote.UI.Material.FilePicker;

internal sealed class FilePickerTitle : StatelessWidget
{
    private readonly string _title;

    public FilePickerTitle(string title)
    {
        _title = title;
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new Label(_title, theme.FontSizeTitle, theme.OnSurface) {
            FontWeight = FontWeight.Bold,
        };
    }
}