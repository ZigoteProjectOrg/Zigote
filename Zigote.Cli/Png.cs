using System.Buffers.Binary;
using System.IO.Compression;

namespace Zigote.Cli;

/// <summary>
///     Just enough PNG to turn one <c>icon.png</c> into every launcher density.
///     <para>
///         Hand-rolled on purpose. The CLI has no project references — the moment it takes one it
///         drags the native engine into `dotnet tool install` — and an image library would be a
///         dependency carried by every user of the tool for a resize that happens at most twice per
///         project. What this needs is narrow: read a truecolour PNG, scale it, write it back.
///     </para>
///     <para>
///         Narrow means narrow: 8-bit RGB/RGBA, non-interlaced, which is what every icon exporter
///         produces. Anything else is refused by name rather than mis-rendered, because a launcher
///         icon that comes out with swapped channels is a bug someone finds on a phone.
///     </para>
/// </summary>
public sealed class Png
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Straight RGBA8, row-major, no padding.</summary>
    public byte[] Pixels { get; }

    private Png(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Straight RGBA8 pixels as an image — the entry point for generated art and for tests.</summary>
    public static Png FromPixels(int width, int height, byte[] rgba) =>
        rgba.Length == width * height * 4
            ? new Png(width: width, height: height, pixels: rgba)
            : throw new CliError($"{rgba.Length} bytes is not {width}×{height} RGBA.");

    /// <summary>
    ///     The icon an app gets when it has none: a rounded square with a simple window mark, tinted
    ///     from the app's own name.
    ///     <para>
    ///         Generated rather than shipped, and generated rather than skipped. Skipped means every
    ///         Zigote app without an <c>icon.png</c> shows the platform's generic placeholder — the
    ///         grey gear on a Linux launcher, the white sheet on Android — which reads as a broken
    ///         install rather than a project that has not chosen art yet. Tinting by name means two
    ///         apps on the same launcher are still telling apart before either has an icon.
    ///     </para>
    ///     <para>
    ///         No letterform, deliberately: drawing one means a font, and a font means either a
    ///         dependency or a hand-built glyph table for a placeholder nobody should ship.
    ///     </para>
    /// </summary>
    public static Png DefaultIcon(string name, int size = 1024)
    {
        // A stable hue per name — the same app is the same colour on every machine and every build.
        int hash = 17;
        foreach (char c in name) hash = hash * 31 + char.ToLowerInvariant(c);
        (byte br, byte bg, byte bb) = HueToRgb((Math.Abs(hash) % 360) / 360.0);

        var pixels = new byte[size * size * 4];
        double radius = size * 0.22;
        double markOuter = size * 0.30, markInner = size * 0.19;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int at = (y * size + x) * 4;
            if (!InRoundedSquare(x: x, y: y, size: size, radius: radius)) continue;

            double dx = Math.Abs(x - size / 2.0), dy = Math.Abs(y - size / 2.0);
            // One square outline, centred. A title bar was tried and dropped: at any size where the
            // frame border is thick enough to read, the bar merges into it — and a placeholder is
            // not worth tuning geometry for. The outline alone says "an app, no art yet" and still
            // reads at 48 pixels, which is where a placeholder is actually seen.
            bool mark = Math.Max(dx, dy) <= markOuter && Math.Max(dx, dy) >= markInner;

            pixels[at] = mark ? (byte)245 : br;
            pixels[at + 1] = mark ? (byte)247 : bg;
            pixels[at + 2] = mark ? (byte)250 : bb;
            pixels[at + 3] = 255;
        }

        return new Png(width: size, height: size, pixels: pixels);
    }

    /// <summary>Inside the rounded square, corners included — the shape every launcher expects.</summary>
    private static bool InRoundedSquare(int x, int y, int size, double radius)
    {
        double left = radius, right = size - radius - 1;
        double cx = x < left ? left : x > right ? right : x;
        double cy = y < left ? left : y > right ? right : y;
        return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;
    }

    /// <summary>A hue at fixed saturation and value — dark enough that a white mark reads on it.</summary>
    private static (byte R, byte G, byte B) HueToRgb(double hue)
    {
        double h = hue * 6, saturation = 0.45, value = 0.36;
        int sector = (int)Math.Floor(h) % 6;
        double f = h - Math.Floor(h);
        double p = value * (1 - saturation);
        double q = value * (1 - f * saturation);
        double t = value * (1 - (1 - f) * saturation);
        (double r, double g, double b) = sector switch {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    // ── decode ────────────────────────────────────────────────────────────────

    public static Png Load(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (file.Length < 8 || !file.AsSpan(start: 0, length: 8).SequenceEqual(signature))
            throw new CliError($"'{Path.GetFileName(path)}' is not a PNG.");

        int width = 0, height = 0, channels = 0;
        var idat = new MemoryStream();
        int offset = 8;
        while (offset + 8 <= file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(bytes: file, index: offset + 4, count: 4);
            int dataAt = offset + 8;
            if (length < 0 || dataAt + length > file.Length)
                throw new CliError($"'{Path.GetFileName(path)}' is truncated.");

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataAt));
                    height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataAt + 4));
                    int depth = file[dataAt + 8];
                    int colorType = file[dataAt + 9];
                    int interlace = file[dataAt + 12];
                    channels = colorType switch {
                        2 => 3, // truecolour
                        6 => 4, // truecolour + alpha
                        _ => throw new CliError(
                            $"'{Path.GetFileName(path)}' is a palette or greyscale PNG. Export it as "
                            + "RGB or RGBA (colour type 2 or 6) — 'Save As PNG-24' in most tools."
                        ),
                    };
                    if (depth != 8)
                        throw new CliError($"'{Path.GetFileName(path)}' is {depth}-bit; 8 bits per channel is what this reads.");
                    if (interlace != 0)
                        throw new CliError($"'{Path.GetFileName(path)}' is interlaced. Re-export it without Adam7 interlacing.");
                    break;
                case "IDAT":
                    idat.Write(buffer: file, offset: dataAt, count: length);
                    break;
            }

            offset = dataAt + length + 4; // + CRC, which zlib's own check makes redundant here
            if (type == "IEND") break;
        }

        if (width <= 0 || height <= 0 || channels == 0)
            throw new CliError($"'{Path.GetFileName(path)}' has no image header.");

        idat.Position = 0;
        using var inflate = new ZLibStream(stream: idat, mode: CompressionMode.Decompress);
        var raw = new MemoryStream();
        inflate.CopyTo(raw);
        return new Png(
            width: width,
            height: height,
            pixels: Unfilter(
                raw: raw.ToArray(),
                width: width,
                height: height,
                channels: channels
            )
        );
    }

    /// <summary>
    ///     Undo the per-row filters and widen to RGBA. The filters are the whole reason a PNG cannot
    ///     just be inflated: each row is predicted from the one above or the pixel to the left, and
    ///     the prediction is undone left to right, in place.
    /// </summary>
    private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        int stride = width * channels;
        if (raw.Length < (long)(stride + 1) * height)
            throw new CliError("the PNG's pixel data is shorter than its header claims.");

        var rgba = new byte[width * height * 4];
        var previous = new byte[stride];
        var current = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int rowAt = y * (stride + 1);
            byte filter = raw[rowAt];
            Array.Copy(sourceArray: raw, sourceIndex: rowAt + 1, destinationArray: current, destinationIndex: 0, length: stride);
            for (int x = 0; x < stride; x++)
            {
                int a = x >= channels ? current[x - channels] : 0; // left
                int b = previous[x]; // up
                int c = x >= channels ? previous[x - channels] : 0; // up-left
                current[x] = filter switch {
                    0 => current[x],
                    1 => (byte)(current[x] + a),
                    2 => (byte)(current[x] + b),
                    3 => (byte)(current[x] + (a + b) / 2),
                    4 => (byte)(current[x] + Paeth(a: a, b: b, c: c)),
                    _ => throw new CliError($"unknown PNG row filter {filter}."),
                };
            }

            for (int x = 0; x < width; x++)
            {
                int source = x * channels, target = (y * width + x) * 4;
                rgba[target] = current[source];
                rgba[target + 1] = current[source + 1];
                rgba[target + 2] = current[source + 2];
                rgba[target + 3] = channels == 4 ? current[source + 3] : (byte)255;
            }

            (previous, current) = (current, previous);
        }

        return rgba;
    }

    private static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    // ── resize ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Box-filter downscale (and bilinear upscale), on premultiplied alpha.
    ///     <para>
    ///         Premultiplying is not a detail: an icon's transparent margin usually carries garbage
    ///         colour, and averaging it straight bleeds that colour into the edge — the classic dark
    ///         halo around a downscaled logo. Averaging colour weighted by alpha and dividing it back
    ///         out afterwards is what avoids it.
    ///     </para>
    /// </summary>
    public Png Resized(int size)
    {
        var output = new byte[size * size * 4];
        double scaleX = (double)Width / size, scaleY = (double)Height / size;
        for (int y = 0; y < size; y++)
        {
            int y0 = (int)(y * scaleY), y1 = Math.Max(val1: y0 + 1, val2: (int)((y + 1) * scaleY));
            for (int x = 0; x < size; x++)
            {
                int x0 = (int)(x * scaleX), x1 = Math.Max(val1: x0 + 1, val2: (int)((x + 1) * scaleX));
                double r = 0, g = 0, b = 0, a = 0;
                int count = 0;
                for (int sy = y0; sy < Math.Min(val1: y1, val2: Height); sy++)
                for (int sx = x0; sx < Math.Min(val1: x1, val2: Width); sx++)
                {
                    int at = (sy * Width + sx) * 4;
                    double alpha = Pixels[at + 3] / 255.0;
                    r += Pixels[at] * alpha;
                    g += Pixels[at + 1] * alpha;
                    b += Pixels[at + 2] * alpha;
                    a += Pixels[at + 3];
                    count++;
                }

                int target = (y * size + x) * 4;
                if (count == 0) continue;
                double alphaAverage = a / count;
                double unpremultiply = alphaAverage <= 0 ? 0 : 255.0 / alphaAverage / count;
                output[target] = Clamp(r * unpremultiply);
                output[target + 1] = Clamp(g * unpremultiply);
                output[target + 2] = Clamp(b * unpremultiply);
                output[target + 3] = Clamp(alphaAverage);
            }
        }

        return new Png(width: size, height: size, pixels: output);
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(value: Math.Round(value), min: 0, max: 255);

    // ── encode ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Write RGBA8 with filter 0 on every row. Bigger than an optimizing encoder would produce
    ///     and irrelevant at icon sizes — the largest launcher icon here is 192×192.
    /// </summary>
    public void Save(string path)
    {
        var raw = new byte[(Width * 4 + 1) * Height];
        for (int y = 0; y < Height; y++)
        {
            raw[y * (Width * 4 + 1)] = 0;
            Array.Copy(
                sourceArray: Pixels,
                sourceIndex: y * Width * 4,
                destinationArray: raw,
                destinationIndex: y * (Width * 4 + 1) + 1,
                length: Width * 4
            );
        }

        var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(stream: compressed, compressionLevel: CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(buffer: raw, offset: 0, count: raw.Length);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(destination: header, value: Width);
        BinaryPrimitives.WriteInt32BigEndian(destination: header.AsSpan(4), value: Height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(file: file, type: "IHDR", data: header);
        WriteChunk(file: file, type: "IDAT", data: compressed.ToArray());
        WriteChunk(file: file, type: "IEND", data: []);
    }

    private static void WriteChunk(Stream file, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(destination: length, value: data.Length);
        file.Write(length);
        byte[] typed = System.Text.Encoding.ASCII.GetBytes(type);
        file.Write(typed);
        file.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(destination: crc, value: Crc32([.. typed, .. data]));
        file.Write(crc);
    }

    /// <summary>PNG's CRC-32, computed the table-free way — three chunks per file do not need a table.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(crc & 1));
        }

        return crc ^ 0xFFFFFFFF;
    }
}
