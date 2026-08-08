namespace Zigote.Scripting;

/// <summary>Frame timing. Updated by ScriptWorld before each OnUpdate batch.</summary>
public static class Time
{
    public static float DeltaTime => _deltaTime;
    public static float Elapsed => _elapsed;

    /// <summary>
    ///     Fraction of a fixed tick left un-simulated after this render frame's fixed-step loop,
    ///     in [0, 1): accumulator / fixed dt. Gameplay ticks at a fixed rate, so a render frame may
    ///     run zero ticks (repeating the last tick's state) or several — render-side code can blend
    ///     between the last two ticks' states by this amount to smooth motion. Set by the host after
    ///     each frame's tick loop.
    /// </summary>
    public static float InterpolationAlpha => _interpolationAlpha;

    /// <summary>
    ///     Reset the play clock to zero. Called at the start of each play session so a script's
    ///     <see cref="Elapsed" /> begins at 0 every play rather than accumulating across replays.
    /// </summary>
    public static void Reset()
    {
        _deltaTime = 0f;
        _elapsed = 0f;
        _interpolationAlpha = 0f;
    }
#pragma warning disable CS0649 // assigned from ScriptWorld in Zigote.Editor
    internal static float _deltaTime;
    internal static float _elapsed;
    internal static float _interpolationAlpha;
#pragma warning restore CS0649
}