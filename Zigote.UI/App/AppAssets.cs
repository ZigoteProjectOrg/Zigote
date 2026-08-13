using Zigote.Core.Assets;

namespace Zigote.UI.Host;

/// <summary>
///     The app's own asset tree — the files declared as <c>ZigoteAsset</c> and deployed to
///     <c>Assets/</c> next to the executable (see <c>build/Zigote.Assets.targets</c>).
///     <para>
///         Apps address assets by the path they have inside <c>Assets/</c> —
///         <c>AppAssets.Path("Sprites/hero.png")</c> — and that string is the same in a dev build, a
///         published bundle and a macOS .app, which is the point of having a convention at all. It is
///         also what makes the publish-time shake possible: a literal path is a path the build can see
///         (<c>tools/AssetShake.cs</c>), while one assembled at runtime is not.
///     </para>
///     <para>
///         This is the 2D/UI counterpart of the player's game <c>Content/</c> directory. Games keep
///         using that: their assets are reached from a scene graph, and the exporter already stages
///         them by reachability.
///     </para>
/// </summary>
public static class AppAssets
{
    private static string? _root;
    private static bool _probed;

    /// <summary>
    ///     Absolute path of the deployed <c>Assets/</c> directory, or null when the app ships none.
    ///     <para>
    ///         Probed exactly as the player probes <c>Content/</c>: both
    ///         <see cref="AppContext.BaseDirectory" /> and the real executable's directory, plus the
    ///         macOS <c>Contents/Resources</c> location. A self-extracting single-file build reports
    ///         its extraction directory under ~/.net as BaseDirectory while the assets sit beside the
    ///         executable, so probing only one of the two finds nothing in exactly the configuration
    ///         that is hardest to debug.
    ///     </para>
    /// </summary>
    public static string? Root
    {
        get
        {
            if (_probed) return _root;
            _probed = true;

            string?[] baseDirs = [
                AppContext.BaseDirectory,
                System.IO.Path.GetDirectoryName(Environment.ProcessPath),
            ];
            _root = baseDirs
                .OfType<string>()
                .SelectMany(dir => new[] {
                        System.IO.Path.Combine(path1: dir, path2: "Assets"),
                        System.IO.Path.GetFullPath(
                            System.IO.Path.Combine(
                                path1: dir,
                                path2: "..",
                                path3: "Resources",
                                path4: "Assets"
                            )
                        ),
                    }
                )
                .FirstOrDefault(Directory.Exists);

            return _root;
        }
    }

    /// <summary>
    ///     Resolve an asset path relative to <see cref="Root" />. Returns a path even when no asset
    ///     directory was found, so callers get a "file not found" naming the asset they asked for
    ///     rather than a null-reference somewhere further along.
    /// </summary>
    public static string Path(string relativePath) => System.IO.Path.Combine(
        path1: Root ?? AppContext.BaseDirectory,
        path2: relativePath
    );

    public static bool Exists(string relativePath) => File.Exists(Path(relativePath));

    public static byte[] ReadAllBytes(string relativePath) => File.ReadAllBytes(Path(relativePath));

    public static Stream OpenRead(string relativePath) => File.OpenRead(Path(relativePath));

    /// <summary>
    ///     An <see cref="AssetManager" /> rooted at this app's <c>Assets/</c> directory, for apps that
    ///     want the ref-counted, deduplicating streaming cache rather than plain file reads. The
    ///     manager takes its path resolution as a delegate precisely so it can be reused outside the
    ///     editor's project/content-root model — this is that reuse, not a second cache.
    ///     <para>Pair it with an <see cref="AssetRegistry" /> to turn stable ids back into paths.</para>
    /// </summary>
    public static AssetManager CreateManager(AssetRegistry registry) => new(id =>
        registry.Resolve(id) is { } relative ? Path(relative) : null
    );
}
