namespace Zigote.Scripting;

/// <summary>
///     GPU instancing access for scripts: a game <see cref="Component" /> submits per-instance
///     model matrices for its own node each frame, and the renderer draws the whole batch as one
///     instanced draw (one shared mesh + material). Engine-generic — it knows nothing about
///     asteroids or any game. The host assigns <see cref="Backend" /> in play mode (and clears it
///     on stop); outside play every call is a safe no-op. Mirrors the <c>Input</c>/<c>Physics</c>
///     static-provider pattern.
/// </summary>
public static class Instancing
{
    public static IInstancingBackend? Backend { get; set; }
    public static bool IsAvailable => Backend != null;

    /// <summary>
    ///     Submit <paramref name="count" /> per-instance model matrices for the node identified by
    ///     <paramref name="entityId" /> (a <see cref="Component.EntityId" />). <paramref name="matrices" />
    ///     holds <paramref name="count" /> × 16 column-major floats (one 4×4 matrix per instance). The
    ///     node draws as <paramref name="count" /> instances of its mesh, ignoring its own transform.
    /// </summary>
    public static void SetInstances(uint entityId, ReadOnlySpan<float> matrices, int count)
    {
        Backend?.SetInstances(entityId, matrices, count);
    }

    /// <summary>
    ///     Submit instances to a node addressed by NAME instead of entity id — useful when a manager
    ///     script drives several other mesh nodes (e.g. one node per LOD level). The host resolves the
    ///     first node with that name. No-op if no such node exists.
    /// </summary>
    public static void SetInstances(string nodeName, ReadOnlySpan<float> matrices, int count)
    {
        Backend?.SetInstances(nodeName, matrices, count);
    }

    /// <summary>
    ///     Stop drawing an instanced node (count 0). It draws nothing — an instanced node is never
    ///     rendered as a single fallback mesh, so an emptied node leaves no stray draw at the origin.
    /// </summary>
    public static void Clear(uint entityId)
    {
        Backend?.SetInstances(entityId, ReadOnlySpan<float>.Empty, 0);
    }

    /// <summary>Stop drawing an instanced node addressed by name (count 0 → draws nothing).</summary>
    public static void Clear(string nodeName)
    {
        Backend?.SetInstances(nodeName, ReadOnlySpan<float>.Empty, 0);
    }
}

/// <summary>
///     The contract the host implements to back <see cref="Instancing" /> with the native renderer.
///     The host maps <paramref name="entityId" /> to the scene node's native handle and uploads the
///     matrices. A headless test can inject a fake backend to assert what a Component submits.
/// </summary>
public interface IInstancingBackend
{
    void SetInstances(uint entityId, ReadOnlySpan<float> matrices, int count);
    void SetInstances(string nodeName, ReadOnlySpan<float> matrices, int count);
}
