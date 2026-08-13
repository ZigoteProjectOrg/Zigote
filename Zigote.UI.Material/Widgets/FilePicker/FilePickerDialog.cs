using Zigote.Core.Engine;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     Project-scoped file picker: the <see cref="FileBrowserDialog" /> locked to a root
///     directory, returning root-relative paths. This is the picker for asset REFERENCES
///     (Inspector mesh/texture fields) — it must not wander outside the project, unlike the
///     OS-level dialogs behind <see cref="FileDialog" />. The static <see cref="Show" /> shape
///     predates the browser and is kept for its call sites.
/// </summary>
public static class FilePickerDialog
{
    /// <summary>
    ///     Pick one file under <paramref name="rootPath" /> matching
    ///     <paramref name="extensions" /> (with or without dots; empty = any). Navigation is
    ///     clamped to the root, and <paramref name="onSelected" /> receives the selection as a
    ///     root-relative, forward-slash path.
    /// </summary>
    public static void Show(
        App app,
        string title,
        string rootPath,
        string[] extensions,
        Action<string> onSelected,
        Action? onCancel = null)
    {
        var options = new FileBrowserOptions {
            Kind = FileDialogKind.OpenFile,
            Title = title,
            StartDirectory = rootPath,
            LockRoot = rootPath,
            Filters = extensions.Length == 0
                ? null
                : [new FileDialogFilter("Matching Files", extensions)],
        };
        Run();
        return;

        async void Run()
        {
            try
            {
                var picked = await FileBrowserDialog.ShowAsync(app, options);
                if (picked.Length == 0)
                {
                    onCancel?.Invoke();
                    return;
                }

                var relative = Path.GetRelativePath(rootPath, picked[0]).Replace('\\', '/');
                onSelected(
                    relative.StartsWith("..", StringComparison.Ordinal)
                        ? picked[0]
                        : relative
                );
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FilePicker] {ex.Message}");
                onCancel?.Invoke();
            }
        }
    }
}
