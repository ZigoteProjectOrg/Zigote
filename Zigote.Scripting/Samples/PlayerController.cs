// Sample script — Samples/Scripting/PlayerController.cs

using Zigote.Core.Math3D;
using Zigote.Scripting;

namespace Samples.Scripting;

/// <summary>Moves the attached node based on input axes.</summary>
public sealed class PlayerController : Component
{
    [Export] [EditorRange(0, 1000)] public float Speed { get; set; } = 320f;

    [Export] public bool AllowVerticalMovement { get; set; }

    protected override void OnUpdate(float deltaTime)
    {
        var axes = Input.Axis2D("Move"); // horizontal = X, vertical = Z
        if (axes.LengthSq() < 0.001f) return;

        var dir = new Vec3(axes.X, 0f, -axes.Y);
        if (!AllowVerticalMovement) dir = new Vec3(dir.X, 0f, dir.Z);

        Position = Position + dir.Normalize() * (Speed * deltaTime);
    }
}
