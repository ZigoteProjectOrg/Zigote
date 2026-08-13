namespace Zigote.Editor.Scene;

/// <summary>
///     C# mirror of the JSON manifest produced by the native Assimp importer
///     (<c>Zigote.Engine/src/ffi/assimp_loader.zig</c>). Deserialized with
///     <c>PropertyNameCaseInsensitive</c>, so these PascalCase members bind to the importer's
///     camelCase keys. Geometry and textures live on disk (referenced by path); only the structural
///     description travels through JSON.
/// </summary>
internal sealed class ModelManifest
{
    /// <summary>True → hierarchical <see cref="Nodes" /> path (animated); false → flat <see cref="MeshNodes" />.</summary>
    public bool Animated { get; set; }

    public ModelCounts Counts { get; set; } = new();
    public string[] Warnings { get; set; } = [];
    public ModelNode[] Nodes { get; set; } = [];
    public ModelMeshNode[] MeshNodes { get; set; } = [];
    public ModelMaterial[] Materials { get; set; } = [];
    public ModelLight[] Lights { get; set; } = [];
    public ModelAnimation[] Animations { get; set; } = [];
}

internal sealed class ModelCounts
{
    public int Meshes { get; set; }
    public int Materials { get; set; }
    public int Textures { get; set; }
    public int Lights { get; set; }
    public int Animations { get; set; }
    public int Nodes { get; set; }
    public int Primitives { get; set; }
}

/// <summary>A flattened-by-material mesh node (static import). <see cref="Cache" /> is a world-space `.zmesh`.</summary>
internal sealed class ModelMeshNode
{
    public string Name { get; set; } = "";
    public string Cache { get; set; } = "";
    public int Material { get; set; }
}

/// <summary>A mesh inside a hierarchical node (animated import). <see cref="Cache" /> is a local-space `.zmesh`.</summary>
internal sealed class ModelMeshRef
{
    public string Cache { get; set; } = "";
    public int Material { get; set; }
}

internal sealed class ModelNode
{
    public string Name { get; set; } = "";
    public float[] Translation { get; set; } = [];
    public float[] Rotation { get; set; } = []; // x, y, z, w
    public float[] Scale { get; set; } = [];
    public ModelMeshRef[] Meshes { get; set; } = [];
    public ModelNode[] Children { get; set; } = [];
}

internal sealed class ModelMaterial
{
    public string Name { get; set; } = "";
    public float[] BaseColor { get; set; } = [1, 1, 1, 1];
    public float Metallic { get; set; }
    public float Roughness { get; set; } = 0.5f;
    public bool HasMetallicRoughness { get; set; }
    public float[] Emissive { get; set; } = [0, 0, 0];
    public float EmissiveStrength { get; set; } = 1f;
    public string AlphaMode { get; set; } = "OPAQUE";
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool DoubleSided { get; set; }
    public bool Unlit { get; set; }
    public float Clearcoat { get; set; }
    public float ClearcoatRoughness { get; set; }
    public float Ior { get; set; } = 1.5f;
    public float Specular { get; set; } = 1f;
    public float Transmission { get; set; }

    // KHR_materials_volume / _sheen — forward-compat (sheen is not rendered yet).
    public float Thickness { get; set; }
    public float[] SheenColor { get; set; } = [0, 0, 0];
    public float SheenRoughness { get; set; }
    public string? BaseColorTexture { get; set; }
    public string? MetallicRoughnessTexture { get; set; }
    public string? NormalTexture { get; set; }
    public string? EmissiveTexture { get; set; }
    public string? OcclusionTexture { get; set; }
}

internal sealed class ModelLight
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "point"; // directional | point | spot
    public float[] Color { get; set; } = [1, 1, 1];
    public float Intensity { get; set; } = 1f;
    public float Range { get; set; }
    public float[] Position { get; set; } = [];
    public float[] Rotation { get; set; } = [];
}

internal sealed class ModelChannel
{
    public string Node { get; set; } = "";
    public string Path { get; set; } = ""; // translation | rotation | scale
    public string Interpolation { get; set; } = "LINEAR";
    public float[] Times { get; set; } = [];
    public float[] Values { get; set; } = []; // flattened: vec3 → 3/key, quat (x,y,z,w) → 4/key
}

internal sealed class ModelAnimation
{
    public string Name { get; set; } = "";
    public ModelChannel[] Channels { get; set; } = [];
}
