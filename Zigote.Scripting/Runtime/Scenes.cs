namespace Zigote.Scripting;

/// <summary>
///     The contract the host (play session) implements to back the generic <see cref="Scenes" />
///     scripting API. A strongly-typed interface (rather than multiplexed delegates) so it stays
///     debuggable and headless tests can inject a fake backend.
/// </summary>
public interface IScenesBackend
{
    /// <summary>The scene path the session is currently playing (as the host spelled it), if known.</summary>
    string? Current { get; }

    /// <summary>
    ///     Replace the running scene: every live entity is destroyed and the new scene's content is
    ///     loaded in its place. Deferred to the end of the current fixed tick (like World.Destroy);
    ///     with a fade the swap happens at full black. In the editor this rides the play session's
    ///     scene-edit ledger, so stopping play still restores the authored scene.
    /// </summary>
    void Load(string scenePath, float fadeSeconds);

    /// <summary>
    ///     Load a scene's content INTO the running scene (rooms, streamed sections). The loaded
    ///     content is grafted under one container entity whose handle is returned — destroy it to
    ///     unload. Applied immediately, like World.Spawn.
    /// </summary>
    EntityHandle LoadAdditive(string scenePath);
}

/// <summary>
///     Generic runtime scene management for scripts: switch levels, load rooms additively, and drive a
///     fade transition. Engine-generic — it knows nothing about any game. The host assigns
///     <see cref="Backend" /> in play mode (and clears it on stop); outside play every call is a safe
///     no-op. Mirrors <see cref="World" />/<see cref="Physics" />.
/// </summary>
public static class Scenes
{
    /// <summary>Set by the host (or a test) to route calls to the live session.</summary>
    public static IScenesBackend? Backend { get; set; }

    public static bool IsAvailable => Backend != null;

    public static string? Current => Backend?.Current;

    /// <summary>Switch to another scene (deferred to the end of the current fixed tick).</summary>
    public static void Load(string scenePath) =>
        Backend?.Load(scenePath: scenePath, fadeSeconds: 0f);

    /// <summary>
    ///     Switch to another scene through a black fade: fade out over <paramref name="fadeSeconds" />,
    ///     swap at full black, fade back in over the same duration.
    /// </summary>
    public static void Load(string scenePath, float fadeSeconds) => Backend?.Load(
        scenePath: scenePath,
        fadeSeconds: fadeSeconds
    );

    /// <summary>Load a scene's content into the running scene; destroy the returned entity to unload it.</summary>
    public static EntityHandle LoadAdditive(string scenePath) =>
        Backend?.LoadAdditive(scenePath) ?? EntityHandle.None;
}
