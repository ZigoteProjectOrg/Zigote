using System.Reflection;

namespace Zigote.Ecs.Reflection;

/// <summary>
///     One field of a POD component, with reflective get/set over a boxed struct.
///     PROTOTYPE: reflection-backed. A source generator can later emit the same shape (Name/FieldType +
///     get/set delegates) with zero reflection — the consumers (inspector, serializer) bind to this
///     surface, not to <see cref="System.Reflection" />, so swapping the backing is invisible to them.
/// </summary>
public sealed class EcsField
{
    private readonly FieldInfo _field;

    internal EcsField(FieldInfo field)
    {
        _field = field;
        Name = field.Name;
        FieldType = field.FieldType;
    }

    public string Name { get; }
    public Type FieldType { get; }

    public object? Get(object boxedComponent)
    {
        return _field.GetValue(boxedComponent);
    }

    public void Set(object boxedComponent, object? value)
    {
        _field.SetValue(boxedComponent, value);
    }
}

/// <summary>Metadata for one registered POD component type: a stable name + its editable fields.</summary>
public sealed class EcsComponentType
{
    internal EcsComponentType(Type type, string name)
    {
        Type = type;
        Name = name;
        Fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => new EcsField(f))
            .ToArray();
    }

    public Type Type { get; }

    /// <summary>Stable identifier used in serialized scenes + the inspector (defaults to the short type name).</summary>
    public string Name { get; }

    public IReadOnlyList<EcsField> Fields { get; }
}

/// <summary>
///     The component-metadata catalogue that flecs cannot provide (it stores components as opaque
///     size+align blobs). The editor registers every authorable component type here; the inspector and
///     scene serializer drive entirely off this registry — no per-type editor code.
/// </summary>
public sealed class EcsComponentRegistry
{
    private readonly Dictionary<string, EcsComponentType> _byName = new();
    private readonly Dictionary<Type, EcsComponentType> _byType = new();

    public IReadOnlyCollection<EcsComponentType> Types => _byType.Values;

    public EcsComponentType Register<T>(string? name = null) where T : unmanaged
    {
        return Register(typeof(T), name);
    }

    public EcsComponentType Register(Type type, string? name = null)
    {
        if (_byType.TryGetValue(type, out var existing)) return existing;
        var info = new EcsComponentType(type, name ?? type.Name);
        _byType[type] = info;
        _byName[info.Name] = info;
        return info;
    }

    public EcsComponentType? ByName(string name)
    {
        return _byName.GetValueOrDefault(name);
    }

    public EcsComponentType? ByType(Type type)
    {
        return _byType.GetValueOrDefault(type);
    }
}
