using Zigote.Editor.Shading;
using Zigote.Graphs.Shading;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels.AssetPreview.Providers;

/// <summary>
///     Previews material assets (and textures, as a material applied to a sphere) by reusing the
///     CPU-shaded <see cref="MaterialPreviewWidget" /> from the shader editor. For a texture asset
///     the texture path is bound as the base-color map; for a ".mat" it shows defaults.
/// </summary>
public sealed class MaterialPreviewProvider : IAssetPreviewProvider
{
    private static readonly string[] TextureExts =
        [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga"];

    public bool CanHandle(string ext) =>
        ext == ".mat" || Array.IndexOf(array: TextureExts, value: ext) >= 0;

    public Widget BuildPreview(string path, ThemeData theme)
    {
        var widget = new MaterialPreviewWidget(theme);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        ShaderTextureRef[] textures = Array.IndexOf(array: TextureExts, value: ext) >= 0
            ? [new ShaderTextureRef(Path: path, Slot: TextureSlot.BaseColor)]
            : [];

        widget.Compiled = CompiledShaderGraph.Constant(
            c: SurfaceConstants.Default,
            textures: textures
        );
        return widget;
    }

    public IEnumerable<(string Key, string Value)> ExtraMetadata(string path)
    {
        // Texture dimensions are surfaced by ImagePreviewProvider; nothing material-specific to add.
        yield break;
    }
}
