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

    public bool CanHandle(string ext) => Array.IndexOf(array: Exts, value: ext) >= 0;

    public Widget BuildPreview(string path, ThemeData theme)
    {
        var mesh = MeshLoader.Load(path);
        if (mesh is { } m && m.TriangleCount > 0)
            return new MeshPreviewWidget(mesh: m, theme: theme);

        return new ModelPlaceholderWidget(path: path, theme: theme);
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

        string ext = Path.GetExtension(path).ToLowerInvariant();

        // Cheap text-format counts where possible; binary formats stay opaque.
        if (ext == ".obj")
        {
            (int verts, int faces) = CountObj(path);
            if (verts > 0) yield return ("Vertices", verts.ToString());
            if (faces > 0) yield return ("Faces", faces.ToString());
        }
    }

    private static (int Verts, int Faces) CountObj(string path)
    {
        int verts = 0;
        int faces = 0;
        try
        {
            using var reader = new StreamReader(path);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.StartsWith(value: "v ", comparisonType: StringComparison.Ordinal)) verts++;
                else if (line.StartsWith(
                             value: "f ",
                             comparisonType: StringComparison.Ordinal
                         )) faces++;
            }
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
        float w = float.IsInfinity(c.MaxWidth) ? 240f : c.MaxWidth;
        _size = c.Constrain(new Size(width: w, height: 200f));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        paint.AddRect(bounds: Bounds, color: _theme.SurfaceAlt, radius: 6f);
        paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: 6f);

        var iconBox = new Rect(
            x: Bounds.X,
            y: Bounds.Y + (Bounds.Height * 0.18f),
            width: Bounds.Width,
            height: 64f
        );
        Icons.Draw(
            paint: paint,
            glyph: Icons.Cube,
            box: iconBox,
            color: _theme.Primary.WithAlpha(0.85f),
            size: 56f
        );

        string label = _ext.TrimStart('.').ToUpperInvariant() + " model";
        float lx = Bounds.X + ((Bounds.Width - (label.Length * 6.5f)) * 0.5f);
        float ly = iconBox.Bottom + 18f;
        paint.AddText(
            text: label,
            baselineX: lx,
            baselineY: ly,
            color: _theme.OnSurface,
            fontSize: _theme.FontSizeBody
        );

        const string note = "3D preview requires the renderer (currently gated)";
        float nx = Bounds.X + ((Bounds.Width - (note.Length * 5.0f)) * 0.5f);
        float ny = ly + 20f;
        paint.AddText(
            text: note,
            baselineX: MathF.Max(x: Bounds.X + 8f, y: nx),
            baselineY: ny,
            color: _theme.TextMuted,
            fontSize: _theme.FontSizeCaption
        );
    }
}
