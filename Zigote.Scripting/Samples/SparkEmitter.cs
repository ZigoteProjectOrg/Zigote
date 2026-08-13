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
    [EditorRange(min: 1, max: 500)]
    [EditorTooltip("Continuous emission rate (particles/second)")]
    public float Rate { get; set; } = 60f;

    [Export]
    [EditorRange(min: 0.1f, max: 20f)]
    public float Speed { get; set; } = 4f;

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
            StartSpeed = new FloatRange(min: Speed * 0.5f, max: Speed),
            StartLifetime = new FloatRange(min: 0.5f, max: 1.0f),
            StartSize = new FloatRange(min: 0.03f, max: 0.07f),
            Blend = VfxBlendMode.Additive,
        };
        asset.UpdateModules.Add(new GravityModule(new Vec3(x: 0f, y: -4f, z: 0f)));
        asset.UpdateModules.Add(new DragModule(0.8f));
        asset.UpdateModules.Add(
            new ColorOverLifeModule(
                new ColorRamp(
                    [
                        new ColorStop(position: 0f, color: Color.White),
                        new ColorStop(position: 0.5f, color: new Color(r: 1f, g: 0.6f, b: 0.15f)),
                        new ColorStop(
                            position: 1f,
                            color: new Color(
                                r: 0.5f,
                                g: 0.05f,
                                b: 0f,
                                a: 0f
                            )
                        ),
                    ]
                )
            )
        );

        _emitter = Vfx.Create(asset: asset, position: Position);
        Vfx.Burst(handle: _emitter, count: StartBurst);
    }

    protected override void OnUpdate(float deltaTime)
    {
        if (_emitter.IsValid) Vfx.SetPosition(handle: _emitter, position: Position);
    }

    protected override void OnDestroy()
    {
        if (!_emitter.IsValid) return;
        Vfx.Destroy(_emitter);
        _emitter = VfxHandle.None;
    }
}
