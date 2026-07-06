using Zigote.Core.Math3D;

namespace Zigote.Scripting;

/// <summary>
///     Input query helpers for scripts. Providers are set by ScriptWorld each frame
///     before calling OnUpdate, so scripts can use these statics directly.
/// </summary>
public static class Input
{
    /// <summary>This frame's mouse-look delta in logical pixels (right-drag in play mode). Zero otherwise.</summary>
    public static Vec2 LookDelta => LookDeltaProvider?.Invoke() ?? Vec2.Zero;

    /// <summary>Returns the normalized 2D axis for the named input action, or Vec2.Zero.</summary>
    public static Vec2 Axis2D(string name)
    {
        return Axis2DProvider?.Invoke(name) ?? Vec2.Zero;
    }

    /// <summary>Returns true while the named key is held down.</summary>
    public static bool IsKeyDown(string name)
    {
        return KeyDownProvider?.Invoke(name) ?? false;
    }
#pragma warning disable CS0649 // assigned from ScriptWorld in Zigote.Editor
    internal static Func<string, Vec2>? Axis2DProvider;
    internal static Func<string, bool>? KeyDownProvider;
    internal static Func<Vec2>? LookDeltaProvider;
#pragma warning restore CS0649
}