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
                : [new FileDialogFilter(name: "Matching Files", extensions: extensions)],
        };
        Run();
        return;

        async void Run()
        {
            try
            {
                string[] picked = await FileBrowserDialog.ShowAsync(app: app, options: options);
                if (picked.Length == 0)
                {
                    onCancel?.Invoke();
                    return;
                }

                string relative = Path.GetRelativePath(relativeTo: rootPath, path: picked[0])
                    .Replace(oldChar: '\\', newChar: '/');
                onSelected(
                    relative.StartsWith(value: "..", comparisonType: StringComparison.Ordinal)
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
