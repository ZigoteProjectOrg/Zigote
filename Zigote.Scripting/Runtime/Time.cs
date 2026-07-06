namespace Zigote.Scripting;

/// <summary>Frame timing. Updated by ScriptWorld before each OnUpdate batch.</summary>
public static class Time
{
    public static float DeltaTime => _deltaTime;
    public static float Elapsed => _elapsed;

    /// <summary>
    ///     Reset the play clock to zero. Called at the start of each play session so a script's
    ///     <see cref="Elapsed" /> begins at 0 every play rather than accumulating across replays.
    /// </summary>
    public static void Reset()
    {
        _deltaTime = 0f;
        _elapsed = 0f;
    }
#pragma warning disable CS0649 // assigned from ScriptWorld in Zigote.Editor
    internal static float _deltaTime;
    internal static float _elapsed;
#pragma warning restore CS0649
}