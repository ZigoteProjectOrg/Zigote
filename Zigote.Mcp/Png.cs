using System.Buffers.Binary;
using System.IO.Compression;

namespace Zigote.Mcp;

/// <summary>
///     Turns the engine's capture BMPs into PNGs, because LLM clients render
///     <c>image/png</c> and not <c>image/bmp</c>. Reads exactly what
///     <c>zigote_capture_ui_bmp</c> writes — 54-byte header, 24-bit, bottom-up, BGR, rows padded
///     to 4 bytes — plus the 32-bit and top-down variants, in case the engine's writer ever
///     changes shape. Anything else is an error, not a guess.
/// </summary>
public static class Png
{
    public static byte[] FromBmp(byte[] bmp)
    {
        if (bmp.Length < 54 || bmp[0] != 'B' || bmp[1] != 'M')
            throw new ToolError("the capture reply was not a BMP");

        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10));
        var width = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22));
        var bpp = BinaryPrimitives.ReadInt16LittleEndian(bmp.AsSpan(28));

        var height = Math.Abs(rawHeight);
        var bottomUp = rawHeight > 0;
        if (width <= 0 || height == 0 || bpp is not (24 or 32))
            throw new ToolError($"unsupported BMP shape: {width}x{rawHeight} at {bpp}bpp");

        var bytesPerPixel = bpp / 8;
        var stride = (width * bytesPerPixel + 3) & ~3;
        if (dataOffset < 54 || (long)dataOffset + (long)stride * height > bmp.Length)
            throw new ToolError("truncated BMP");

        // PNG scanlines: filter byte 0, then RGB triples.
        var scanlines = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var srcRow = dataOffset + (bottomUp ? height - 1 - y : y) * stride;
            var dst = y * (1 + width * 3) + 1; // +1 skips the filter byte, already 0
            for (var x = 0; x < width; x++)
            {
                var src = srcRow + x * bytesPerPixel;
                scanlines[dst++] = bmp[src + 2]; // R  (BMP stores BGR)
                scanlines[dst++] = bmp[src + 1]; // G
                scanlines[dst++] = bmp[src];     // B
            }
        }

        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Fastest, true))
        {
            zlib.Write(scanlines);
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // color type: truecolor RGB

        using var png = new MemoryStream();
        png.Write((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(MemoryStream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);

        var typeBytes = new byte[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        png.Write(typeBytes);
        png.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        png.Write(crc);
    }

    // CRC-32 (the PNG/zip polynomial) over type + data. A table beats a package dependency.
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
