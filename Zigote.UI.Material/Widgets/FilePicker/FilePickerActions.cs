namespace Zigote.UI.Material.FilePicker;

internal sealed class FilePickerActions : StatelessWidget
{
    private readonly Button _cancelButton;
    private readonly Button _selectButton;

    public FilePickerActions(Button cancelButton, Button selectButton)
    {
        _cancelButton = cancelButton;
        _selectButton = selectButton;
    }

    protected override Widget Build(BuildContext context)
    {
        return new Row {
            MainAxisAlignment = MainAxisAlignment.End,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = {
                _cancelButton,
                new SizedBox(8f),
                _selectButton,
            },
        };
    }
}