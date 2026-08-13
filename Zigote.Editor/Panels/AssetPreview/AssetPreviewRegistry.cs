using Zigote.Editor.Panels.AssetPreview.Providers;

namespace Zigote.Editor.Panels.AssetPreview;

/// <summary>
///     Ordered registry of <see cref="IAssetPreviewProvider" />s. <see cref="Resolve" /> returns the
///     first provider that handles a given extension, so more-specific providers should be
///     registered before broad fallbacks.
/// </summary>
public sealed class AssetPreviewRegistry
{
    private readonly List<IAssetPreviewProvider> _providers = [];

    public IReadOnlyList<IAssetPreviewProvider> Providers => _providers;

    /// <summary>A registry pre-populated with the built-in providers in priority order.</summary>
    public static AssetPreviewRegistry Default()
    {
        var r = new AssetPreviewRegistry();
        // Order matters: textures resolve to the rich Image preview first, then materials,
        // then code/text, models, and scene info.
        r.Register(new ImagePreviewProvider());
        r.Register(new MaterialPreviewProvider());
        r.Register(new PrefabPreviewProvider());
        r.Register(new SceneInfoPreviewProvider());
        r.Register(new CodeTextPreviewProvider());
        r.Register(new ModelPreviewProvider());
        return r;
    }

    public void Register(IAssetPreviewProvider provider) => _providers.Add(provider);

    /// <summary>Resolve a provider for the given lower-case extension, or null if none handles it.</summary>
    public IAssetPreviewProvider? Resolve(string ext)
    {
        foreach (var p in _providers)
        {
            if (p.CanHandle(ext))
                return p;
        }

        return null;
    }
}
