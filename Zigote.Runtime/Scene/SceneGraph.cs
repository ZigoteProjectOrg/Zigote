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
        CollectInternal(node: Root, outList: detached);
        foreach (var (parent, _, node) in detached) parent.Children.Remove(node);
        try
        {
            string json = JsonSerializer.Serialize(
                value: this,
                options: MathJson.SceneOptions(true)
            );
            File.WriteAllText(path: path, contents: json);
        }
        finally
        {
            // Re-insert in original positions (collected in ascending index order per parent).
            foreach ((var parent, int index, var node) in detached)
            {
                parent.Children.Insert(
                    index: Math.Min(val1: index, val2: parent.Children.Count),
                    item: node
                );
            }
        }
    }

    private static void CollectInternal(SceneNode node, List<(SceneNode, int, SceneNode)> outList)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            var c = node.Children[i];
            if (c.IsInternal) outList.Add((node, i, c)); // whole subtree removed as a unit
            else CollectInternal(node: c, outList: outList);
        }
    }

    public static SceneGraph Load(string path)
    {
        if (!File.Exists(path)) return new SceneGraph();
        string json = File.ReadAllText(path);
        var graph = JsonSerializer.Deserialize<SceneGraph>(
            json: json,
            options: MathJson.SceneOptions(false)
        );
        if (graph != null)
        {
            // Defensive: strip any editor-only nodes that older builds leaked into the file.
            StripLeakedInternal(graph.Root);
            // Restore parent references which might not be fully reconstructed by Preserve in some scenarios,
            // though Preserve does handle it if serialized correctly. It's safer to ensure parent linkage.
            RestoreParents(node: graph.Root, parent: null);
        }

        return graph ?? new SceneGraph();
    }

    private static void StripLeakedInternal(SceneNode node)
    {
        node.Children.RemoveAll(c => c.Name.StartsWith(
                value: "__Gizmo",
                comparisonType: StringComparison.Ordinal
            )
        );
        foreach (var c in node.Children) StripLeakedInternal(c);
    }

    private static void RestoreParents(SceneNode node, SceneNode? parent)
    {
        node.Parent = parent;
        foreach (var child in node.Children)
            RestoreParents(node: child, parent: node);
    }

    public static SceneGraph Demo()
    {
        var g = new SceneGraph();

        // Camera must be a direct child of Scene root so PushOrbitCamera writes
        // world-space positions without parent-offset correction.
        g.Root.AddChild(
            new SceneNode(name: "Camera", kind: NodeKind.Camera) {
                Position = new Vec3(x: 3f, y: 2f, z: 7f),
            }
        );

        g.Root.AddChild(
            new SceneNode(name: "Sun", kind: NodeKind.Light) {
                LightKind = LightType.Directional,
                LightColor = new Vec3(x: 1f, y: 0.95f, z: 0.88f),
                LightIntensity = 1.7f,
                Rotation = Quat.FromEuler(
                    pitch: (float)(-Math.PI / 3.5),
                    yaw: (float)(Math.PI / 4),
                    roll: 0f
                ),
            }
        );

        g.Root.AddChild(
            new SceneNode(name: "Sky Fill", kind: NodeKind.Light) {
                LightKind = LightType.Directional,
                LightColor = new Vec3(x: 0.45f, y: 0.65f, z: 1.0f),
                LightIntensity = 0.35f,
                Rotation = Quat.FromEuler(
                    pitch: (float)(-Math.PI / 6),
                    yaw: (float)(-Math.PI * 0.75f),
                    roll: 0f
                ),
            }
        );

        g.Root.AddChild(
            new SceneNode(name: "Ground", kind: NodeKind.Mesh) {
                MeshPath = "#quad",
                Scale = new Vec3(x: 20f, y: 1f, z: 20f),
                MeshColor = new Vec3(x: 0.45f, y: 0.55f, z: 0.35f),
                MeshMetallic = 0.0f,
                MeshRoughness = 0.92f, // very rough ground
            }
        );

        g.Root.AddChild(
            new SceneNode(name: "Cube", kind: NodeKind.Mesh) {
                MeshPath = "#cube",
                Position = new Vec3(x: 0f, y: 0.5f, z: 0f),
                MeshColor = new Vec3(x: 0.85f, y: 0.40f, z: 0.20f),
                MeshMetallic = 0.0f,
                MeshRoughness = 0.65f, // rough clay/terracotta
                ScriptClass = "Samples.Scripting.Rotator",
            }
        );

        g.Root.AddChild(
            new SceneNode(name: "Sphere", kind: NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x: 2.5f, y: 0.5f, z: -1f),
                MeshColor = new Vec3(x: 0.80f, y: 0.82f, z: 0.85f),
                MeshMetallic = 0.92f,
                MeshRoughness = 0.08f, // polished metal
            }
        );

        g.Root.AddChild(
            new SceneNode(name: "CRT TV Screen", kind: NodeKind.Mesh) {
                MeshPath = "#quad",
                Position = new Vec3(x: -2.5f, y: 1.2f, z: -1f),
                Scale = new Vec3(x: 2f, y: 1.5f, z: 1f),
                Rotation = Quat.FromEuler(pitch: 0f, yaw: (float)(Math.PI / 6f), roll: 0f),
                MeshColor = new Vec3(x: 1f, y: 1f, z: 1f),
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
            new SceneNode(name: "Camera", kind: NodeKind.Camera) {
                Position = new Vec3(x: 0f, y: 1.6f, z: 9f),
            }
        );

        // A soft key light; the HDRI provides most of the lighting + reflections.
        g.Root.AddChild(
            new SceneNode(name: "Sun", kind: NodeKind.Light) {
                LightKind = LightType.Directional,
                LightColor = new Vec3(x: 1f, y: 0.97f, z: 0.92f),
                LightIntensity = 1.2f,
                Rotation = Quat.FromEuler(
                    pitch: (float)(-Math.PI / 3.5),
                    yaw: (float)(Math.PI / 4),
                    roll: 0f
                ),
            }
        );

        g.Root.AddChild(
            new SceneNode(name: "Ground", kind: NodeKind.Mesh) {
                MeshPath = "#quad",
                Scale = new Vec3(x: 30f, y: 1f, z: 30f),
                MeshColor = new Vec3(x: 0.35f, y: 0.35f, z: 0.36f),
                MeshMetallic = 0.0f,
                MeshRoughness = 0.55f,
            }
        );

        const float r = 0.7f; // sphere radius (the #sphere primitive is unit-ish; y = radius)
        float x = -5f;
        const float step = 2.5f;

        // 1) Glass — transmissive/reflective.
        g.Root.AddChild(
            new SceneNode(name: "Glass", kind: NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x: x, y: r, z: 0f),
                MeshColor = new Vec3(x: 0.9f, y: 0.95f, z: 1f),
                MeshMetallic = 0f,
                MeshRoughness = 0.04f,
                MeshAlphaMode = 3, // glass path
            }
        );
        x += step;

        // 2) White dielectric — matte reference (shows diffuse IBL).
        g.Root.AddChild(
            new SceneNode(name: "White Diffuse", kind: NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x: x, y: r, z: 0f),
                MeshColor = new Vec3(x: 0.9f, y: 0.9f, z: 0.9f),
                MeshMetallic = 0f,
                MeshRoughness = 0.85f,
            }
        );
        x += step;

        // 3) Chrome — perfect mirror (shows the prefiltered specular environment).
        g.Root.AddChild(
            new SceneNode(name: "Chrome", kind: NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x: x, y: r, z: 0f),
                MeshColor = new Vec3(x: 0.95f, y: 0.95f, z: 0.96f),
                MeshMetallic = 1f,
                MeshRoughness = 0.02f,
            }
        );
        x += step;

        // 4) Blue clearcoat paint — glossy dielectric with a coat lobe.
        g.Root.AddChild(
            new SceneNode(name: "Blue Paint", kind: NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x: x, y: r, z: 0f),
                MeshColor = new Vec3(x: 0.10f, y: 0.22f, z: 0.55f),
                MeshMetallic = 0f,
                MeshRoughness = 0.18f,
                MeshClearcoat = 1f,
                MeshClearcoatRoughness = 0.03f,
            }
        );
        x += step;

        // 5) Gold — coloured rough metal (shows multi-scatter energy + tinted F0).
        g.Root.AddChild(
            new SceneNode(name: "Gold", kind: NodeKind.Mesh) {
                MeshPath = "#sphere",
                Position = new Vec3(x: x, y: r, z: 0f),
                MeshColor = new Vec3(x: 1.0f, y: 0.78f, z: 0.34f),
                MeshMetallic = 1f,
                MeshRoughness = 0.28f,
            }
        );

        return g;
    }
}
