using System.Globalization;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;

namespace Camera;

/// <summary>
///     A 3D color LUT (<c>.cube</c>) as a GPU resource: the lattice lives in a 2D strip texture
///     (slices side by side, the standard GPU encoding of a 3D LUT), and grading happens in the
///     render pipeline — <see cref="LutEffect" /> for anything on screen, the controller's photo
///     pass for captures. The camera frames themselves are never touched: capture stays raw, color
///     is a draw-time concern, and swapping LUTs mid-stream costs one texture upload.
/// </summary>
public sealed class CameraLut : IDisposable
{
    private byte[]? _strip;
    private ulong _texture;

    private CameraLut(int size, byte[] strip)
    {
        Size = size;
        _strip = strip;
    }

    /// <summary>Lattice resolution N (an N×N×N cube; 17, 33 and 65 are the common sizes).</summary>
    public int Size { get; }

    /// <summary>The un-uploaded strip, for asserting the parse without a GPU.</summary>
    internal byte[] StripForTesting =>
        _strip ?? throw new InvalidOperationException("Strip already uploaded.");

    /// <summary>
    ///     Parse a <c>.cube</c> file (Adobe/Resolve dialect: <c>LUT_3D_SIZE</c>, optional
    ///     <c>DOMAIN_MIN</c>/<c>DOMAIN_MAX</c>, red-fastest data order).
    /// </summary>
    /// <exception cref="InvalidDataException">Not a parseable 3D cube.</exception>
    public static CameraLut Load(string path) => Parse(File.ReadLines(path));

    /// <summary>Parse <c>.cube</c> content directly — for LUTs embedded as resources.</summary>
    public static CameraLut Parse(IEnumerable<string> lines)
    {
        int size = 0;
        float domainMin = 0f, domainMax = 1f;
        byte[]? strip = null;
        int index = 0;

        foreach (string raw in lines)
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty || line[0] == '#') continue;

            if (line.StartsWith("LUT_3D_SIZE"))
            {
                size = int.Parse(s: line["LUT_3D_SIZE".Length..].Trim(), provider: CultureInfo.InvariantCulture);
                if (size is < 2 or > 129)
                    throw new InvalidDataException($"Unsupported LUT_3D_SIZE {size}.");
                strip = new byte[size * size * size * 4];
                continue;
            }

            if (line.StartsWith("DOMAIN_MIN"))
            {
                domainMin = ParseFloat(FirstField(line["DOMAIN_MIN".Length..]));
                continue;
            }

            if (line.StartsWith("DOMAIN_MAX"))
            {
                domainMax = ParseFloat(FirstField(line["DOMAIN_MAX".Length..]));
                continue;
            }

            if (line.StartsWith("TITLE") || line.StartsWith("LUT_1D") || !IsDataLine(line))
                continue;

            if (strip is null)
                throw new InvalidDataException("LUT data before LUT_3D_SIZE.");
            if (index >= size * size * size) continue; // trailing junk: ignore, like everyone does

            // File order is red-fastest: index = r + g*N + b*N*N. Strip layout: slice b at
            // x ∈ [b*N, (b+1)*N), so texel x = b*N + r, y = g — one flat texture the shader
            // samples with hardware bilinear on r/g and a manual mix across b.
            (float r, float g, float b) = ParseTriple(line);
            float scale = domainMax - domainMin;
            if (scale <= 0) scale = 1f;
            int ri = index % size;
            int gi = index / size % size;
            int bi = index / (size * size);
            int at = ((gi * size * size) + (bi * size) + ri) * 4;
            strip[at + 0] = Quantize(value: r, min: domainMin, scale: scale);
            strip[at + 1] = Quantize(value: g, min: domainMin, scale: scale);
            strip[at + 2] = Quantize(value: b, min: domainMin, scale: scale);
            strip[at + 3] = 255;
            index++;
        }

        if (strip is null || index < size * size * size)
            throw new InvalidDataException(
                $"Incomplete .cube: expected {(long)size * size * size} entries, got {index}."
            );
        return new CameraLut(size: size, strip: strip);
    }

    /// <summary>
    ///     The strip texture's handle, uploading on first use. App thread (it touches the engine);
    ///     0 when no engine is running. The CPU copy is dropped after upload.
    /// </summary>
    public ulong EnsureTexture()
    {
        if (_texture != 0) return _texture;
        if (ZigoteEngine.Instance is null || _strip is null) return 0;
        _texture = ZigoteEngine.LoadTextureFromRgba(
            rgba: _strip,
            width: (uint)(Size * Size),
            height: (uint)Size
        );
        if (_texture != 0) _strip = null;
        return _texture;
    }

    public void Dispose()
    {
        if (_texture != 0 && ZigoteEngine.Instance is not null)
            ZigoteEngine.ReleaseTexture(_texture);
        _texture = 0;
        _strip = null;
    }

    private static byte Quantize(float value, float min, float scale) =>
        (byte)Math.Clamp(value: MathF.Round((value - min) / scale * 255f), min: 0f, max: 255f);

    private static bool IsDataLine(ReadOnlySpan<char> line) =>
        line[0] is '-' or '.' or >= '0' and <= '9';

    private static ReadOnlySpan<char> FirstField(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        int space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    private static float ParseFloat(ReadOnlySpan<char> s) =>
        float.Parse(s: s, provider: CultureInfo.InvariantCulture);

    private static (float R, float G, float B) ParseTriple(ReadOnlySpan<char> line)
    {
        Span<Range> fields = stackalloc Range[4];
        int n = line.SplitAny(destination: fields, separators: " \t",
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (n < 3) throw new InvalidDataException($"Bad .cube data line: '{line}'.");
        return (ParseFloat(line[fields[0]]), ParseFloat(line[fields[1]]), ParseFloat(line[fields[2]]));
    }
}

/// <summary>
///     The GPU grading pass: a registered shader effect that samples whatever was painted under it
///     (the backdrop) and pushes it through a <see cref="CameraLut" /> bound at <c>@group(1)</c>.
///     Works anywhere a <see cref="PaintList" /> does — over the live preview, inside a render
///     texture for the photo pipeline, over anything else an app wants graded.
/// </summary>
public static class LutEffect
{
    /// <summary>Our id in the engine's shared shader table — see <c>BackdropBlur.ShaderId</c>.</summary>
    public const uint ShaderId = 0xCA_3D_1D_01;

    private static bool _tried;
    private static bool _ok;

    /// <summary>Whether the pipeline compiled. Registration happens on first use, once.</summary>
    public static bool Ready
    {
        get
        {
            if (_tried) return _ok;
            if (ZigoteEngine.Instance is null) return false;
            _tried = true;
            _ok = ZigoteEngine.RegisterShader(id: ShaderId, wgsl: Source);
            return _ok;
        }
    }

    /// <summary>
    ///     Grade everything already painted inside <paramref name="bounds" />. No-op without a
    ///     compiled pipeline or an uploadable LUT — the content stays ungraded rather than absent.
    /// </summary>
    /// <param name="strength">0 = untouched, 1 = the LUT's full grade; linear blend between.</param>
    public static void Paint(PaintList paint, Rect bounds, CameraLut lut, float strength = 1f)
    {
        ArgumentNullException.ThrowIfNull(lut);
        if (strength <= 0f || !Ready) return;
        ulong texture = lut.EnsureTexture();
        if (texture == 0) return;
        paint.AddShaderEffect(
            bounds: bounds,
            shaderId: ShaderId,
            p0: lut.Size,
            p1: Math.Clamp(value: strength, min: 0f, max: 1f),
            imageKey: texture,
            // A grade is a filter: an app that puts its own passes either side of this one must
            // see them compose, rather than each reading the same ungraded frame.
            chainsBackdrop: true
        );
    }

    /// <summary>
    ///     Params: p0 = lattice size N, p1 = strength. The backdrop arrives linear (the scene
    ///     texture is sRGB, hardware decodes on sample); the LUT's domain is gamma, so coordinates
    ///     are re-encoded before lookup. The strip texture is sRGB too, so the sampled lattice
    ///     values come back linear — exactly what an sRGB render target wants written.
    /// </summary>
    private static readonly string Source =
        """
        @group(0) @binding(0) var backdrop: texture_2d<f32>;
        @group(0) @binding(1) var backdrop_sampler: sampler;
        @group(1) @binding(0) var lut: texture_2d<f32>;
        @group(1) @binding(1) var lut_sampler: sampler;

        struct VertexOut {
          @builtin(position) position: vec4<f32>,
          @location(0) uv: vec2<f32>,
          @location(1) params_a: vec4<f32>,
          @location(2) params_b: vec4<f32>,
        };

        @vertex
        fn vs_main(
          @location(0) position: vec2<f32>,
          @location(1) uv: vec2<f32>,
          @location(2) params_a: vec4<f32>,
          @location(3) params_b: vec4<f32>,
        ) -> VertexOut {
          var out: VertexOut;
          out.position = vec4<f32>(position, 0.0, 1.0);
          out.uv = uv;
          out.params_a = params_a;
          out.params_b = params_b;
          return out;
        }

        fn srgb_encode(c: vec3<f32>) -> vec3<f32> {
          let lo = c * 12.92;
          let hi = 1.055 * pow(max(c, vec3<f32>(0.0)), vec3<f32>(1.0 / 2.4)) - 0.055;
          return select(hi, lo, c <= vec3<f32>(0.0031308));
        }

        @fragment
        fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
          let src = textureSampleLevel(backdrop, backdrop_sampler, in.uv, 0.0);
          let n = in.params_a.x;
          let strength = in.params_a.y;

          // LUT domain is gamma-encoded [0,1]; hardware bilinear covers the r/g axes inside a
          // slice, the b axis blends two slice lookups.
          let c = clamp(srgb_encode(src.rgb), vec3<f32>(0.0), vec3<f32>(1.0));
          let b = c.b * (n - 1.0);
          let slice = floor(min(b, n - 1.001));
          let f = b - slice;
          let v = (c.g * (n - 1.0) + 0.5) / n;
          let u0 = (slice * n + c.r * (n - 1.0) + 0.5) / (n * n);
          let u1 = (min(slice + 1.0, n - 1.0) * n + c.r * (n - 1.0) + 0.5) / (n * n);
          let g0 = textureSampleLevel(lut, lut_sampler, vec2<f32>(u0, v), 0.0).rgb;
          let g1 = textureSampleLevel(lut, lut_sampler, vec2<f32>(u1, v), 0.0).rgb;
          let graded = mix(g0, g1, f);

          return vec4<f32>(mix(src.rgb, graded, strength), src.a);
        }
        """;
}
