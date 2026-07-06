namespace Zigote.UI.Material.FilePicker;

internal sealed class FilePickerBody : StatelessWidget
{
    private readonly Button _cancelButton;
    private readonly ScrollView _fileList;
    private readonly TextField _searchField;
    private readonly Button _selectButton;
    private readonly Label _selectedLabel;
    private readonly string _title;

    public FilePickerBody(
        string title,
        TextField searchField,
        ScrollView fileList,
        Label selectedLabel,
        Button cancelButton,
        Button selectButton)
    {
        _title = title;
        _searchField = searchField;
        _fileList = fileList;
        _selectedLabel = selectedLabel;
        _cancelButton = cancelButton;
        _selectButton = selectButton;
    }

    protected override Widget Build(BuildContext context)
    {
        return new Column {
            MainAxisAlign = MainAxisAlignment.Start,
            CrossAxisAlign = CrossAxisAlignment.Start,
            Children = {
                new FilePickerTitle(_title),
                new SizedBox(height: 8f),
                new SizedBox(height: 24f, child: _searchField),
                new SizedBox(height: 8f),
                new SizedBox(
                    height: 300f,
                    child: new Card(_fileList)
                ),
                new SizedBox(height: 8f),
                _selectedLabel,
                new SizedBox(height: 12f),
                new FilePickerActions(
                    _cancelButton,
                    _selectButton
                ),
            },
        };
    }
}