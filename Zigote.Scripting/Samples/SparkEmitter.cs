// Sample script — Samples/Scripting/SparkEmitter.cs
// Copy to your project and reference Zigote.Scripting.dll + Zigote.Vfx.dll to get started.

using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Scripting;
using Zigote.Vfx;

namespace Samples.Scripting;

/// <summary>
///     Spawns a sparks/ember emitter built in code and keeps it glued to the attached node — drive the
///     node (or put a <c>Rotator</c> on it) and the sparks trail along. Demonstrates the generic
///     <see cref="Vfx" /> scripting API; the editor's play session backs it with a CPU simulator the
///     viewport draws (2D overlay, or the native GPU billboard pass when <c>render.vfx_native</c> is
///     on).
/// </summary>
public sealed class SparkEmitter : Component
{
    private VfxHandle _emitter;

    [Export]
    [EditorRange(1, 500)]
    [EditorTooltip("Continuous emission rate (particles/second)")]
    public float Rate { get; set; } = 60f;

    [Export] [EditorRange(0.1f, 20f)] public float Speed { get; set; } = 4f;

    [Export]
    [EditorTooltip("Particles emitted in one burst when the component starts")]
    public int StartBurst { get; set; } = 40;

    protected override void OnCreate()
    {
        var asset = new VfxEmitterAsset {
            Capacity = 512,
            SpawnRate = Rate,
            Shape = EmissionShape.Sphere,
            ShapeRadius = 0.1f,
            StartSpeed = new FloatRange(Speed * 0.5f, Speed),
            StartLifetime = new FloatRange(0.5f, 1.0f),
            StartSize = new FloatRange(0.03f, 0.07f),
            Blend = VfxBlendMode.Additive,
        };
        asset.UpdateModules.Add(new GravityModule(new Vec3(0f, -4f, 0f)));
        asset.UpdateModules.Add(new DragModule(0.8f));
        asset.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(0f, Color.White),
                        new ColorStop(0.5f, new Color(1f, 0.6f, 0.15f)),
                        new ColorStop(
                            1f,
                            new Color(
                                0.5f,
                                0.05f,
                                0f,
                                0f
                            )
                        ),
                    ]
                )
            )
        );

        _emitter = Vfx.Create(asset, Position);
        Vfx.Burst(_emitter, StartBurst);
    }

    protected override void OnUpdate(float deltaTime)
    {
        if (_emitter.IsValid) Vfx.SetPosition(_emitter, Position);
    }

    protected override void OnDestroy()
    {
        if (!_emitter.IsValid) return;
        Vfx.Destroy(_emitter);
        _emitter = VfxHandle.None;
    }
}