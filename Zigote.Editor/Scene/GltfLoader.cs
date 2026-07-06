using System.Text.Json;
using Zigote.Core.Engine;
using Zigote.Core.Math3D;
using Zigote.Editor.Animation;
using Zigote.Game.Resources;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.Scene;

/// <summary>
///     Imports a 3D model into a <see cref="SceneNode" /> hierarchy. Despite the name, this handles
///     <b>any format Assimp understands</b> — glTF/GLB, FBX, OBJ, DAE, PLY, STL, 3DS and more.
///     Parsing happens natively. The Zig/Assimp importer (<c>zigote_model_import</c>) loads the
///     file, processes the geometry (triangulate, generate normals/tangents), writes one
///     <c>.zmesh</c> binary per mesh plus any extracted textures into a per-source
///     <c>.mesh_cache</c> directory, and returns a JSON manifest. This loader consumes the manifest
///     to build the node tree and apply the editor's material heuristics.
///     Static models are <b>flattened by material</b> (one mesh node per material, geometry baked
///     into world space) — far fewer GPU uploads and per-frame syncs. Animated models
///     <b>
///         preserve
///         the node hierarchy
///     </b>
///     so animation channels bind to nodes by name.
/// </summary>
public static class GltfLoader
{
    // Assimp reads far more than this, but these are the extensions we surface as drag-droppable.
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) {
            ".glb",
            ".gltf",
            ".fbx",
            ".obj",
            ".dae",
            ".ply",
            ".stl",
            ".3ds",
            ".blend",
            ".x",
            ".ms3d",
            ".ase",
            ".ifc",
            ".lwo",
            ".lxo",
            ".dxf",
            ".off",
            ".3mf",
            ".gltf",
        };

    private static readonly JsonSerializerOptions JsonOpts = new() {
        PropertyNameCaseInsensitive = true,
    };

    public static bool IsSupported(string path)
    {
        return !string.IsNullOrEmpty(path) && SupportedExtensions.Contains(Path.GetExtension(path));
    }

    public static SceneNode Load(string path)
    {
        return Load(path, out _);
    }

    /// <summary>
    ///     Import the model at <paramref name="path" /> and return a root <see cref="SceneNode" />,
    ///     plus a <see cref="GltfImportReport" /> of object counts and warnings.
    /// </summary>
    public static SceneNode Load(string path, out GltfImportReport report)
    {
        var baseName = Path.GetFileNameWithoutExtension(path);
        report = new GltfImportReport { SourceName = Path.GetFileName(path) };

        var fullPath = Path.GetFullPath(path);
        var cacheDir = Path.Combine(Path.GetDirectoryName(fullPath)!, ".mesh_cache");
        Directory.CreateDirectory(cacheDir);

        var engine = ZigoteEngine.Instance
                     ?? throw new InvalidOperationException(
                         "Engine not initialised; cannot import model."
                     );

        var json = engine.ModelImport(fullPath, cacheDir)
                   ?? throw new InvalidOperationException(
                       $"Assimp failed to import '{Path.GetFileName(path)}'."
                   );

        var manifest = JsonSerializer.Deserialize<ModelManifest>(json, JsonOpts)
                       ?? throw new InvalidOperationException(
                           "Model import returned an empty manifest."
                       );

        report.Scenes = 1;
        report.Meshes = manifest.Counts.Meshes;
        report.Materials = manifest.Counts.Materials;
        report.Textures = manifest.Counts.Textures;
        report.Nodes = manifest.Counts.Nodes;
        report.Primitives = manifest.Counts.Primitives;
        foreach (var w in manifest.Warnings) report.Warn(w);

        var root = new SceneNode(baseName);

        if (manifest.Animated)
        {
            // Hierarchy preserved so animation channels bind to nodes by name.
            foreach (var n in manifest.Nodes)
                root.AddChild(BuildHierarchicalNode(n, manifest.Materials, report));

            var clips = GltfAnimationImporter.Import(manifest.Animations);
            root.Animations.AddRange(clips);
            if (clips.Count > 0)
                report.Warn(
                    $"Imported {clips.Count} animation clip(s); hierarchy preserved for binding."
                );
        }
        else
        {
            // One flattened mesh node per material (geometry already world-baked into the .zmesh).
            foreach (var mn in manifest.MeshNodes)
            {
                if (mn.Material < 0)
                    report.Warn(
                        "Some primitives have no material; imported with a default dielectric fallback."
                    );
                var node = new SceneNode(
                    string.IsNullOrWhiteSpace(mn.Name) ? $"Material {mn.Material}" : mn.Name,
                    NodeKind.Mesh
                ) {
                    MeshPath = mn.Cache,
                };
                ApplyMaterial(node, MaterialAt(manifest.Materials, mn.Material), report);
                root.AddChild(node);
            }
        }

        // KHR_lights_punctual (and equivalents in other formats) — imported as Light nodes.
        foreach (var light in manifest.Lights)
            root.AddChild(BuildLightNode(light));

        // The native manifest hands back absolute cache/texture paths; store the canonical
        // project-relative form so scenes stay portable across machines (cwd = project dir).
        var rpt = report;
        ScenePaths.Normalize(
            root,
            Directory.GetCurrentDirectory(),
            w => rpt.Warn($"unportable path: {w}")
        );

        return root;
    }

    // ── Node construction ───────────────────────────────────────────────────────

    private static SceneNode BuildHierarchicalNode(ModelNode n, ModelMaterial[] materials,
        GltfImportReport report)
    {
        var node = new SceneNode(n.Name) {
            Position = Vec(n.Translation),
            Rotation = Quat4(n.Rotation),
            Scale = n.Scale.Length >= 3 ? Vec(n.Scale) : Vec3.One,
        };

        var p = 0;
        foreach (var mref in n.Meshes)
        {
            var primNode =
                new SceneNode($"{n.Name}#prim{p++}", NodeKind.Mesh) { MeshPath = mref.Cache };
            ApplyMaterial(primNode, MaterialAt(materials, mref.Material), report);
            node.AddChild(primNode);
        }

        foreach (var child in n.Children)
            node.AddChild(BuildHierarchicalNode(child, materials, report));

        return node;
    }

    private static SceneNode BuildLightNode(ModelLight light)
    {
        var kind = light.Kind switch {
            "directional" => LightType.Directional,
            "spot" => LightType.Spot,
            _ => LightType.Point,
        };
        return new SceneNode(
            string.IsNullOrWhiteSpace(light.Name) ? $"{kind} Light" : light.Name,
            NodeKind.Light
        ) {
            Position = Vec(light.Position),
            Rotation = Quat4(light.Rotation),
            LightKind = kind,
            LightColor = Vec(light.Color),
            LightIntensity = light.Intensity,
            LightRange = light.Range > 0f ? light.Range : 0f,
        };
    }

    // ── Material ──────────────────────────────────────────────────────────────

    private static ModelMaterial? MaterialAt(ModelMaterial[] mats, int idx)
    {
        return idx >= 0 && idx < mats.Length ? mats[idx] : null;
    }

    private static void ApplyMaterial(SceneNode node, ModelMaterial? mat, GltfImportReport report)
    {
        if (mat is null) return;

        node.MeshColor = new Vec3(mat.BaseColor[0], mat.BaseColor[1], mat.BaseColor[2]);
        if (mat.BaseColorTexture is not null) node.TexturePath = mat.BaseColorTexture;

        // Per-pixel metalness/roughness (glTF convention: roughness in G, metallic in B).
        if (mat.HasMetallicRoughness)
        {
            node.MeshMetallic = mat.Metallic;
            node.MeshRoughness = mat.Roughness;
            if (mat.MetallicRoughnessTexture is not null)
                node.MetallicRoughnessTexturePath = mat.MetallicRoughnessTexture;
        }

        // Tangent-space normal map (linear). When a material ships no normal map we fall back to a
        // plain dielectric — stylised / unlit / MToon surfaces otherwise read as mirror metal from
        // the glTF metallic=1 default, killing their base colour. (Mirrors the original loader.)
        if (mat.NormalTexture is not null)
        {
            node.NormalTexturePath = mat.NormalTexture;
        }
        else
        {
            node.MeshMetallic = 0f;
            node.MeshRoughness = 0.6f;
        }

        // Mis-export heuristic: a base-colour texture but no MR map yet metallic ~1 (the unset glTF
        // default) is almost always stylised/dielectric art — force dielectric so its albedo shows.
        if (node.MetallicRoughnessTexturePath is null &&
            node.TexturePath is not null &&
            node.MeshMetallic > 0.5f)
        {
            node.MeshMetallic = 0f;
            if (node.MeshRoughness > 0.9f) node.MeshRoughness = 0.6f;
            report.Warn(
                $"Material '{mat.Name}' looked fully metallic with no metallic-roughness map; treated as dielectric so its albedo shows."
            );
        }

        // Unlit (KHR_materials_unlit) — flat-shaded toon/VRM materials route through the unlit effect.
        if (mat.Unlit) node.MeshEffect = RenderEffect.Unlit;

        // Extended PBR (clearcoat / specular / IOR). The shader now derives the dielectric F0 from
        // the REAL index of refraction (((n-1)/(n+1))², scaled by the specular factor), so both flow
        // through unfolded — 1.5 reproduces the classic 0.04 base exactly.
        node.MeshClearcoat = mat.Clearcoat;
        node.MeshClearcoatRoughness = mat.ClearcoatRoughness;
        node.MeshIor = mat.Ior;
        node.MeshSpecular = mat.Specular;

        // Geometry/alpha flags that used to be parsed then dropped.
        node.MeshDoubleSided = mat.DoubleSided;
        node.MeshAlphaCutoff = mat.AlphaCutoff;

        // Emissive (KHR_materials_emissive_strength already split out by the importer).
        node.MeshEmissive = new Vec3(
            mat.Emissive[0] * mat.EmissiveStrength,
            mat.Emissive[1] * mat.EmissiveStrength,
            mat.Emissive[2] * mat.EmissiveStrength
        );
        if (mat.EmissiveTexture is not null)
        {
            node.EmissiveTexturePath = mat.EmissiveTexture;
            // Some exporters leave emissiveFactor at the [0,0,0] glTF default even with an emissive
            // texture attached; a black factor would erase the map, so treat it as white.
            if (node.MeshEmissive.ApproxEquals(Vec3.Zero))
            {
                node.MeshEmissive = new Vec3(
                    mat.EmissiveStrength,
                    mat.EmissiveStrength,
                    mat.EmissiveStrength
                );
                report.Warn(
                    $"Material '{mat.Name}' has an emissive texture but a black emissive factor; treated the factor as white so the map shows."
                );
            }
        }

        // Baked occlusion. glTF packs AO into the ORM texture's R channel — when the occlusion map
        // IS the metallic-roughness map, enable the shader's ORM read. A separate AO image has no
        // dedicated sampler slot yet.
        if (mat.OcclusionTexture is not null)
        {
            if (mat.OcclusionTexture == mat.MetallicRoughnessTexture)
                node.MeshOcclusionStrength = 1f;
            else
                report.Warn(
                    $"Material '{mat.Name}' uses a standalone occlusion texture; only ORM-packed occlusion (shared with the metallic-roughness map) is supported — skipped."
                );
        }

        // Transparency. BLEND is generic alpha (decals, stickers, hair, cloth, toon). The dedicated
        // glass path triggers on a real KHR_materials_transmission factor regardless of alpha mode
        // (transmissive glTFs are usually authored OPAQUE), or on glass-like names under BLEND.
        var isGlass = mat.Transmission > 0f || (mat.AlphaMode == "BLEND" && IsGlassMaterial(mat));
        if (isGlass)
        {
            node.MeshAlphaMode = 3; // glass: refractive + reflective, depth-sorted
            node.MeshTransmission = mat.Transmission > 0f ? mat.Transmission : 1f;
            node.MeshMetallic = 0f;
            // Honour authored roughness (frosted glass); the no-MR default would read frosty.
            if (!mat.HasMetallicRoughness) node.MeshRoughness = 0.05f;
            else node.MeshRoughness = mat.Roughness;
        }
        else
        {
            switch (mat.AlphaMode)
            {
                case "BLEND" when mat.Unlit:
                    node.MeshAlphaMode =
                        1; // toon/VRM cutout (depth-written, no self-sorting artifacts)
                    break;
                case "BLEND":
                    node.MeshAlphaMode = 2; // generic alpha blend
                    break;
                case "MASK":
                    node.MeshAlphaMode = 1;
                    break;
                default:
                    node.MeshAlphaMode = 0;
                    break;
            }
        }
    }

    private static bool IsGlassMaterial(ModelMaterial mat)
    {
        if (mat.Transmission > 0f) return true;
        var name = mat.Name ?? string.Empty;
        return name.Contains("glass", StringComparison.OrdinalIgnoreCase)
               || name.Contains("window", StringComparison.OrdinalIgnoreCase)
               || name.Contains("windshield", StringComparison.OrdinalIgnoreCase)
               || name.Contains("lens", StringComparison.OrdinalIgnoreCase);
    }

    private static Vec3 Vec(float[] a)
    {
        return a.Length >= 3 ? new Vec3(a[0], a[1], a[2]) : Vec3.Zero;
    }

    private static Quat Quat4(float[] a)
    {
        return a.Length >= 4
            ? new Quat(
                a[0],
                a[1],
                a[2],
                a[3]
            )
            : Quat.Identity;
    }
}