using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Editor.Scene;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Panels.AssetPreview;

/// <summary>
///     Shows a rich preview + metadata for the asset selected in the Asset Browser. Extensible by
///     registering <see cref="IAssetPreviewProvider" />s on the shared
///     <see cref="AssetPreviewRegistry" />.
///     Subscribes to <c>EditorState.AssetSelected</c>. On selection it resolves a provider, builds the
///     preview widget and a metadata footer, and composes them with real layout widgets
///     (<c>Column</c> → <c>Expanded</c>(preview) + <c>Divider</c> + metadata) so the framework handles
///     layout — no manual paint positioning (which previously overlapped the metadata onto the preview
///     because child <see cref="Widget.MeasuredSize" /> is only set during normal dispatch).
/// </summary>
public sealed class AssetPreviewPanel : RenderWidget, IDisposable
{
    private readonly Action<string> _onAssetSelected;
    private readonly AssetPreviewRegistry _registry;
    private readonly EditorState _state;
    private Widget _content;

    private string? _selectedPath;
    private Size _size;
    private ThemeData _theme;

    public AssetPreviewPanel(EditorState state, ThemeData theme,
        AssetPreviewRegistry? registry = null)
    {
        _state = state;
        _theme = theme;
        _registry = registry ?? AssetPreviewRegistry.Default();
        _content = Empty(theme);

        _onAssetSelected = OnAssetSelected;
        _state.AssetSelected += _onAssetSelected;
    }

    public void Dispose()
    {
        _state.AssetSelected -= _onAssetSelected;
        DisposeContent();
    }

    /// <summary>
    ///     Release the current preview's resources. A preview is free to own a texture or a mesh — the
    ///     engine hands those out caller-owned and frees none of them — so the panel that stops
    ///     showing one is the thing that has to say so. Providers with nothing to release simply do
    ///     not implement <see cref="IDisposable" />.
    /// </summary>
    private void DisposeContent()
    {
        if (_content is IDisposable disposable) disposable.Dispose();
    }

    private void OnAssetSelected(string path)
    {
        if (_selectedPath == path) return;
        _selectedPath = path;
        // The outgoing preview may own native resources (an image preview owns its texture, and
        // nothing else frees those). One rule for every provider, present and future: whatever we
        // stop showing, we dispose.
        DisposeContent();
        _content = BuildContent();
        MarkNeedsLayout();
        MarkNeedsPaint();
    }

    // ── Content tree (built once per selection; laid out by the framework) ────────

    private Widget BuildContent()
    {
        var path = _selectedPath;
        if (string.IsNullOrEmpty(path)) return Empty(_theme);

        var meta = AssetMetadata.For(path);
        var provider = _registry.Resolve(meta.Extension);

        Widget? preview = null;
        if (provider is not null)
        {
            try
            {
                preview = provider.BuildPreview(path, _theme);
            }
            catch
            {
                preview = null;
            }

            try
            {
                foreach (var (k, v) in provider.ExtraMetadata(path))
                    meta.Rows.Add((k, v));
            }
            catch
            {
                /* provider metadata failed — keep common rows */
            }
        }

        var footer = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
        };
        footer.Children.Add(
            new Label(meta.Name, _theme.FontSizeBody, _theme.OnSurface) {
                FontWeight = FontWeight.Medium,
                Overflow = TextOverflow.Ellipsis,
            }
        );
        footer.Children.Add(new SizedBox(height: 6f));
        footer.Children.Add(MetaRow("Type", FriendlyType(meta.Extension)));
        footer.Children.Add(MetaRow("Size", meta.SizeHuman));
        footer.Children.Add(MetaRow("Modified", meta.Modified));
        foreach (var (k, v) in meta.Rows)
            footer.Children.Add(MetaRow(k, v));

        var previewArea = preview ?? Empty(_theme, "No preview available");

        return new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children = {
                new Expanded(new Padding(EdgeInsets.All(8f), previewArea)),
                new Divider(),
                new Padding(EdgeInsets.All(8f), footer),
            },
        };
    }

    private Widget MetaRow(string key, string value)
    {
        return new Row {
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = {
                new SizedBox(86f, child: new Label(key, _theme.FontSizeCaption, _theme.TextMuted)),
                new Expanded(
                    new Label(value, _theme.FontSizeCaption, _theme.OnSurface) {
                        Overflow = TextOverflow.Ellipsis,
                    }
                ),
            },
        };
    }

    private static Widget Empty(ThemeData theme, string text = "Select an asset to preview")
    {
        return new Padding(EdgeInsets.All(16f), new Label(text, 12f, theme.TextMuted));
    }

    private static string FriendlyType(string ext)
    {
        return string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant();
    }

    // ── Layout (delegate everything to the composed content) ──────────────────────

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);

        var w = float.IsInfinity(c.MaxWidth) ? 280f : c.MaxWidth;
        var h = float.IsInfinity(c.MaxHeight) ? 360f : c.MaxHeight;
        _size = c.Constrain(new Size(w, MathF.Max(h, c.MinHeight)));

        _content.Measure(
            new Constraints(
                _size.Width,
                _size.Width,
                _size.Height,
                _size.Height
            )
        );
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _content.Layout(origin);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        yield return _content;
    }

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;
        paint.AddRect(Bounds, _theme.Surface);
        _content.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _content.HitTest(point) ?? this;
    }
}