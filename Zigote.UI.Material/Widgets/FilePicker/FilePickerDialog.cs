using Zigote.UI.Material.FilePicker;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     A modal dialog file picker that presents a grid-like listing of files
///     matching specific extensions under a root directory.
/// </summary>
public sealed class FilePickerDialog : StatefulWidget
{
    public FilePickerDialog(
        App app,
        string title,
        string rootPath,
        string[] extensions,
        Action<string> onSelected,
        Action? onCancel = null)
    {
        App = app;
        Title = title;
        RootPath = rootPath;
        Extensions = NormalizeExtensions(extensions);
        OnSelected = onSelected;
        OnCancel = onCancel;
    }

    internal App App { get; }
    internal string Title { get; }
    internal string RootPath { get; }
    internal IReadOnlyList<string> Extensions { get; }
    internal Action<string> OnSelected { get; }
    internal Action? OnCancel { get; }

    internal Dialog? HostDialog { get; set; }

    /// <summary>Displays the file picker dialog as a modal overlay.</summary>
    public static void Show(
        App app,
        string title,
        string rootPath,
        string[] extensions,
        Action<string> onSelected,
        Action? onCancel = null)
    {
        var picker = new FilePickerDialog(
            app,
            title,
            rootPath,
            extensions,
            onSelected,
            onCancel
        );

        var dialog = new Dialog(new SizedBox(500f, child: picker), app) {
            Dismissible = true,
        };

        picker.HostDialog = dialog;
        dialog.Show();
    }

    protected override WidgetState CreateState()
    {
        return new FilePickerDialogState();
    }

    private static string[] NormalizeExtensions(IEnumerable<string> extensions)
    {
        return extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e =>
                {
                    var ext = e.Trim();
                    return ext.StartsWith('.') ? ext : "." + ext;
                }
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}