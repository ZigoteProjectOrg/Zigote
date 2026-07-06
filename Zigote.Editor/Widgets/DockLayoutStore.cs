using System.Text.Json;

namespace Zigote.Editor.Widgets;

/// <summary>
///     Persists a <see cref="DockNode" /> dock tree to a per-project
///     <c>&lt;project&gt;.layout.json</c>
///     next to the .zigoteproj. Unknown panel ids are dropped on load (so layouts survive panels
///     being renamed/removed), and an unreadable file simply falls back to the default layout.
/// </summary>
public static class DockLayoutStore
{
    public static string PathFor(string projectFile)
    {
        return Path.ChangeExtension(projectFile, ".layout.json");
    }

    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    public static void Save(string projectFile, DockNode root)
    {
        try
        {
            var json = JsonSerializer.Serialize(ToDto(root), SaveOptions);
            File.WriteAllText(PathFor(projectFile), json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Layout] save failed: {ex.Message}");
        }
    }

    public static DockNode? Load(string projectFile, IReadOnlySet<string> knownPanels)
    {
        try
        {
            var path = PathFor(projectFile);
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
            return dto == null ? null : FromDto(dto, knownPanels);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Layout] load failed: {ex.Message}");
            return null;
        }
    }

    private static Dto ToDto(DockNode node)
    {
        return node switch {
            DockLeaf l => new Dto {
                Panels = l.PanelIds.ToList(),
                Active = l.ActiveIndex,
                Collapsed = l.Collapsed,
            },
            DockSplit s => new Dto {
                First = ToDto(s.First),
                Second = ToDto(s.Second),
                Vertical = s.Vertical,
                Ratio = s.Ratio,
            },
            _ => new Dto(),
        };
    }

    /// <summary>
    ///     Rebuild a node, dropping unknown panels and collapsing empty subtrees (returns null if
    ///     empty).
    /// </summary>
    private static DockNode? FromDto(Dto dto, IReadOnlySet<string> known)
    {
        if (dto.Panels is { Count: > 0 })
        {
            var ids = dto.Panels.Where(known.Contains).Distinct().ToList();
            if (ids.Count == 0) return null;
            return new DockLeaf(ids) {
                ActiveIndex = Math.Clamp(dto.Active, 0, ids.Count - 1),
                Collapsed = dto.Collapsed,
            };
        }

        if (dto.First != null && dto.Second != null)
        {
            var f = FromDto(dto.First, known);
            var s = FromDto(dto.Second, known);
            if (f == null) return s;
            if (s == null) return f;
            return new DockSplit(
                f,
                s,
                dto.Vertical,
                dto.Ratio
            );
        }

        return null;
    }

    private sealed class Dto
    {
        public List<string>? Panels { get; set; } // leaf
        public int Active { get; set; }
        public bool Collapsed { get; set; }
        public Dto? First { get; set; } // split
        public Dto? Second { get; set; }
        public bool Vertical { get; set; }
        public float Ratio { get; set; }
    }
}