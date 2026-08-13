using Zigote.Ecs.Scene;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.Prefab;

/// <summary>
///     The component groups a prefab instance can override, at the same granularity flecs
///     <c>EcsPrefab</c> tracks ownership (whole component, not individual field).
/// </summary>
public enum PrefabComponent
{
    Transform,
    Material,
    Light,
}

/// <summary>
///     Edit-mode override detection + revert for a prefab instance, by diffing the instance's
///     authorable
///     POD components against its <c>.prefab</c> template. This is the same per-component override
///     model
///     as <see cref="ScenePrefabLibrary" />/<c>EcsPrefab</c> (own = differs from inherited), computed
///     purely from the two <see cref="SceneNode" />s — no live ECS entity needed to drive the
///     inspector.
///     Reverting copies the template's component values back onto the instance. Pure +
///     headless-testable.
/// </summary>
public static class PrefabOverrides
{
    public static bool IsOverridden(PrefabComponent component, SceneNode instance,
        SceneNode template)
    {
        return component switch {
            PrefabComponent.Transform => !Eq(
                SceneNodeComponents.ReadTransform(instance),
                SceneNodeComponents.ReadTransform(template)
            ),
            PrefabComponent.Material => instance.Kind == NodeKind.Mesh && !Eq(
                SceneNodeComponents.ReadMaterial(instance),
                SceneNodeComponents.ReadMaterial(template)
            ),
            PrefabComponent.Light => instance.Kind == NodeKind.Light && !Eq(
                SceneNodeComponents.ReadLight(instance),
                SceneNodeComponents.ReadLight(template)
            ),
            _ => false,
        };
    }

    /// <summary>
    ///     Which component groups apply to this node's kind (Transform always; Material/Light by
    ///     kind).
    /// </summary>
    public static IEnumerable<PrefabComponent> ApplicableTo(SceneNode node)
    {
        yield return PrefabComponent.Transform;
        if (node.Kind == NodeKind.Mesh) yield return PrefabComponent.Material;
        if (node.Kind == NodeKind.Light) yield return PrefabComponent.Light;
    }

    public static bool AnyOverridden(SceneNode instance, SceneNode template)
    {
        foreach (var c in ApplicableTo(instance))
            if (IsOverridden(c, instance, template))
                return true;
        return false;
    }

    /// <summary>Copy the template's values for <paramref name="component" /> onto the instance (a revert).</summary>
    public static void Revert(PrefabComponent component, SceneNode instance, SceneNode template)
    {
        switch (component)
        {
            case PrefabComponent.Transform:
                SceneNodeComponents.WriteTransform(
                    instance,
                    SceneNodeComponents.ReadTransform(template)
                );
                break;
            case PrefabComponent.Material:
                SceneNodeComponents.WriteMaterial(
                    instance,
                    SceneNodeComponents.ReadMaterial(template)
                );
                break;
            case PrefabComponent.Light:
                SceneNodeComponents.WriteLight(instance, SceneNodeComponents.ReadLight(template));
                break;
        }
    }

    /// <summary>Snapshot the instance's current values for <paramref name="component" /> (for undo).</summary>
    public static object Capture(PrefabComponent component, SceneNode node)
    {
        return component switch {
            PrefabComponent.Transform => SceneNodeComponents.ReadTransform(node),
            PrefabComponent.Material => SceneNodeComponents.ReadMaterial(node),
            PrefabComponent.Light => SceneNodeComponents.ReadLight(node),
            _ => new object(),
        };
    }

    /// <summary>Restore a snapshot produced by <see cref="Capture" /> onto the instance.</summary>
    public static void Restore(PrefabComponent component, SceneNode node, object snapshot)
    {
        switch (component)
        {
            case PrefabComponent.Transform when snapshot is Transform t:
                SceneNodeComponents.WriteTransform(node, t);
                break;
            case PrefabComponent.Material when snapshot is NodeMaterial m:
                SceneNodeComponents.WriteMaterial(node, m);
                break;
            case PrefabComponent.Light when snapshot is NodeLight l:
                SceneNodeComponents.WriteLight(node, l);
                break;
        }
    }

    private static bool Eq<T>(T a, T b) where T : struct
    {
        return EqualityComparer<T>.Default.Equals(a, b);
    }
}
