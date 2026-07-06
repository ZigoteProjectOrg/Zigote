using System.Text.Json;
using System.Text.Json.Serialization;
using Zigote.Core.Math3D;
using Zigote.Game.Resources;

namespace Zigote.Runtime.Scene;

public sealed class SceneGraph
{
    [JsonInclude] public SceneNode Root { get; set; } = new("Scene");

    /// <summary>
    ///     Optional environment map (HDRI / equirectangular image) applied when the scene
    ///     loads. Relative to the working directory. Null = built-in procedural studio environment.
    /// </summary>
    [JsonInclude]
    public string? EnvironmentPath { get; set; }

    public void Save(string path)
    {
        // Detach editor-only nodes (transform gizmos, etc.) so they never leak into the
        // saved scene, then restore them afterwards.
        var detached = new List<(SceneNode Parent, int Index, SceneNode Node)>();
        CollectInternal(Root, detached);
        foreach (var (parent, _, node) in detached) parent.Children.Remove(node);
        try
        {
            var json = JsonSerializer.Serialize(this, MathJson.SceneOptions(true));
            File.WriteAllText(path, json);
        }
        finally
        {
            // Re-insert in original positions (collected in ascending index order per parent).
            foreach (var (parent, index, node) in detached)
                parent.Children.Insert(Math.Min(index, parent.Children.Count), node);
        }
    }

    private static void CollectInternal(SceneNode node, List<(SceneNode, int, SceneNode)> outList)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var c = node.Children[i];
            if (c.IsInternal) outList.Add((node, i, c)); // whole subtree removed as a unit
            else CollectInternal(c, outList);
        }
    }

    public static SceneGraph Load(string path)
    {
        if (!File.Exists(path)) return new SceneGraph();
        var json = File.ReadAllText(path);
        var graph = JsonSerializer.Deserialize<SceneGraph>(json, MathJson.SceneOptions(false));
        if (graph != null)
        {
            // Defensive: strip any editor-only nodes that older builds leaked into the file.
            StripLeakedInternal(graph.Root);
            // Restore parent references which might not be fully reconstructed by Preserve in some scenarios,
            // though Preserve does handle it if serialized correctly. It's safer to ensure parent linkage.
            RestoreParents(graph.Root, null);
        }

        return graph ?? new SceneGraph();
    }

    private static void StripLeakedInternal(SceneNode node)
    {
        node.Children.RemoveAll(c => c.Name.StartsWith("__Gizmo", StringComparison.Ordinal));
        foreach (var c in node.Children) StripLeakedInternal(c);
    }

    private static void RestoreParents(SceneNode node, SceneNode? parent)
    {
        node.Parent = parent;
        foreach (var child in node.Children)
            RestoreParents(child, node);
    }

    public static SceneGraph Demo()
    {
        var g = new SceneGraph();

        // Camera must be a direct child of Scene root so PushOrbitCamera writes
        // world-space positions without parent-offset correction.
        g.Root.AddChild(
            new SceneNode("Camera", NodeKind.Camera) {
                Position = new Vec3(3f, 2f, 7f),
            }
        );

        g.Root.AddChild(
            new SceneNode("Sun", NodeKind.Light) {
                LightKind = LightType.Directional,
                LightColor = new Vec3(1f, 0.95f, 0.88f),
                LightIntensity = 1.7f,
                Rotation = Quat.FromEuler((float)(-Math.PI / 3.5), (float)(Math.PI / 4), 0f),
            }
        );

        g.Root.AddChild(
            new SceneNode("Sky Fill", NodeKind.Light) {
                LightKind = LightType.Directional,
                LightColor = new Vec3(0.45f, 0.65f, 1.0f),
                LightIntensity = 0.35f,
                Rotation = Quat.FromEuler((float)(-Math.PI / 6), (float)(-Math.PI * 0.75f), 0f),
            }
        );

        g.Root.AddChild(
            new SceneNode("Ground", NodeKind.Mesh) {
                MeshPath = "#quad",
                Scale = new Vec3(20f, 1f, 20f),
                MeshColor = new Vec3(0.45f, 0.55f, 0.35f),
                MeshMetallic = 0.0f,
                MeshRoughness = 0.92f, // very rough ground
            }
        );

        g.Root.AddChild(
            new SceneNode("Cube", NodeKind.Mesh) {
                MeshPath = "#cube",
                Position = new Vec3(0f, 0.5f, 0f),
                MeshColor = new Vec3(0.85f, 0.40f, 0.20f),
                MeshMetallic = 0.0f,
                MeshRoughness = 0.65f, // rough clay/terracotta
                ScriptClass = "Samples.Scripting.Rotator",
            }
        );

        g.Root.AddChild(
            new SceneNode("Sphere", NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(2.5f, 0.5f, -1f),
                MeshColor = new Vec3(0.80f, 0.82f, 0.85f),
                MeshMetallic = 0.92f,
                MeshRoughness = 0.08f, // polished metal
            }
        );

        g.Root.AddChild(
            new SceneNode("CRT TV Screen", NodeKind.Mesh) {
                MeshPath = "#quad",
                Position = new Vec3(-2.5f, 1.2f, -1f),
                Scale = new Vec3(2f, 1.5f, 1f),
                Rotation = Quat.FromEuler(0f, (float)(Math.PI / 6f), 0f),
                MeshColor = new Vec3(1f, 1f, 1f),
                MeshMetallic = 0.0f,
                MeshRoughness = 0.9f,
                TexturePath = "assets/image0.webp",
                MeshEffect = RenderEffect.CrtTv,
            }
        );

        return g;
    }

    /// <summary>
    ///     A material showcase: a row of spheres with distinct materials (glass, white dielectric,
    ///     chrome mirror, blue clearcoat paint, gold metal, rough rubber) on a neutral floor, lit by
    ///     an HDRI environment — demonstrates the image-based-lighting / material pipeline.
    /// </summary>
    public static SceneGraph MaterialBalls()
    {
        var g = new SceneGraph {
            // Auto-loaded on scene activation (converted from the supplied .exr; see assets/hdri).
            // Relative to the project dir (the editor sets cwd to the project root on launch).
            EnvironmentPath = "assets/hdri/german_town_street_4k.hdr",
        };

        g.Root.AddChild(
            new SceneNode("Camera", NodeKind.Camera) {
                Position = new Vec3(0f, 1.6f, 9f),
            }
        );

        // A soft key light; the HDRI provides most of the lighting + reflections.
        g.Root.AddChild(
            new SceneNode("Sun", NodeKind.Light) {
                LightKind = LightType.Directional,
                LightColor = new Vec3(1f, 0.97f, 0.92f),
                LightIntensity = 1.2f,
                Rotation = Quat.FromEuler((float)(-Math.PI / 3.5), (float)(Math.PI / 4), 0f),
            }
        );

        g.Root.AddChild(
            new SceneNode("Ground", NodeKind.Mesh) {
                MeshPath = "#quad",
                Scale = new Vec3(30f, 1f, 30f),
                MeshColor = new Vec3(0.35f, 0.35f, 0.36f),
                MeshMetallic = 0.0f,
                MeshRoughness = 0.55f,
            }
        );

        const float r = 0.7f; // sphere radius (the #sphere primitive is unit-ish; y = radius)
        var x = -5f;
        const float step = 2.5f;

        // 1) Glass — transmissive/reflective.
        g.Root.AddChild(
            new SceneNode("Glass", NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x, r, 0f),
                MeshColor = new Vec3(0.9f, 0.95f, 1f),
                MeshMetallic = 0f,
                MeshRoughness = 0.04f,
                MeshAlphaMode = 3, // glass path
            }
        );
        x += step;

        // 2) White dielectric — matte reference (shows diffuse IBL).
        g.Root.AddChild(
            new SceneNode("White Diffuse", NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x, r, 0f),
                MeshColor = new Vec3(0.9f, 0.9f, 0.9f),
                MeshMetallic = 0f,
                MeshRoughness = 0.85f,
            }
        );
        x += step;

        // 3) Chrome — perfect mirror (shows the prefiltered specular environment).
        g.Root.AddChild(
            new SceneNode("Chrome", NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x, r, 0f),
                MeshColor = new Vec3(0.95f, 0.95f, 0.96f),
                MeshMetallic = 1f,
                MeshRoughness = 0.02f,
            }
        );
        x += step;

        // 4) Blue clearcoat paint — glossy dielectric with a coat lobe.
        g.Root.AddChild(
            new SceneNode("Blue Paint", NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x, r, 0f),
                MeshColor = new Vec3(0.10f, 0.22f, 0.55f),
                MeshMetallic = 0f,
                MeshRoughness = 0.18f,
                MeshClearcoat = 1f,
                MeshClearcoatRoughness = 0.03f,
            }
        );
        x += step;

        // 5) Gold — coloured rough metal (shows multi-scatter energy + tinted F0).
        g.Root.AddChild(
            new SceneNode("Gold", NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x, r, 0f),
                MeshColor = new Vec3(1.0f, 0.78f, 0.34f),
                MeshMetallic = 1f,
                MeshRoughness = 0.28f,
            }
        );

        return g;
    }
}