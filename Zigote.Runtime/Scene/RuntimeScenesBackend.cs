using Zigote.Scripting;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Backs the generic <c>Scenes</c> scripting API in play mode, built entirely on the World spawn
///     machinery: a full <see cref="Load" /> destroys every live entity and grafts the new scene's
///     content as a spawned subtree, an additive load grafts under a container entity. Because both
///     ride <see cref="RuntimeWorldBackend" />'s scene-edit ledger, stopping play in the editor still
///     restores the authored scene — scene switching never mutates what's on disk. The optional black
///     fade is a session-ticked alpha the host viewports draw over the frame.
/// </summary>
internal sealed class RuntimeScenesBackend(RuntimeWorldBackend world, string? initialScenePath)
    : IScenesBackend
{
    private readonly List<EntityHandle> _scratch = [];
    private float _fadeSeconds;
    private (string path, float fade)? _pending;

    private FadePhase _phase = FadePhase.None;

    /// <summary>Black-overlay opacity the host viewport draws over the rendered frame (0 = none).</summary>
    public float FadeAlpha { get; private set; }

    public string? Current { get; private set; } = initialScenePath;

    public void Load(string scenePath, float fadeSeconds)
    {
        if (string.IsNullOrWhiteSpace(scenePath)) return;
        _pending = (scenePath, MathF.Max(x: 0f, y: fadeSeconds));
        if (_pending.Value.fade > 0f && _phase != FadePhase.Out)
        {
            // Also covers a Load arriving mid-fade-IN: flip back to Out so the alpha climbs from
            // wherever it is and the swap still happens at full black, not at a partial fade.
            _phase = FadePhase.Out;
            _fadeSeconds = _pending.Value.fade;
        }
    }

    public EntityHandle LoadAdditive(string scenePath)
    {
        var content = LoadContainer(scenePath);
        return content != null
            ? world.IntegrateExternal(subtreeRoot: content, parentNode: world.Root)
            : EntityHandle.None;
    }

    /// <summary>
    ///     Advance the fade at render rate (host calls once per frame with the render dt). Kept apart
    ///     from <see cref="ApplyPending" /> so a slow frame's many fixed ticks don't rush the fade.
    /// </summary>
    public void TickFade(float dt)
    {
        switch (_phase)
        {
            case FadePhase.Out:
                FadeAlpha = _fadeSeconds > 0f
                    ? MathF.Min(x: 1f, y: FadeAlpha + (dt / _fadeSeconds))
                    : 1f;
                break;
            case FadePhase.In:
                FadeAlpha = _fadeSeconds > 0f
                    ? MathF.Max(x: 0f, y: FadeAlpha - (dt / _fadeSeconds))
                    : 0f;
                if (FadeAlpha <= 0f) _phase = FadePhase.None;
                break;
        }
    }

    /// <summary>
    ///     Perform a requested scene swap. Called by the session at the deferred-apply point of the
    ///     fixed tick (after scripts, before physics) — the same safe point as World destroys. With a
    ///     fade, the swap waits until the fade-out reaches full black.
    /// </summary>
    public void ApplyPending()
    {
        if (_pending is not { } request) return;
        if (_phase == FadePhase.Out && FadeAlpha < 1f) return; // still fading out

        _pending = null;
        Swap(request.path);
        if (_phase == FadePhase.Out) _phase = FadePhase.In;
    }

    private void Swap(string path)
    {
        var content = LoadContainer(path);
        if (content == null) return; // load failed — keep playing the current scene (logged)

        // Destroy every live top-level entity (skips unregistered/editor-internal nodes). Snapshot the
        // handles first: DestroyNow mutates Root.Children. Runs the full destroy path per entity, so
        // physics/audio/VFX/ECS release and the ledger records authored nodes for play-stop restore.
        _scratch.Clear();
        var children = world.Root.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var h = new EntityHandle((uint)children[i].Id);
            if (world.IsAlive(h)) _scratch.Add(h);
        }

        foreach (var h in _scratch) world.DestroyNow(h);

        world.IntegrateExternal(subtreeRoot: content, parentNode: world.Root);
        Current = path;
    }

    /// <summary>Load a scene file and wrap its root's children in a fresh container node.</summary>
    private SceneNode? LoadContainer(string path)
    {
        // SceneGraph.Load returns an EMPTY scene for a missing file (it never throws for that), which
        // would make a typo'd Scenes.Load silently wipe the world — check existence explicitly.
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[Scenes] Scene not found: '{path}'");
            return null;
        }

        SceneGraph graph;
        try
        {
            graph = SceneGraph.Load(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Scenes] Failed to load scene '{path}': {ex.Message}");
            return null;
        }

        var container = new SceneNode(Path.GetFileNameWithoutExtension(path));
        // AddChild reparents (removes from the loaded root), so iterate a snapshot.
        foreach (var child in graph.Root.Children.ToArray()) container.AddChild(child);
        return container;
    }

    private enum FadePhase
    {
        None,
        Out, // alpha 0 → 1, swap when it reaches 1
        In, // alpha 1 → 0 after the swap
    }
}
