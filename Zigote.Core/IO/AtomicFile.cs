using System.Text;

namespace Zigote.Core.IO;

/// <summary>
///     Whole-file writes that either replace the target completely or leave it untouched. The content
///     goes to a temporary file beside the target, is flushed to disk, and is then renamed over it —
///     so a crash, a full disk or a power cut mid-write costs the new version, never the old one.
///     <para>
///         For documents a user cannot afford to lose: scenes, projects, registries.
///         <see cref="File.WriteAllText(string, string?)" /> truncates the target first, and anything that goes
///         wrong after that leaves an empty or half-written file.
///     </para>
/// </summary>
public static class AtomicFile
{
    /// <summary>Replace <paramref name="path" /> with <paramref name="contents" /> (UTF-8, no BOM).</summary>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);

        // Unique per write: two writers of the same target must not share a temporary file.
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       path: temporary,
                       mode: FileMode.CreateNew,
                       access: FileAccess.Write,
                       share: FileShare.None
                   ))
            {
                stream.Write(Encoding.UTF8.GetBytes(contents));
                // To the device, not just the OS cache: the rename below can otherwise become
                // durable before the data it points at.
                stream.Flush(flushToDisk: true);
            }

            File.Move(sourceFileName: temporary, destFileName: path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The write already failed; a stray temporary file is the lesser problem.
            }

            throw;
        }
    }
}
