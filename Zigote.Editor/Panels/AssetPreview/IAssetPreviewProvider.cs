using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels.AssetPreview;

/// <summary>
///     Extensible preview contract: one provider per asset family (images, code, models, …).
///     The <see cref="AssetPreviewRegistry" /> resolves the first provider whose
///     <see cref="CanHandle" /> returns true for an asset's extension.
/// </summary>
public interface IAssetPreviewProvider
{
    /// <summary>True if this provider can preview the given lower-case extension (incl. leading dot).</summary>
    bool CanHandle(string ext);

    /// <summary>
    ///     Build the preview widget for <paramref name="path" />. Must never throw — return a
    ///     fallback widget on failure. Called on selection; the result is a retained widget.
    /// </summary>
    Widget BuildPreview(string path, ThemeData theme);

    /// <summary>Type-specific metadata rows (e.g. dimensions, line count). Must never throw.</summary>
    IEnumerable<(string Key, string Value)> ExtraMetadata(string path);
}