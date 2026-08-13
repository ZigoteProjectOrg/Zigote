using System.Reflection;
using System.Runtime.Loader;

namespace Zigote.Scripting.Loading;

/// <summary>
///     Isolates a user script assembly in a collectible <see cref="AssemblyLoadContext" />
///     so it can be unloaded and reloaded without restarting the editor.
/// </summary>
public sealed class ScriptDomain : IDisposable
{
    private CollectibleContext? _context;
    private bool _disposed;

    public Assembly? Assembly { get; private set; }

    public bool IsLoaded => Assembly != null;
    public string? AssemblyPath { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
    }

    /// <summary>
    ///     Load a compiled script assembly. Throws on failure — callers should catch and
    ///     display diagnostics rather than letting exceptions propagate.
    /// </summary>
    public void Load(string assemblyPath)
    {
        Unload();
        _context = new CollectibleContext(assemblyPath);
        Assembly = _context.LoadFromAssemblyPath(assemblyPath);
        AssemblyPath = assemblyPath;
    }

    /// <summary>Discover all non-abstract <see cref="Component" /> subtypes in the loaded assembly.</summary>
    public IReadOnlyList<Type> FindComponentTypes()
    {
        if (Assembly is null) return [];
        var result = new List<Type>();
        foreach (var t in Assembly.GetTypes())
        {
            if (t is { IsAbstract: false, IsInterface: false } &&
                typeof(Component).IsAssignableFrom(t))
                result.Add(t);
        }

        return result;
    }

    /// <summary>Unload the current assembly. The unload completes when GC collects the context.</summary>
    public void Unload()
    {
        Assembly = null;
        AssemblyPath = null;
        _context?.Unload();
        _context = null;
    }

    // ── Collectible context ───────────────────────────────────────────────────

    private sealed class CollectibleContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public CollectibleContext(string path)
            : base(
                name: $"ScriptDomain:{Path.GetFileNameWithoutExtension(path)}",
                isCollectible: true
            ) =>
            _resolver = new AssemblyDependencyResolver(path);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Unify host-shared assemblies (Zigote.Core, Zigote.Scripting, the BCL, …) with the
            // default context so types like `Component` are the SAME Type across the boundary — a
            // separate copy here would make ScriptRegistry's `IsSubclassOf(Component)` fail.
            foreach (var loaded in Default.Assemblies)
            {
                if (loaded.GetName().Name == assemblyName.Name)
                    return null; // fall back to the default context
            }

            // Resolve genuinely-new dependencies the host does NOT provide (e.g. Zigote.ECS, or any
            // package the script project pulls in) from the script's own output via its deps.json.
            string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            return resolved != null ? LoadFromAssemblyPath(resolved) : null;
        }
    }
}
