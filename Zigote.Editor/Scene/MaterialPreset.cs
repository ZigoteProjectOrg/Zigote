using Zigote.Core.Math3D;
using Zigote.Game.Resources;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.Scene;

/// <summary>
///     A named material finish that maps onto a <see cref="SceneNode" />'s existing PBR fields (no
///     native
///     change). Use to one-click a car body to Car Paint / Chrome / Glass / Matte / Emissive, etc.
///     <see cref="KeepNodeColor" /> presets change only the finish so the node keeps its painted
///     colour.
/// </summary>
public sealed record MaterialPreset(
    string Name,
    Vec3 Color,
    float Metallic,
    float Roughness,
    float Clearcoat,
    float ClearcoatRoughness,
    float Specular,
    Vec3 Emissive,
    uint AlphaMode,
    RenderEffect Effect,
    bool KeepNodeColor)
{
    public void ApplyTo(SceneNode n)
    {
        if (!KeepNodeColor) n.MeshColor = Color;
        n.MeshMetallic = Metallic;
        n.MeshRoughness = Roughness;
        n.MeshClearcoat = Clearcoat;
        n.MeshClearcoatRoughness = ClearcoatRoughness;
        n.MeshSpecular = Specular;
        n.MeshEmissive = Emissive;
        n.MeshAlphaMode = AlphaMode;
        n.MeshEffect = Effect;
    }
}

public static class MaterialPresets
{
    public static readonly MaterialPreset CarPaint = new(
        Name: "Car Paint",
        Color: new Vec3(x: 0.72f, y: 0.05f, z: 0.06f),
        Metallic: 0.9f,
        Roughness: 0.30f,
        Clearcoat: 1.0f,
        ClearcoatRoughness: 0.05f,
        Specular: 1.0f,
        Emissive: Vec3.Zero,
        AlphaMode: 0,
        Effect: RenderEffect.Standard,
        KeepNodeColor: true
    );

    public static readonly MaterialPreset Chrome = new(
        Name: "Chrome",
        Color: new Vec3(x: 0.90f, y: 0.90f, z: 0.92f),
        Metallic: 1.0f,
        Roughness: 0.04f,
        Clearcoat: 0f,
        ClearcoatRoughness: 0f,
        Specular: 1.0f,
        Emissive: Vec3.Zero,
        AlphaMode: 0,
        Effect: RenderEffect.Standard,
        KeepNodeColor: false
    );

    public static readonly MaterialPreset Glass = new(
        Name: "Glass",
        Color: new Vec3(x: 0.85f, y: 0.92f, z: 0.95f),
        Metallic: 0f,
        Roughness: 0.04f,
        Clearcoat: 1.0f,
        ClearcoatRoughness: 0.03f,
        Specular: 1.0f,
        Emissive: Vec3.Zero,
        AlphaMode: 2 /* Blend */,
        Effect: RenderEffect.Standard,
        KeepNodeColor: false
    );

    public static readonly MaterialPreset Matte = new(
        Name: "Matte",
        Color: new Vec3(x: 0.5f, y: 0.5f, z: 0.5f),
        Metallic: 0f,
        Roughness: 0.9f,
        Clearcoat: 0f,
        ClearcoatRoughness: 0f,
        Specular: 0.5f,
        Emissive: Vec3.Zero,
        AlphaMode: 0,
        Effect: RenderEffect.Standard,
        KeepNodeColor: true
    );

    public static readonly MaterialPreset Plastic = new(
        Name: "Plastic",
        Color: new Vec3(x: 0.5f, y: 0.5f, z: 0.5f),
        Metallic: 0f,
        Roughness: 0.45f,
        Clearcoat: 0.5f,
        ClearcoatRoughness: 0.1f,
        Specular: 0.7f,
        Emissive: Vec3.Zero,
        AlphaMode: 0,
        Effect: RenderEffect.Standard,
        KeepNodeColor: true
    );

    public static readonly MaterialPreset Emissive = new(
        Name: "Emissive",
        Color: new Vec3(x: 1f, y: 1f, z: 1f),
        Metallic: 0f,
        Roughness: 1.0f,
        Clearcoat: 0f,
        ClearcoatRoughness: 0f,
        Specular: 0.5f,
        Emissive: new Vec3(x: 2f, y: 2f, z: 2f),
        AlphaMode: 0,
        Effect: RenderEffect.Standard,
        KeepNodeColor: false
    );

    public static readonly IReadOnlyList<MaterialPreset> All =
        [CarPaint, Chrome, Glass, Matte, Plastic, Emissive];
}

/// <summary>Snapshot of a mesh node's material fields, for atomic preset apply + undo.</summary>
public readonly record struct MeshMaterialSnapshot(
    Vec3 Color,
    float Metallic,
    float Roughness,
    float Clearcoat,
    float ClearcoatRoughness,
    float Specular,
    Vec3 Emissive,
    uint AlphaMode,
    RenderEffect Effect)
{
    public static MeshMaterialSnapshot Of(SceneNode n)
    {
        return new MeshMaterialSnapshot(
            Color: n.MeshColor,
            Metallic: n.MeshMetallic,
            Roughness: n.MeshRoughness,
            Clearcoat: n.MeshClearcoat,
            ClearcoatRoughness: n.MeshClearcoatRoughness,
            Specular: n.MeshSpecular,
            Emissive: n.MeshEmissive,
            AlphaMode: n.MeshAlphaMode,
            Effect: n.MeshEffect
        );
    }

    public void RestoreTo(SceneNode n)
    {
        n.MeshColor = Color;
        n.MeshMetallic = Metallic;
        n.MeshRoughness = Roughness;
        n.MeshClearcoat = Clearcoat;
        n.MeshClearcoatRoughness = ClearcoatRoughness;
        n.MeshSpecular = Specular;
        n.MeshEmissive = Emissive;
        n.MeshAlphaMode = AlphaMode;
        n.MeshEffect = Effect;
    }
}
