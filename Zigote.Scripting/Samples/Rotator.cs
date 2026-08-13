// Sample script — Samples/Scripting/Rotator.cs
// Copy to your project and reference Zigote.Scripting.dll to get started.

using Zigote.Core.Math3D;
using Zigote.Scripting;

namespace Samples.Scripting;

/// <summary>Continuously rotates the attached node around the Y-axis.</summary>
public sealed class Rotator : Component
{
    [Export]
    [EditorRange(0, 720)]
    [EditorTooltip("Rotation speed in degrees per second")]
    public float Speed { get; set; } = 90f;

    [Export] public bool Clockwise { get; set; } = true;

    protected override void OnUpdate(float deltaTime)
    {
        var sign = Clockwise ? 1f : -1f;
        var euler = Rotation.ToEulerRadians();
        Rotation = Quat.FromEuler(
            euler.X,
            euler.Y + sign * Speed * deltaTime * (MathF.PI / 180f),
            euler.Z
        );
    }
}
