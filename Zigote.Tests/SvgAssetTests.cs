using System.Text;
using Xunit;
using Zigote.UI.Svg;

namespace Zigote.Tests;

/// <summary>
///     <see cref="SvgAsset" /> — the resvg binding behind <c>SvgPicture</c>. These run the native
///     library for real (parse and rasterize are CPU-only; only the texture upload needs an engine),
///     which is the point: the C ABI in <c>native/zigote-svg/src/lib.rs</c> hands back raw pointers
///     and a pixel buffer whose alpha convention the image shader depends on, and none of that is
///     visible to the compiler on either side.
/// </summary>
public class SvgAssetTests
{
    // Half red, half transparent, with the fill arriving through a CSS class — so a document that
    // still needs resolving, not one already in usvg's simplified form.
    private const string Svg =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="20" height="10">
          <style>.a { fill: #ff0000 }</style>
          <rect class="a" width="10" height="10"/>
        </svg>
        """;

    [Fact]
    public void Parses_its_intrinsic_size()
    {
        using var asset = SvgAsset.FromString(Svg);

        Assert.Equal(expected: 20f, actual: asset.IntrinsicSize.Width, tolerance: 0.01f);
        Assert.Equal(expected: 10f, actual: asset.IntrinsicSize.Height, tolerance: 0.01f);
    }

    [Fact]
    public void Rasterizes_to_straight_alpha_rgba()
    {
        using var asset = SvgAsset.FromString(Svg);

        byte[] rgba = asset.Rasterize(width: 20, height: 10);

        Assert.Equal(expected: 20 * 10 * 4, actual: rgba.Length);
        // Left half: opaque red. Straight alpha, so the red channel is full even where it is
        // composited — a premultiplied buffer would still read 255 here, which is why the
        // transparent half below is the half that actually pins the convention.
        Assert.Equal(expected: [255, 0, 0, 255], actual: rgba[..4]);
        // Right half: nothing was drawn.
        int lastPixel = (20 * 10 - 1) * 4;
        Assert.Equal(expected: 0, actual: rgba[lastPixel + 3]);
    }

    [Fact]
    public void Compiled_output_is_a_resolved_svg_that_parses_back_the_same()
    {
        byte[] compiled = SvgAsset.Compile(Encoding.UTF8.GetBytes(Svg));
        string xml = Encoding.UTF8.GetString(compiled);

        // The whole point of compiling: the CSS is gone, the fill is on the element, and the rect
        // is already a path.
        Assert.DoesNotContain(expectedSubstring: "<style", actualString: xml);
        Assert.Contains(expectedSubstring: "#ff0000", actualString: xml);

        using var original = SvgAsset.FromString(Svg);
        using var reloaded = SvgAsset.FromBytes(compiled);
        Assert.Equal(expected: original.IntrinsicSize.Width, actual: reloaded.IntrinsicSize.Width);
        Assert.Equal(
            expected: original.Rasterize(width: 20, height: 10),
            actual: reloaded.Rasterize(width: 20, height: 10)
        );
    }

    [Fact]
    public void Bad_bytes_are_rejected_rather_than_returning_an_empty_document()
    {
        Assert.Throws<InvalidDataException>(() => SvgAsset.FromString("<html>not an svg</html>"));
    }
}
