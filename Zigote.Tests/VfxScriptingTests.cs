using Xunit;
using Zigote.Core.Math3D;
using Zigote.Runtime.Scene;
using Zigote.Scripting;
using Zigote.Vfx;
// The Zigote.Vfx namespace and the Zigote.Scripting.Vfx provider class share the name `Vfx`; alias the class.
using ScriptVfx = Zigote.Scripting.Vfx;

namespace Zigote.Tests;

/// <summary>
///     Tests the generic <see cref="Vfx" /> scripting provider + the editor's
///     <see cref="RuntimeVfxBackend" />
///     (script-spawned emitters). Headless — the backend simulates on the CPU, no native/editor
///     window.
/// </summary>
public class VfxScriptingTests
{
    private static VfxEmitterAsset LoopingPoint(float rate, float lifetime = 100f)
    {
        return new VfxEmitterAsset {
            Capacity = 256,
            Looping = true,
            Shape = EmissionShape.Point,
            SpawnRate = rate,
            StartSpeed = FloatRange.Constant(0f),
            StartLifetime = FloatRange.Constant(lifetime),
        };
    }

    [Fact]
    public void Provider_ForwardsToBackend()
    {
        var fake = new FakeVfxBackend();
        ScriptVfx.Backend = fake;
        try
        {
            Assert.True(ScriptVfx.IsAvailable);
            var h = ScriptVfx.Create(
                asset: new VfxEmitterAsset(),
                position: new Vec3(x: 1f, y: 2f, z: 3f)
            );
            Assert.True(h.IsValid);
            Assert.Equal(expected: 1, actual: fake.Created);

            ScriptVfx.SetPosition(handle: h, position: new Vec3(x: 4f, y: 5f, z: 6f));
            Assert.Equal(expected: new Vec3(x: 4f, y: 5f, z: 6f), actual: fake.LastPos);

            ScriptVfx.SetEmitting(handle: h, emitting: false);
            Assert.False(fake.LastEmitting);

            ScriptVfx.Burst(handle: h, count: 25);
            Assert.Equal(expected: 25, actual: fake.Bursts);

            ScriptVfx.Destroy(h);
            Assert.Equal(expected: 1, actual: fake.Destroyed);
        }
        finally
        {
            ScriptVfx.Backend = null;
        }
    }

    [Fact]
    public void Provider_IsNoOp_WithoutBackend()
    {
        ScriptVfx.Backend = null;
        Assert.False(ScriptVfx.IsAvailable);
        Assert.False(ScriptVfx.Create(asset: new VfxEmitterAsset(), position: Vec3.Zero).IsValid);
        // None of these should throw.
        ScriptVfx.SetPosition(handle: VfxHandle.None, position: Vec3.Zero);
        ScriptVfx.SetEmitting(handle: VfxHandle.None, emitting: true);
        ScriptVfx.Burst(handle: VfxHandle.None, count: 10);
        ScriptVfx.Destroy(VfxHandle.None);
    }

    [Fact]
    public void EditorBackend_CreatesAndSimulates()
    {
        var backend = new RuntimeVfxBackend();
        var h = backend.Create(asset: LoopingPoint(40f), position: new Vec3(x: 2f, y: 0f, z: 0f));
        Assert.True(h.IsValid);
        Assert.Single(backend.Emitters);

        for (int i = 0; i < 60; i++) backend.Step(1f / 60f);
        Assert.True(backend.Emitters[h.Id].Pool.Count > 0);
        Assert.Equal(
            expected: new Vec3(x: 2f, y: 0f, z: 0f),
            actual: backend.Emitters[h.Id].Position
        );
    }

    [Fact]
    public void EditorBackend_BurstAndPositionAndDestroy()
    {
        var backend = new RuntimeVfxBackend();
        var h = backend.Create(asset: LoopingPoint(0f), position: Vec3.Zero); // no continuous spawn

        backend.Burst(handle: h, count: 30);
        Assert.Equal(expected: 30, actual: backend.Emitters[h.Id].Pool.Count);

        backend.SetPosition(handle: h, position: new Vec3(x: 5f, y: 1f, z: 0f));
        Assert.Equal(
            expected: new Vec3(x: 5f, y: 1f, z: 0f),
            actual: backend.Emitters[h.Id].Position
        );

        backend.Destroy(h);
        Assert.Empty(backend.Emitters);
    }

    [Fact]
    public void EditorBackend_ReapsFinishedFireAndForget()
    {
        var backend = new RuntimeVfxBackend();
        var asset = LoopingPoint(rate: 60f, lifetime: 0.2f);
        asset.Looping = false;
        asset.Duration = 0.15f;
        backend.Create(asset: asset, position: Vec3.Zero);
        Assert.Single(backend.Emitters);

        for (int i = 0; i < 120; i++) backend.Step(1f / 60f); // 2s — past duration + lifetime
        Assert.Empty(backend.Emitters); // auto-reaped once finished + emptied
    }

    private sealed class FakeVfxBackend : IVfxBackend
    {
        public int Bursts;
        public int Created;
        public int Destroyed;
        public bool? LastEmitting;
        public Vec3 LastPos;

        public VfxHandle Create(VfxEmitterAsset asset, Vec3 position)
        {
            Created++;
            LastPos = position;
            return new VfxHandle((uint)Created);
        }

        public void SetPosition(VfxHandle handle, Vec3 position) => LastPos = position;

        public void SetEmitting(VfxHandle handle, bool emitting) => LastEmitting = emitting;

        public void Burst(VfxHandle handle, int count) => Bursts += count;

        public void Destroy(VfxHandle handle) => Destroyed++;
    }
}
