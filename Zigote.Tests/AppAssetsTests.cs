using Xunit;
using Zigote.UI.Host;

namespace Zigote.Tests;

/// <summary>
///     <see cref="AppAssets" /> — the deployed <c>Assets/</c> root a Zigote UI app addresses its own
///     files through. The resolution rules matter beyond convenience: the publish-time asset shake
///     (<c>tools/AssetShake.cs</c>) can only keep a file whose path the code states as a literal, so
///     the contract is "pass the path inside Assets/, unchanged, on every platform".
///     <para>
///         The test process is not a published app, so <see cref="AppAssets.Root" /> is whatever the
///         test host's layout yields — these assert the path algebra and the miss behaviour, which
///         hold either way.
///     </para>
/// </summary>
public class AppAssetsTests
{
    [Fact]
    public void Path_keeps_the_relative_path_and_roots_it()
    {
        var resolved = AppAssets.Path("Sprites/hero.png");

        Assert.True(System.IO.Path.IsPathRooted(resolved));
        Assert.EndsWith(
            System.IO.Path.Combine("Sprites", "hero.png"),
            resolved,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Nested_paths_survive_separator_normalisation()
    {
        // A forward-slashed literal is what app code writes (and what the shaker matches on); it must
        // resolve on Windows too, where Path.Combine yields backslashes.
        var resolved = AppAssets.Path("Audio/Ui/click.wav");

        Assert.Contains("Audio", resolved, StringComparison.Ordinal);
        Assert.Contains("click.wav", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_asset_reports_as_missing_rather_than_throwing()
    {
        // Root is null in a test host with no Assets/ directory. Path must still answer, so the
        // failure a caller sees names the asset instead of a NullReferenceException far from here.
        Assert.False(AppAssets.Exists("does-not-exist.png"));
        Assert.NotNull(AppAssets.Path("does-not-exist.png"));
    }

    [Fact]
    public void Reading_a_missing_asset_throws_naming_the_file()
    {
        var error = Assert.ThrowsAny<IOException>(() => AppAssets.ReadAllBytes("nope/missing.bin"));
        Assert.Contains("missing.bin", error.Message, StringComparison.Ordinal);
    }
}
