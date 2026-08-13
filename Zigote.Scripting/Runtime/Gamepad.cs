using Zigote.Core.Math3D;

namespace Zigote.Scripting;

/// <summary>Controller axes, in SDL order. Sticks read [-1, 1]; triggers read [0, 1].</summary>
public enum GamepadAxis
{
    LeftX = 0,
    LeftY = 1,
    RightX = 2,
    RightY = 3,
    LeftTrigger = 4,
    RightTrigger = 5,
}

/// <summary>Controller buttons, in SDL order (Xbox labels; A = south / cross).</summary>
public enum GamepadButton
{
    A = 0,
    B = 1,
    X = 2,
    Y = 3,
    Back = 4,
    Guide = 5,
    Start = 6,
    LeftStick = 7,
    RightStick = 8,
    LeftShoulder = 9,
    RightShoulder = 10,
    DpadUp = 11,
    DpadDown = 12,
    DpadLeft = 13,
    DpadRight = 14,
}

/// <summary>
///     Generic game-controller input for scripts. The host wires the providers in play mode (editor →
///     SDL gamepad); outside play every read is neutral. Mirrors <see cref="Input" />. Y axes are
///     returned
///     with up positive (SDL reports down positive), so a stick pushed up reads +Y.
/// </summary>
public static class Gamepad
{
    internal static Func<bool>? ConnectedProvider;
    internal static Func<int, float>? AxisProvider;
    internal static Func<int, bool>? ButtonProvider;

    public static bool IsConnected => ConnectedProvider?.Invoke() ?? false;

    /// <summary>Left stick (X right, Y up), raw (apply your own dead-zone).</summary>
    public static Vec2 LeftStick => new(Axis(GamepadAxis.LeftX), -Axis(GamepadAxis.LeftY));

    public static Vec2 RightStick => new(Axis(GamepadAxis.RightX), -Axis(GamepadAxis.RightY));

    public static float Axis(GamepadAxis axis)
    {
        return AxisProvider?.Invoke((int)axis) ?? 0f;
    }

    public static bool Button(GamepadButton button)
    {
        return ButtonProvider?.Invoke((int)button) ?? false;
    }

    /// <summary>Apply a radial dead-zone and rescale the remainder to the full range.</summary>
    public static float DeadZone(float value, float deadZone = 0.12f)
    {
        var a = MathF.Abs(value);
        if (a <= deadZone) return 0f;
        return MathF.Sign(value) * ((a - deadZone) / (1f - deadZone));
    }
}
