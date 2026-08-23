using Xunit;
using Zigote.Cli;

namespace Zigote.Tests;

/// <summary>
///     The two pieces of <c>zigote build</c> that compute something: the Android versionCode derived
///     from the app's one version string, and the icon rasteriser that turns one PNG into every
///     launcher density. Both are silent when wrong — a versionCode that does not increase is
///     rejected by the store after the upload, and a broken resize is a logo nobody notices until
///     it is on a phone.
/// </summary>
public class BuildCliTests
{
    [Theory]
    [InlineData("1.0.0", 10000)]
    [InlineData("1.4.2", 10402)]
    [InlineData("0.2.0-preview.3", 200)] // prerelease tags do not reach the number
    [InlineData("2.10.9", 21009)]
    public void VersionCode_IsMonotonicInTheVersion(string version, int expected) =>
        Assert.Equal(expected, Build.VersionCode(version));

    [Fact]
    public void VersionCode_RefusesAComponentItCannotPack() =>
        Assert.Throws<CliError>(() => Build.VersionCode("1.100.0"));

    [Fact]
    public void VersionCode_Increases_ForEveryOrderedRelease()
    {
        string[] releases = ["1.0.0", "1.0.1", "1.1.0", "1.10.0", "2.0.0"];
        int[] codes = [.. releases.Select(Build.VersionCode)];
        for (int i = 1; i < codes.Length; i++)
            Assert.True(codes[i] > codes[i - 1], $"{releases[i]} must outrank {releases[i - 1]}");
    }

    [Fact]
    public void Png_RoundTripsThroughResize()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zigote-icon-{Guid.NewGuid():N}.png");
        string resized = path.Replace(".png", "-48.png");
        try
        {
            // A 4x4 image, opaque red in the top-left quadrant and transparent elsewhere: enough to
            // catch a channel swap, an alpha bleed, or a row-filter mistake.
            WriteSolid(path: path, size: 4);
            var icon = Png.Load(path);
            Assert.Equal(4, icon.Width);
            Assert.Equal(255, icon.Pixels[0]); // R
            Assert.Equal(0, icon.Pixels[1]); // G
            Assert.Equal(255, icon.Pixels[3]); // A

            icon.Resized(48).Save(resized);
            var scaled = Png.Load(resized);
            Assert.Equal(48, scaled.Width);
            Assert.Equal(48, scaled.Height);
            // The top-left pixel is inside the red quadrant at every scale.
            Assert.Equal(255, scaled.Pixels[0]);
            Assert.Equal(0, scaled.Pixels[2]);
            Assert.Equal(255, scaled.Pixels[3]);
            // ...and the bottom-right one is inside the transparent half, with no red bled into it.
            int last = (47 * 48 + 47) * 4;
            Assert.Equal(0, scaled.Pixels[last + 3]);
        }
        finally
        {
            File.Delete(path);
            File.Delete(resized);
        }
    }

    [Fact]
    public void Png_RefusesWhatItCannotRead()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zigote-not-a-{Guid.NewGuid():N}.png");
        File.WriteAllText(path: path, contents: "definitely not a png");
        try
        {
            Assert.Throws<CliError>(() => Png.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Writes a square RGBA PNG: red where x and y are in the first half, transparent elsewhere.</summary>
    private static void WriteSolid(string path, int size)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int at = (y * size + x) * 4;
            bool filled = x < size / 2 && y < size / 2;
            pixels[at] = filled ? (byte)255 : (byte)0;
            pixels[at + 3] = filled ? (byte)255 : (byte)0;
        }

        Png.FromPixels(width: size, height: size, rgba: pixels).Save(path);
    }
}

/// <summary>
///     The golden-diff panel: three images wide, and every changed pixel unmistakable. A diff that
///     is subtly wrong is worse than none — it sends someone looking at the wrong widget.
/// </summary>
public class GoldenDiffTests
{
    [Fact]
    public void Compose_PutsExpectedActualAndDifferenceSideBySide()
    {
        var expected = Solid(size: 4, r: 10, g: 20, b: 30);
        var actual = Solid(size: 4, r: 10, g: 20, b: 30);
        // One pixel moved, in the middle of the image.
        actual.Pixels[(2 * 4 + 2) * 4] = 200;

        var panel = Zigote.Cli.Test.Compose(expected: expected, actual: actual);

        Assert.Equal(12, panel.Width); // three 4-wide panels
        Assert.Equal(4, panel.Height);
        // Panel 1 is the expected image untouched, panel 2 the actual.
        Assert.Equal(10, panel.Pixels[0]);
        Assert.Equal(200, panel.Pixels[((2 * 12) + 4 + 2) * 4]);
        // Panel 3: magenta exactly where they differ, dimmed everywhere else.
        int changed = ((2 * 12) + 8 + 2) * 4;
        Assert.Equal(255, panel.Pixels[changed]);
        Assert.Equal(0, panel.Pixels[changed + 1]);
        Assert.Equal(255, panel.Pixels[changed + 2]);
        int unchanged = 8 * 4;
        Assert.Equal(10 / 3, panel.Pixels[unchanged]);
    }

    private static Png Solid(int size, byte r, byte g, byte b)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            pixels[i * 4] = r;
            pixels[i * 4 + 1] = g;
            pixels[i * 4 + 2] = b;
            pixels[i * 4 + 3] = 255;
        }

        return Png.FromPixels(width: size, height: size, rgba: pixels);
    }
}

/// <summary>
///     The icon an app gets when it has none. Checked rather than eyeballed because it is generated
///     at build time on someone else's machine: it has to be square, opaque inside the rounded
///     square and transparent at its corners, carry a visible mark, and be a different colour for a
///     different app — the whole point being that two placeholder icons on one launcher are still
///     telling apart.
/// </summary>
public class DefaultIconTests
{
    [Fact]
    public void DefaultIcon_IsARoundedOpaqueSquareWithAMark()
    {
        var icon = Png.DefaultIcon(name: "Fresh", size: 256);
        icon.Save(Path.Combine(Path.GetTempPath(), "zigote-default-icon.png"));

        Assert.Equal(256, icon.Width);
        Assert.Equal(256, icon.Height);
        Assert.Equal(0, Alpha(icon: icon, x: 0, y: 0)); // the corner is rounded away
        Assert.Equal(255, Alpha(icon: icon, x: 128, y: 128)); // and the middle is solid

        // The mark is light on a dark ground: some pixel has to be much brighter than the centre.
        int ground = icon.Pixels[(128 * 256 + 128) * 4];
        bool hasMark = Enumerable
            .Range(0, 256 * 256)
            .Any(i => icon.Pixels[i * 4] > ground + 100 && icon.Pixels[i * 4 + 3] == 255);
        Assert.True(hasMark, "the placeholder needs a visible mark, not a plain colour field");
    }

    [Fact]
    public void DefaultIcon_TintsByName_SoTwoPlaceholdersAreTellingApart()
    {
        var one = Png.DefaultIcon(name: "Fresh", size: 64);
        var other = Png.DefaultIcon(name: "Zigote.UI.Adwaita.Gallery", size: 64);
        int at = (32 * 64 + 8) * 4; // inside the ground, left of the mark

        Assert.NotEqual(
            (one.Pixels[at], one.Pixels[at + 1], one.Pixels[at + 2]),
            (other.Pixels[at], other.Pixels[at + 1], other.Pixels[at + 2])
        );
        // ...and the same name is the same colour on every machine and every build.
        var again = Png.DefaultIcon(name: "Fresh", size: 64);
        Assert.Equal(one.Pixels[at], again.Pixels[at]);
    }

    private static byte Alpha(Png icon, int x, int y) => icon.Pixels[(y * icon.Width + x) * 4 + 3];
}
