using ZstdSharp;

namespace Zigote.Runtime.Content;

/// <summary>
///     Transparent read path for game content that may be zstd-compressed at export: engine-native
///     binary formats the C# runtime reads itself (.zmesh vertex blobs, .hdr environments) ship as
///     '&lt;file&gt;.zst' — smaller install, and zstd decompress is typically faster than the extra
///     disk
///     read. Plain files (the editor's working tree, textures loaded natively by path) read as-is.
/// </summary>
public static class ContentFiles
{
    public const string CompressedExtension = ".zst";

    public static bool Exists(string path)
    {
        return File.Exists(path) || File.Exists(path + CompressedExtension);
    }

    public static byte[] ReadAllBytes(string path)
    {
        if (File.Exists(path)) return File.ReadAllBytes(path);

        var compressed = path + CompressedExtension;
        if (!File.Exists(compressed))
            throw new FileNotFoundException($"Content not found: {path}", path);

        using var src = File.OpenRead(compressed);
        using var zstd = new DecompressionStream(src);
        using var buffer = new MemoryStream();
        zstd.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    ///     Write <paramref name="src" /> as '&lt;dst&gt;.zst'. Level 15 favors ratio — export-time
    ///     compression is offline; decompression speed is level-independent. Returns compressed size.
    /// </summary>
    public static long WriteCompressed(string src, string dst, int level = 15)
    {
        using var input = new FileStream(
            src,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        using var output = new FileStream(
            dst + CompressedExtension,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        );
        using var zstd = new CompressionStream(output, level);
        input.CopyTo(zstd);
        zstd.Flush();
        return output.Position;
    }
}
