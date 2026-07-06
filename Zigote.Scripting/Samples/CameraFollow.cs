// Sample script — Samples/Scripting/CameraFollow.cs

using Zigote.Scripting;

namespace Samples.Scripting;

/// <summary>Smoothly follows a target node. Set TargetEntityId at runtime.</summary>
public sealed class CameraFollow : Component
{
    [Export] [EditorRange(0, 50)] public float Distance { get; set; } = 5f;

    [Export] [EditorRange(0, 20)] public float Height { get; set; } = 2f;

    [Export]
    [EditorRange(0.01f, 1f)]
    [EditorTooltip("Lower = smoother, higher = snappier")]
    public float Smoothing { get; set; } = 0.1f;

    // Set from code to point at the player's EntityId
    public uint TargetEntityId { get; set; }

    protected override void OnUpdate(float deltaTime)
    {
        // Without a full ECS to look up other entities by ID, we use a placeholder.
        // In a real project, inject the target's position via a shared state object.
        _ = (TargetEntityId, deltaTime);
    }
}