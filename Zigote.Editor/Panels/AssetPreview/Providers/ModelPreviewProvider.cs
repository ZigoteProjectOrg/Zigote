using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels.AssetPreview.Providers;

/// <summary>
///     Previews 3D model assets. Geometry is parsed and rasterized entirely on the CPU
///     (<see cref="MeshLoader" /> + <see cref="MeshPreviewWidget" />) — no native renderer round-trip,
///     so it sidesteps the renderer-gated <c>Render3D</c> world path. When a file can't be loaded as
///     mesh geometry it falls back to the structured placeholder card.
/// </summary>
public sealed class ModelPreviewProvider : IAssetPreviewProvider
{
    private static readonly string[] Exts =
        [".zmesh", ".gltf", ".glb", ".fbx", ".obj", ".dae", ".ply", ".stl"];

    public bool CanHandle(string ext)
    {
        return Array.IndexOf(Exts, ext) >= 0;
    }

    public Widget BuildPreview(string path, ThemeData theme)
    {
        var mesh = MeshLoader.Load(path);
        if (mesh is { } m && m.TriangleCount > 0)
            return new MeshPreviewWidget(m, theme);

        return new ModelPlaceholderWidget(path, theme);
    }

    public IEnumerable<(string Key, string Value)> ExtraMetadata(string path)
    {
        // Prefer accurate counts straight off the loaded geometry (covers .zmesh + mesh-cache too).
        var mesh = MeshLoader.Load(path);
        if (mesh is { } m && m.TriangleCount > 0)
        {
            yield return ("Vertices", m.VertexCount.ToString());
            yield return ("Triangles", m.TriangleCount.ToString());
            yield break;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Cheap text-format counts where possible; binary formats stay opaque.
        if (ext == ".obj")
        {
            var (verts, faces) = CountObj(path);
            if (verts > 0) yield return ("Vertices", verts.ToString());
            if (faces > 0) yield return ("Faces", faces.ToString());
        }
    }

    private static (int Verts, int Faces) CountObj(string path)
    {
        var verts = 0;
        var faces = 0;
        try
        {
            using var reader = new StreamReader(path);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                if (line.StartsWith("v ", StringComparison.Ordinal)) verts++;
                else if (line.StartsWith("f ", StringComparison.Ordinal)) faces++;
        }
        catch
        {
            /* ignore */
        }

        return (verts, faces);
    }
}

/// <summary>Leaf widget: a model icon placeholder with a "renderer gated" note.</summary>
internal sealed class ModelPlaceholderWidget : Widget
{
    private readonly string _ext;
    private Size _size;
    private ThemeData _theme;

    public ModelPlaceholderWidget(string path, ThemeData theme)
    {
        _ext = Path.GetExtension(path).ToLowerInvariant();
        _theme = theme;
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = float.IsInfinity(c.MaxWidth) ? 240f : c.MaxWidth;
        _size = c.Constrain(new Size(w, 200f));
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
    }

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        paint.AddRect(Bounds, _theme.SurfaceAlt, 6f);
        paint.AddBorder(Bounds, _theme.Separator, 6f);

        var iconBox = new Rect(
            Bounds.X,
            Bounds.Y + Bounds.Height * 0.18f,
            Bounds.Width,
            64f
        );
        Icons.Draw(
            paint,
            Icons.Cube,
            iconBox,
            _theme.Primary.WithAlpha(0.85f),
            56f
        );

        var label = _ext.TrimStart('.').ToUpperInvariant() + " model";
        var lx = Bounds.X + (Bounds.Width - label.Length * 6.5f) * 0.5f;
        var ly = iconBox.Bottom + 18f;
        paint.AddText(
            label,
            lx,
            ly,
            _theme.OnSurface,
            _theme.FontSizeBody
        );

        const string note = "3D preview requires the renderer (currently gated)";
        var nx = Bounds.X + (Bounds.Width - note.Length * 5.0f) * 0.5f;
        var ny = ly + 20f;
        paint.AddText(
            note,
            MathF.Max(Bounds.X + 8f, nx),
            ny,
            _theme.TextMuted,
            _theme.FontSizeCaption
        );
    }
}