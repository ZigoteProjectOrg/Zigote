namespace Zigote.UI.Material.FilePicker;

internal static class FilePickerScanner
{
    public static IReadOnlyList<string> Scan(
        string rootPath,
        IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return [];
        if (!Directory.Exists(rootPath)) return [];

        var allowedExtensions = extensions.Count == 0
            ? null
            : new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

        try
        {
            return Directory
                .GetFiles(rootPath, "*.*", SearchOption.AllDirectories)
                .Where(path =>
                    {
                        if (allowedExtensions is null) return true;

                        var ext = Path.GetExtension(path);
                        return allowedExtensions.Contains(ext);
                    }
                )
                .Select(path => Path.GetRelativePath(rootPath, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FilePicker] Error scanning files: {ex.Message}");
            return [];
        }
    }
}