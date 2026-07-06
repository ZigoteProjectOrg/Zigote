namespace Zigote.Core.Lod;

/// <summary>What the residency policy wants done with a streamable node this frame.</summary>
public enum ResidencyDecision : byte
{
    /// <summary>In the hysteresis band — leave the current residency state untouched.</summary>
    Keep = 0,

    /// <summary>Close enough — ensure the asset is (being) loaded.</summary>
    Want = 1,

    /// <summary>Far enough — release the asset so it can be evicted.</summary>
    Drop = 2,
}

/// <summary>
///     Pure, headless-testable demand-streaming policy: maps a node's camera distance to a
///     load/keep/drop decision. Separate load and evict thresholds give hysteresis — a node between
///     them keeps its current state, so a node hovering at the boundary does not thrash
///     load↔unload every frame. Companion to <see cref="LodMath" /> (which decides visibility);
///     this decides residency.
/// </summary>
public readonly record struct StreamingPolicy(float LoadDistance, float EvictDistance)
{
    /// <summary>Editor default: load everything, never evict (full residency for authoring).</summary>
    public static readonly StreamingPolicy Unbounded =
        new(float.PositiveInfinity, float.PositiveInfinity);

    /// <summary>False when this policy loads everything (no demand streaming) — the editor default.</summary>
    public bool Enabled => !float.IsPositiveInfinity(LoadDistance);

    /// <summary>
    ///     Decide for a node at <paramref name="distance" /> world units from the camera. Within
    ///     <see cref="LoadDistance" /> → <see cref="ResidencyDecision.Want" />; beyond
    ///     <see cref="EvictDistance" /> → <see cref="ResidencyDecision.Drop" />; in between →
    ///     <see cref="ResidencyDecision.Keep" />.
    /// </summary>
    public ResidencyDecision Decide(float distance)
    {
        if (distance <= LoadDistance) return ResidencyDecision.Want;
        if (distance >= EvictDistance) return ResidencyDecision.Drop;
        return ResidencyDecision.Keep;
    }

    /// <summary>
    ///     Build a policy with a hysteresis margin: evict at <paramref name="loadDistance" /> ×
    ///     <paramref name="hysteresis" /> (clamped so evict ≥ load). A margin of ~1.25 is a sane default.
    /// </summary>
    public static StreamingPolicy WithHysteresis(float loadDistance, float hysteresis = 1.25f)
    {
        var evict = loadDistance * MathF.Max(1f, hysteresis);
        return new StreamingPolicy(loadDistance, evict);
    }
}