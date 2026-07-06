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
        "Car Paint",
        new Vec3(0.72f, 0.05f, 0.06f),
        0.9f,
        0.30f,
        1.0f,
        0.05f,
        1.0f,
        Vec3.Zero,
        0,
        RenderEffect.Standard,
        true
    );

    public static readonly MaterialPreset Chrome = new(
        "Chrome",
        new Vec3(0.90f, 0.90f, 0.92f),
        1.0f,
        0.04f,
        0f,
        0f,
        1.0f,
        Vec3.Zero,
        0,
        RenderEffect.Standard,
        false
    );

    public static readonly MaterialPreset Glass = new(
        "Glass",
        new Vec3(0.85f, 0.92f, 0.95f),
        0f,
        0.04f,
        1.0f,
        0.03f,
        1.0f,
        Vec3.Zero,
        2 /* Blend */,
        RenderEffect.Standard,
        false
    );

    public static readonly MaterialPreset Matte = new(
        "Matte",
        new Vec3(0.5f, 0.5f, 0.5f),
        0f,
        0.9f,
        0f,
        0f,
        0.5f,
        Vec3.Zero,
        0,
        RenderEffect.Standard,
        true
    );

    public static readonly MaterialPreset Plastic = new(
        "Plastic",
        new Vec3(0.5f, 0.5f, 0.5f),
        0f,
        0.45f,
        0.5f,
        0.1f,
        0.7f,
        Vec3.Zero,
        0,
        RenderEffect.Standard,
        true
    );

    public static readonly MaterialPreset Emissive = new(
        "Emissive",
        new Vec3(1f, 1f, 1f),
        0f,
        1.0f,
        0f,
        0f,
        0.5f,
        new Vec3(2f, 2f, 2f),
        0,
        RenderEffect.Standard,
        false
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
            n.MeshColor,
            n.MeshMetallic,
            n.MeshRoughness,
            n.MeshClearcoat,
            n.MeshClearcoatRoughness,
            n.MeshSpecular,
            n.MeshEmissive,
            n.MeshAlphaMode,
            n.MeshEffect
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