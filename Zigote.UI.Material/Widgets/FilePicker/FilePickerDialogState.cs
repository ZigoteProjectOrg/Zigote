namespace Zigote.UI.Material.FilePicker;

internal sealed class FilePickerDialogState : WidgetState<FilePickerDialog>
{
    private readonly FilePickerModel _model = new();
    private Button _cancelButton = null!;
    private FileGrid _grid = null!;
    private ScrollView _scrollView = null!;

    private TextField _searchField = null!;
    private Button _selectButton = null!;
    private Label _selectedLabel = null!;

    public override void InitState()
    {
        _model.SetFiles(
            FilePickerScanner.Scan(
                Widget.RootPath,
                Widget.Extensions
            )
        );

        _searchField = new TextField(decoration: new InputDecoration("Search files...")) {
            Height = 24f,
            OnChanged = OnSearchChanged,
        };

        _selectedLabel = new Label("Selected: (None)") {
            Style = Label.LabelStyle.Caption,
        };

        _grid = new FileGrid(
            SelectFile,
            ConfirmFile,
            () => _model.SelectedFile
        );

        _scrollView = new ScrollView {
            Child = _grid,
        };

        _cancelButton = new Button("Cancel", CancelSelection) {
            Style = ButtonStyle.Flat,
        };

        _selectButton = new Button("Select", ConfirmSelection);

        RefreshGrid();
    }

    public override Widget Build(BuildContext context)
    {
        return new FilePickerBody(
            Widget.Title,
            _searchField,
            _scrollView,
            _selectedLabel,
            _cancelButton,
            _selectButton
        );
    }

    private void OnSearchChanged(string value)
    {
        SetStateLayout(() =>
            {
                _model.SetFilter(value);
                RefreshGrid();
            }
        );
    }

    private void SelectFile(string file)
    {
        SetState(() =>
            {
                _model.Select(file);
                _selectedLabel.Text = "Selected: " + file;
            }
        );
    }

    private void ConfirmFile(string file)
    {
        SelectFile(file);
        ConfirmSelection();
    }

    private void ConfirmSelection()
    {
        var selected = _model.SelectedFile;
        if (string.IsNullOrEmpty(selected)) return;

        Widget.OnSelected(selected);
        Widget.HostDialog?.Dismiss();
    }

    private void CancelSelection()
    {
        Widget.OnCancel?.Invoke();
        Widget.HostDialog?.Dismiss();
    }

    private void RefreshGrid()
    {
        _grid.SetFiles(_model.FilteredFiles);
    }
}