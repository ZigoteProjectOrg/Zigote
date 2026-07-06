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
            var h = ScriptVfx.Create(new VfxEmitterAsset(), new Vec3(1f, 2f, 3f));
            Assert.True(h.IsValid);
            Assert.Equal(1, fake.Created);

            ScriptVfx.SetPosition(h, new Vec3(4f, 5f, 6f));
            Assert.Equal(new Vec3(4f, 5f, 6f), fake.LastPos);

            ScriptVfx.SetEmitting(h, false);
            Assert.False(fake.LastEmitting);

            ScriptVfx.Burst(h, 25);
            Assert.Equal(25, fake.Bursts);

            ScriptVfx.Destroy(h);
            Assert.Equal(1, fake.Destroyed);
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
        Assert.False(ScriptVfx.Create(new VfxEmitterAsset(), Vec3.Zero).IsValid);
        // None of these should throw.
        ScriptVfx.SetPosition(VfxHandle.None, Vec3.Zero);
        ScriptVfx.SetEmitting(VfxHandle.None, true);
        ScriptVfx.Burst(VfxHandle.None, 10);
        ScriptVfx.Destroy(VfxHandle.None);
    }

    [Fact]
    public void EditorBackend_CreatesAndSimulates()
    {
        var backend = new RuntimeVfxBackend();
        var h = backend.Create(LoopingPoint(40f), new Vec3(2f, 0f, 0f));
        Assert.True(h.IsValid);
        Assert.Single(backend.Emitters);

        for (var i = 0; i < 60; i++) backend.Step(1f / 60f);
        Assert.True(backend.Emitters[h.Id].Pool.Count > 0);
        Assert.Equal(new Vec3(2f, 0f, 0f), backend.Emitters[h.Id].Position);
    }

    [Fact]
    public void EditorBackend_BurstAndPositionAndDestroy()
    {
        var backend = new RuntimeVfxBackend();
        var h = backend.Create(LoopingPoint(0f), Vec3.Zero); // no continuous spawn

        backend.Burst(h, 30);
        Assert.Equal(30, backend.Emitters[h.Id].Pool.Count);

        backend.SetPosition(h, new Vec3(5f, 1f, 0f));
        Assert.Equal(new Vec3(5f, 1f, 0f), backend.Emitters[h.Id].Position);

        backend.Destroy(h);
        Assert.Empty(backend.Emitters);
    }

    [Fact]
    public void EditorBackend_ReapsFinishedFireAndForget()
    {
        var backend = new RuntimeVfxBackend();
        var asset = LoopingPoint(60f, 0.2f);
        asset.Looping = false;
        asset.Duration = 0.15f;
        backend.Create(asset, Vec3.Zero);
        Assert.Single(backend.Emitters);

        for (var i = 0; i < 120; i++) backend.Step(1f / 60f); // 2s — past duration + lifetime
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

        public void SetPosition(VfxHandle handle, Vec3 position)
        {
            LastPos = position;
        }

        public void SetEmitting(VfxHandle handle, bool emitting)
        {
            LastEmitting = emitting;
        }

        public void Burst(VfxHandle handle, int count)
        {
            Bursts += count;
        }

        public void Destroy(VfxHandle handle)
        {
            Destroyed++;
        }
    }
}