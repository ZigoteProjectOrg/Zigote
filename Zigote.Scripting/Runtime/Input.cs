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

    /// <summary>
    ///     Capture the pointer for mouselook. While captured the cursor is hidden and pinned, and
    ///     <see cref="LookDelta" /> reports raw motion that never runs out at a window edge — which is
    ///     what a first-person camera needs and what a free cursor cannot provide.
    ///     <para>
    ///         Release it whenever the player needs the cursor: a menu, an inventory, a pause screen.
    ///         The host drops capture automatically when the window loses focus, so a game that forgets
    ///         cannot trap the pointer.
    ///     </para>
    /// </summary>
    public static bool MouseCaptured
    {
        get => CaptureGetProvider?.Invoke() ?? false;
        set => CaptureSetProvider?.Invoke(value);
    }

    /// <summary>True when the host supports capturing the pointer at all.</summary>
    public static bool CanCaptureMouse => CaptureSetProvider != null;

    /// <summary>Returns the normalized 2D axis for the named input action, or Vec2.Zero.</summary>
    public static Vec2 Axis2D(string name) => Axis2DProvider?.Invoke(name) ?? Vec2.Zero;

    /// <summary>Returns true while the named key is held down.</summary>
    public static bool IsKeyDown(string name) => KeyDownProvider?.Invoke(name) ?? false;
#pragma warning disable CS0649 // assigned from ScriptWorld in Zigote.Editor
    internal static Func<string, Vec2>? Axis2DProvider;
    internal static Func<string, bool>? KeyDownProvider;
    internal static Func<Vec2>? LookDeltaProvider;
    internal static Func<bool>? CaptureGetProvider;
    internal static Action<bool>? CaptureSetProvider;
#pragma warning restore CS0649
}
