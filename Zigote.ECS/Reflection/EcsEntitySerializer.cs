using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zigote.Ecs.Reflection;

/// <summary>One component on an entity, boxed for generic field display/editing in an inspector.</summary>
public sealed class EcsComponentView
{
    public required EcsComponentType Type { get; init; }
    public required object Boxed { get; init; }
}

/// <summary>
///     Drives the generic inspector + scene serialization for flecs entities off an
///     <see cref="EcsComponentRegistry" /> — the layer that replaces flecs's missing reflection/meta.
///     Pure data: no UI, no native beyond <see cref="EcsWorld" />'s boxed accessors, so it's
///     headless-testable.
/// </summary>
public static class EcsEntitySerializer
{
    /// <summary>Every registered component the entity currently has, boxed for field read/write.</summary>
    public static List<EcsComponentView> Inspect(EcsWorld world, EcsComponentRegistry registry,
        Entity entity)
    {
        var views = new List<EcsComponentView>();
        foreach (var ct in registry.Types)
        {
            if (world.Has(e: entity, t: ct.Type) &&
                world.GetBoxed(e: entity, t: ct.Type) is { } boxed)
            {
                views.Add(
                    new EcsComponentView {
                        Type = ct,
                        Boxed = boxed,
                    }
                );
            }
        }

        return views;
    }

    /// <summary>
    ///     Write one field back to flecs (read-modify-write the boxed struct). An editor wraps this in an
    ///     undo command; the change fires OnSet observers, so a live inspector / other watchers update.
    /// </summary>
    public static void SetField(EcsWorld world, Entity entity, EcsComponentType type,
        EcsField field, object? value)
    {
        if (world.GetBoxed(e: entity, t: type.Type) is not { } boxed) return;
        field.Set(boxedComponent: boxed, value: value);
        world.SetBoxed(e: entity, t: type.Type, value: boxed);
    }

    /// <summary>Serialize all of an entity's registered components to a portable JSON object.</summary>
    public static JsonObject Serialize(EcsWorld world, EcsComponentRegistry registry, Entity entity)
    {
        var components = new JsonArray();
        foreach (var ct in registry.Types)
        {
            if (world.Has(e: entity, t: ct.Type) &&
                world.GetBoxed(e: entity, t: ct.Type) is { } boxed)
                components.Add(ComponentNode(ct: ct, boxed: boxed));
        }

        return new JsonObject { ["components"] = components };
    }

    /// <summary>
    ///     One component → <c>{ "type": name, "fields": {…} }</c>. Shared by entity + prefab-instance
    ///     serialization.
    /// </summary>
    public static JsonObject ComponentNode(EcsComponentType ct, object boxed)
    {
        var fields = new JsonObject();
        foreach (var f in ct.Fields)
        {
            fields[f.Name] = JsonSerializer.SerializeToNode(
                value: f.Get(boxed),
                inputType: f.FieldType
            );
        }

        return new JsonObject {
            ["type"] = ct.Name,
            ["fields"] = fields,
        };
    }

    /// <summary>
    ///     Apply a <c>{ "fields": {…} }</c> object's values onto a boxed component (missing fields
    ///     untouched).
    /// </summary>
    public static void ApplyFields(EcsComponentType ct, JsonObject component, object boxed)
    {
        if (component["fields"] is not JsonObject fields) return;
        foreach (var f in ct.Fields)
        {
            if (fields[f.Name] is { } fv)
                f.Set(boxedComponent: boxed, value: fv.Deserialize(f.FieldType));
        }
    }

    /// <summary>Create a fresh entity and apply the serialized component data.</summary>
    public static Entity Deserialize(EcsWorld world, EcsComponentRegistry registry, JsonObject data)
    {
        var e = world.CreateEntity();
        Apply(
            world: world,
            registry: registry,
            entity: e,
            data: data
        );
        return e;
    }

    /// <summary>Apply serialized component data onto an existing entity (set/overwrite each component).</summary>
    public static void Apply(EcsWorld world, EcsComponentRegistry registry, Entity entity,
        JsonObject data)
    {
        if (data["components"] is not JsonArray components) return;

        foreach (var node in components)
        {
            if (node is not JsonObject co || (string?)co["type"] is not { } typeName) continue;
            if (registry.ByName(typeName) is not { } ct) continue;

            object boxed = world.GetBoxed(e: entity, t: ct.Type) ??
                           Activator.CreateInstance(ct.Type)!;
            ApplyFields(ct: ct, component: co, boxed: boxed);
            world.SetBoxed(e: entity, t: ct.Type, value: boxed);
        }
    }
}
