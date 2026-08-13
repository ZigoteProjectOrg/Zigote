using System.Reflection;

namespace Zigote.Scripting.Metadata;

/// <summary>
///     Discovers and caches <see cref="ScriptMetadata" /> for every <see cref="Component" />
///     subtype found in a loaded <see cref="Assembly" />. One registry per
///     <see cref="Loading.ScriptDomain" />.
/// </summary>
public sealed class ScriptRegistry
{
    private readonly Dictionary<string, ScriptMetadata> _byFullName = new();

    public IReadOnlyCollection<ScriptMetadata> All => _byFullName.Values;

    /// <summary>Scan the assembly and build metadata for all concrete Component subtypes.</summary>
    public void Load(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(Component).IsAssignableFrom(type)) continue;
            var meta = ScriptMetadata.From(type);
            _byFullName[meta.FullName] = meta;
        }
    }

    /// <summary>
    ///     Register a single component type explicitly — the statically-linked path an exported
    ///     game's generated registration uses instead of an assembly scan (NativeAOT-safe).
    /// </summary>
    public void Register(Type type)
    {
        var meta = ScriptMetadata.From(type);
        _byFullName[meta.FullName] = meta;
    }

    public ScriptMetadata? Find(string fullName) =>
        _byFullName.TryGetValue(key: fullName, value: out var m) ? m : null;

    /// <summary>Creates a new instance of the named component. Returns null on failure.</summary>
    public Component? CreateInstance(string fullName)
    {
        if (_byFullName.TryGetValue(key: fullName, value: out var meta))
            return TryCreate(meta.Type);
        return null;
    }

    private static Component? TryCreate(Type type)
    {
        try
        {
            return (Component?)Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ScriptRegistry] Failed to create {type.Name}: {ex.Message}");
            return null;
        }
    }
}
