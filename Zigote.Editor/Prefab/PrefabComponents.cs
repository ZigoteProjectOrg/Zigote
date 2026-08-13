using Zigote.Core.Math3D;
using Zigote.Ecs.Scene;
using Zigote.Game.Resources;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.Prefab;

/// <summary>
///     The mesh material fields a prefab can inherit/override, as a blittable POD component so flecs
///     (and therefore <c>EcsPrefab</c>) can store and share it. Bools/enums are widened to
///     <c>uint</c>/<c>int</c> because the marshalled component layout must be blittable (see
///     <see cref="Zigote.Ecs.EcsWorld" /> reflective-access note).
/// </summary>
public struct NodeMaterial
{
    public Vec3 Color;
    public float Metallic;
    public float Roughness;
    public float Clearcoat;
    public float ClearcoatRoughness;
    public float Specular;
    public Vec3 Emissive;
    public uint AlphaMode;
    public uint Effect;
}

/// <summary>
///     The light fields a prefab can inherit/override (blittable POD; see
///     <see cref="NodeMaterial" />).
/// </summary>
public struct NodeLight
{
    public int Kind;
    public Vec3 Color;
    public float Intensity;
    public float Range;
    public float Temperature;
    public float SpotInner;
    public float SpotOuter;
    public uint CastShadows;
}

/// <summary>
///     Maps a <see cref="SceneNode" />'s authorable numeric state to/from the blittable POD components
///     that back the prefab system (<see cref="Transform" />, <see cref="NodeMaterial" />,
///     <see cref="NodeLight" />). The string/structural identity (name, kind, mesh path, hierarchy) is
///     NOT here — it lives in the <c>.prefab</c> template and defines the prefab, so it is not a
///     per-property override. Pure, headless-testable (no native, no ECS).
/// </summary>
public static class SceneNodeComponents
{
    public static Transform ReadTransform(SceneNode n)
    {
        return new Transform {
            Position = n.Position,
            Rotation = n.Rotation,
            Scale = n.Scale,
        };
    }

    public static void WriteTransform(SceneNode n, in Transform t)
    {
        n.Position = t.Position;
        n.Rotation = t.Rotation;
        n.Scale = t.Scale;
    }

    public static NodeMaterial ReadMaterial(SceneNode n)
    {
        return new NodeMaterial {
            Color = n.MeshColor,
            Metallic = n.MeshMetallic,
            Roughness = n.MeshRoughness,
            Clearcoat = n.MeshClearcoat,
            ClearcoatRoughness = n.MeshClearcoatRoughness,
            Specular = n.MeshSpecular,
            Emissive = n.MeshEmissive,
            AlphaMode = n.MeshAlphaMode,
            Effect = (uint)n.MeshEffect,
        };
    }

    public static void WriteMaterial(SceneNode n, in NodeMaterial m)
    {
        n.MeshColor = m.Color;
        n.MeshMetallic = m.Metallic;
        n.MeshRoughness = m.Roughness;
        n.MeshClearcoat = m.Clearcoat;
        n.MeshClearcoatRoughness = m.ClearcoatRoughness;
        n.MeshSpecular = m.Specular;
        n.MeshEmissive = m.Emissive;
        n.MeshAlphaMode = m.AlphaMode;
        n.MeshEffect = (RenderEffect)m.Effect;
    }

    public static NodeLight ReadLight(SceneNode n)
    {
        return new NodeLight {
            Kind = (int)n.LightKind,
            Color = n.LightColor,
            Intensity = n.LightIntensity,
            Range = n.LightRange,
            Temperature = n.LightTemperature,
            SpotInner = n.SpotInnerAngleDeg,
            SpotOuter = n.SpotOuterAngleDeg,
            CastShadows = n.LightCastShadows ? 1u : 0u,
        };
    }

    public static void WriteLight(SceneNode n, in NodeLight l)
    {
        n.LightKind = (LightType)(byte)l.Kind;
        n.LightColor = l.Color;
        n.LightIntensity = l.Intensity;
        n.LightRange = l.Range;
        n.LightTemperature = l.Temperature;
        n.SpotInnerAngleDeg = l.SpotInner;
        n.SpotOuterAngleDeg = l.SpotOuter;
        n.LightCastShadows = l.CastShadows != 0;
    }
}
