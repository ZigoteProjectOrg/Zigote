namespace Zigote.UI.Material.FilePicker;

public sealed class FilePickerModel
{
    private readonly List<string> _allFiles = [];

    public string Filter { get; private set; } = string.Empty;
    public string? SelectedFile { get; private set; }
    public IReadOnlyList<string> FilteredFiles { get; private set; } = [];

    public void SetFiles(IEnumerable<string> files)
    {
        _allFiles.Clear();
        _allFiles.AddRange(files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase));
        ApplyFilter();
    }

    public void SetFilter(string? filter)
    {
        Filter = filter ?? string.Empty;
        ApplyFilter();
    }

    public void Select(string file)
    {
        SelectedFile = file;
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(Filter))
        {
            FilteredFiles = _allFiles;
            return;
        }

        FilteredFiles = _allFiles
            .Where(file => file.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}