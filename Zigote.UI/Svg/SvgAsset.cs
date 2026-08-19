using System.Runtime.InteropServices;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.UI.Host;

namespace Zigote.UI.Svg;

/// <summary>
///     A parsed SVG document, kept in its resolved form so it can be rasterized at any size —
///     jovial_svg's <c>ScalableImage</c>, or flutter_svg's <c>PictureInfo</c>. Parsing is the
///     expensive half (CSS cascade, text shaping, <c>use</c>/marker/gradient resolution);
///     rasterizing the same asset again at a new size is just tiny-skia filling paths.
///     <para>
///         An asset is independent of any widget: parse an icon once at startup, hand it to as many
///         <see cref="Widgets.Controls.SvgPicture" />s as you like, and dispose it when the last one
///         is gone. <see cref="Widgets.Controls.SvgPicture" />'s own factories parse per widget,
///         which is right for a one-off illustration and wasteful for an icon on every row.
///     </para>
///     <para>
///         <b>Compiled SVG.</b> <see cref="Compile(ReadOnlySpan{byte})" /> runs the parse ahead of
///         time and writes the resolved document back out as (still valid) SVG, with no CSS, no
///         text, no inheritance left to resolve — the same trade as jovial_svg's <c>.si</c>,
///         without a second format to load: a compiled document is loaded by the very same
///         <see cref="FromBytes" />. What it saves is proportional to what there was to resolve
///         (a stylesheet-and-text document parses several times faster; plain path art is a wash
///         and gets bigger), plus the one that dwarfs the rest — a document with no text never
///         makes the binding enumerate the system fonts. See <c>docs/svg.md</c> for measurements.
///     </para>
/// </summary>
/// <remarks>
///     Backed by resvg (native/zigote-svg). Rasterization is CPU work on the calling thread —
///     serialized per asset, since one native tree is not re-entrant — and does not touch the GPU;
///     <see cref="CreateTexture" /> is the step that does, and it must run on the UI thread.
/// </remarks>
public sealed class SvgAsset : IDisposable
{
    private readonly object _gate = new();
    private nint _tree;

    private SvgAsset(nint tree, Size size)
    {
        _tree = tree;
        IntrinsicSize = size;
    }

    /// <summary>The document's own size in CSS pixels — its <c>width</c>/<c>height</c> or viewBox.</summary>
    public Size IntrinsicSize { get; }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_tree == 0) return;
            NativeSvg.Free(_tree);
            _tree = 0;
        }
    }

    /// <summary>
    ///     Parse SVG bytes (plain, gzipped, or <see cref="Compile(ReadOnlySpan{byte})" />d).
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a valid SVG document.</exception>
    public static unsafe SvgAsset FromBytes(ReadOnlySpan<byte> svg)
    {
        float w, h;
        nint tree;
        fixed (byte* p = svg)
        {
            tree = NativeSvg.Parse(data: p, len: (nuint)svg.Length, outW: &w, outH: &h);
        }

        if (tree == 0) throw new InvalidDataException("Not a valid SVG document.");
        return new SvgAsset(tree: tree, size: new Size(width: w, height: h));
    }

    public static SvgAsset FromString(string svg) =>
        FromBytes(System.Text.Encoding.UTF8.GetBytes(svg));

    public static SvgAsset FromFile(string path) =>
        FromBytes(File.ReadAllBytes(Path.GetFullPath(path)));

    /// <summary>
    ///     An SVG from the app's deployed <c>Assets/</c> tree — <c>SvgAsset.FromAsset("icons/logo.svg")</c>.
    ///     Pass a literal path: that is what the publish-time asset shake looks for.
    /// </summary>
    public static SvgAsset FromAsset(string relativePath) =>
        FromFile(AppAssets.Path(relativePath));

    /// <summary>
    ///     Rasterize into a straight-alpha RGBA8 buffer of exactly
    ///     <paramref name="width" /> × <paramref name="height" /> pixels. The document is scaled to
    ///     fill that box, so pass a box with the aspect you want preserved.
    /// </summary>
    public unsafe byte[] Rasterize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ObjectDisposedException.ThrowIf(_tree == 0, this);

        byte[] rgba = new byte[width * height * 4];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_tree == 0, this);
            fixed (byte* p = rgba)
            {
                if (!NativeSvg.Render(
                        tree: _tree,
                        width: (uint)width,
                        height: (uint)height,
                        outPixels: p,
                        outLen: (nuint)rgba.Length
                    ))
                {
                    throw new InvalidOperationException($"SVG rasterization failed at {width}×{height}.");
                }
            }
        }

        return rgba;
    }

    /// <summary>
    ///     Rasterize and upload as an engine texture, returning its handle (0 on failure). The
    ///     caller owns it: release it with <see cref="ZigoteEngine.ReleaseTexture" />. UI thread.
    /// </summary>
    public ulong CreateTexture(int width, int height) =>
        ZigoteEngine.LoadTextureFromRgba(
            rgba: Rasterize(width: width, height: height),
            width: (uint)width,
            height: (uint)height
        );

    /// <summary>
    ///     Resolve <paramref name="svg" /> ahead of time and return the compiled document — still
    ///     SVG, with styles, text and references already flattened. Build-time step; the runtime
    ///     loads the result through <see cref="FromBytes" /> like any other SVG.
    ///     <para>The CLI exposes this as <c>zigote svg &lt;in&gt; &lt;out&gt;</c>.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a valid SVG document.</exception>
    public static unsafe byte[] Compile(ReadOnlySpan<byte> svg)
    {
        nuint len;
        byte* result;
        fixed (byte* p = svg)
        {
            result = NativeSvg.Compile(data: p, len: (nuint)svg.Length, outLen: &len);
        }

        if (result == null) throw new InvalidDataException("Not a valid SVG document.");
        try
        {
            return new ReadOnlySpan<byte>(pointer: result, length: (int)len).ToArray();
        }
        finally
        {
            NativeSvg.BytesFree(ptr: result, len: len);
        }
    }
}

/// <summary>resvg's C ABI (native/zigote-svg/src/lib.rs).</summary>
internal static unsafe partial class NativeSvg
{
    private const string Lib = "zigote_svg";

    [LibraryImport(Lib, EntryPoint = "zgsvg_parse")]
    internal static partial nint Parse(byte* data, nuint len, float* outW, float* outH);

    [LibraryImport(Lib, EntryPoint = "zgsvg_render")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool Render(nint tree, uint width, uint height, byte* outPixels,
        nuint outLen);

    [LibraryImport(Lib, EntryPoint = "zgsvg_free")]
    internal static partial void Free(nint tree);

    [LibraryImport(Lib, EntryPoint = "zgsvg_compile")]
    internal static partial byte* Compile(byte* data, nuint len, nuint* outLen);

    [LibraryImport(Lib, EntryPoint = "zgsvg_bytes_free")]
    internal static partial void BytesFree(byte* ptr, nuint len);
}
