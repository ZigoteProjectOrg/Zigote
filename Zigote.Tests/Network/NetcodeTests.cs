using Xunit;
using Zigote.Network;

namespace Zigote.Tests;

public class NetcodeTests
{
    private static float Step(float state, MoveInput input, float dt)
    {
        return state + input.Move * dt;
    }

    // ── Prediction ───────────────────────────────────────────────────────────
    [Fact]
    public void ClientPrediction_Predicts_Locally_And_Buffers()
    {
        var cp = new ClientPrediction<MoveInput, float>(0f, Step);
        cp.Predict(new MoveInput { Move = 1f }, 1f);
        cp.Predict(new MoveInput { Move = 1f }, 1f);

        Assert.Equal(2f, cp.State, 4);
        Assert.Equal(2, cp.PendingInputs);
        Assert.Equal(2u, cp.LastInputSequence);
    }

    [Fact]
    public void ClientPrediction_Reconciles_And_Replays_Unacked()
    {
        var cp = new ClientPrediction<MoveInput, float>(0f, Step);
        cp.Predict(new MoveInput { Move = 1f }, 1f); // seq 1 → 1
        cp.Predict(new MoveInput { Move = 1f }, 1f); // seq 2 → 2

        // Server processed up to seq 1, authoritative state there is 1.0. Replay seq 2 → 2.0.
        cp.Reconcile(1f, 1, 1f);
        Assert.Equal(2f, cp.State, 4);
        Assert.Equal(1, cp.PendingInputs);
    }

    [Fact]
    public void ClientPrediction_Corrects_Misprediction()
    {
        var cp = new ClientPrediction<MoveInput, float>(0f, Step);
        cp.Predict(new MoveInput { Move = 10f }, 1f); // client predicts 10

        // Server says after seq 1 the authoritative state is only 5 (e.g. hit a wall). No unacked inputs.
        cp.Reconcile(5f, 1, 1f);
        Assert.Equal(5f, cp.State, 4);
        Assert.Equal(0, cp.PendingInputs);
    }

    [Fact]
    public void ServerReconciler_Applies_In_Order_Ignoring_Dupes_And_Stale()
    {
        var sr = new ServerReconciler<MoveInput, float>(0f, Step);
        sr.Enqueue(
            new MoveInput {
                Sequence = 2,
                Move = 3f,
            }
        );
        sr.Enqueue(
            new MoveInput {
                Sequence = 1,
                Move = 2f,
            }
        ); // arrived out of order
        sr.Enqueue(
            new MoveInput {
                Sequence = 1,
                Move = 2f,
            }
        ); // duplicate
        sr.Step(1f);

        Assert.Equal(5f, sr.State, 4); // 2 then 3, in sequence order
        Assert.Equal(2u, sr.LastProcessedSequence);

        sr.Enqueue(
            new MoveInput {
                Sequence = 1,
                Move = 99f,
            }
        ); // stale
        sr.Step(1f);
        Assert.Equal(5f, sr.State, 4);
    }

    [Fact]
    public void Client_And_Server_Converge()
    {
        var client = new ClientPrediction<MoveInput, float>(0f, Step);
        var server = new ServerReconciler<MoveInput, float>(0f, Step);

        for (var i = 0; i < 5; i++)
        {
            var input = client.Predict(new MoveInput { Move = 2f }, 0.5f);
            server.Enqueue(input);
        }

        server.Step(0.5f);
        client.Reconcile(server.State, server.LastProcessedSequence, 0.5f);

        Assert.Equal(server.State, client.State, 4);
        Assert.Equal(0, client.PendingInputs);
    }

    // ── Interpolation ──────────────────────────────────────────────────────────
    [Fact]
    public void Interpolator_Lerps_Between_Samples()
    {
        var interp = new SnapshotInterpolator<float>(static (a, b, t) => a + (b - a) * t);
        interp.Push(0.0, 0f);
        interp.Push(1.0, 10f);

        Assert.True(interp.TrySample(0.5, out var mid));
        Assert.Equal(5f, mid, 4);
        Assert.True(interp.TrySample(0.0, out var start));
        Assert.Equal(0f, start, 4);
    }

    [Fact]
    public void Interpolator_Clamps_Before_First_And_Extrapolates_After_Last()
    {
        var interp = new SnapshotInterpolator<float>(static (a, b, t) => a + (b - a) * t);
        interp.Push(1.0, 0f);
        interp.Push(2.0, 10f);

        Assert.True(interp.TrySample(0.0, out var before));
        Assert.Equal(0f, before, 4); // clamped to first

        Assert.True(interp.TrySample(2.5, out var after));
        Assert.Equal(15f, after, 4); // extrapolated half a step
    }

    [Fact]
    public void Interpolator_Empty_Returns_False()
    {
        var interp = new SnapshotInterpolator<float>(static (a, b, t) => a + (b - a) * t);
        Assert.False(interp.TrySample(1.0, out _));
    }

    // ── Fixed tick ─────────────────────────────────────────────────────────────
    [Fact]
    public void NetTick_Accumulates_Fixed_Steps()
    {
        var tick = new NetTick(0.1f);
        Assert.Equal(0, tick.Advance(0.05f));
        Assert.Equal(1, tick.Advance(0.06f)); // accum 0.11 → 1 step
        Assert.Equal(2, tick.Advance(0.2f)); // accum ~0.21 → 2 steps
        Assert.Equal(3u, tick.Tick);
    }

    [Fact]
    public void NetTick_Guards_Against_Spiral()
    {
        var tick = new NetTick(0.1f);
        Assert.True(tick.Advance(100f) <= 8);
    }

    private struct MoveInput : IInputCommand
    {
        public uint Sequence { get; set; }
        public float Move;

        public void Serialize(NetWriter w)
        {
            w.WriteVarUInt(Sequence);
            w.WriteSingle(Move);
        }

        public void Deserialize(NetReader r)
        {
            Sequence = r.ReadVarUInt();
            Move = r.ReadSingle();
        }
    }
}