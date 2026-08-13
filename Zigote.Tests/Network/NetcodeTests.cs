using Xunit;
using Zigote.Network;

namespace Zigote.Tests;

public class NetcodeTests
{
    private static float Step(float state, MoveInput input, float dt) => state + (input.Move * dt);

    // ── Prediction ───────────────────────────────────────────────────────────
    [Fact]
    public void ClientPrediction_Predicts_Locally_And_Buffers()
    {
        var cp = new ClientPrediction<MoveInput, float>(initial: 0f, step: Step);
        cp.Predict(input: new MoveInput { Move = 1f }, dt: 1f);
        cp.Predict(input: new MoveInput { Move = 1f }, dt: 1f);

        Assert.Equal(expected: 2f, actual: cp.State, precision: 4);
        Assert.Equal(expected: 2, actual: cp.PendingInputs);
        Assert.Equal(expected: 2u, actual: cp.LastInputSequence);
    }

    [Fact]
    public void ClientPrediction_Reconciles_And_Replays_Unacked()
    {
        var cp = new ClientPrediction<MoveInput, float>(initial: 0f, step: Step);
        cp.Predict(input: new MoveInput { Move = 1f }, dt: 1f); // seq 1 → 1
        cp.Predict(input: new MoveInput { Move = 1f }, dt: 1f); // seq 2 → 2

        // Server processed up to seq 1, authoritative state there is 1.0. Replay seq 2 → 2.0.
        cp.Reconcile(authoritative: 1f, lastProcessedSequence: 1, dt: 1f);
        Assert.Equal(expected: 2f, actual: cp.State, precision: 4);
        Assert.Equal(expected: 1, actual: cp.PendingInputs);
    }

    [Fact]
    public void ClientPrediction_Corrects_Misprediction()
    {
        var cp = new ClientPrediction<MoveInput, float>(initial: 0f, step: Step);
        cp.Predict(input: new MoveInput { Move = 10f }, dt: 1f); // client predicts 10

        // Server says after seq 1 the authoritative state is only 5 (e.g. hit a wall). No unacked inputs.
        cp.Reconcile(authoritative: 5f, lastProcessedSequence: 1, dt: 1f);
        Assert.Equal(expected: 5f, actual: cp.State, precision: 4);
        Assert.Equal(expected: 0, actual: cp.PendingInputs);
    }

    [Fact]
    public void ServerReconciler_Applies_In_Order_Ignoring_Dupes_And_Stale()
    {
        var sr = new ServerReconciler<MoveInput, float>(initial: 0f, step: Step);
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

        Assert.Equal(expected: 5f, actual: sr.State, precision: 4); // 2 then 3, in sequence order
        Assert.Equal(expected: 2u, actual: sr.LastProcessedSequence);

        sr.Enqueue(
            new MoveInput {
                Sequence = 1,
                Move = 99f,
            }
        ); // stale
        sr.Step(1f);
        Assert.Equal(expected: 5f, actual: sr.State, precision: 4);
    }

    [Fact]
    public void Client_And_Server_Converge()
    {
        var client = new ClientPrediction<MoveInput, float>(initial: 0f, step: Step);
        var server = new ServerReconciler<MoveInput, float>(initial: 0f, step: Step);

        for (int i = 0; i < 5; i++)
        {
            var input = client.Predict(input: new MoveInput { Move = 2f }, dt: 0.5f);
            server.Enqueue(input);
        }

        server.Step(0.5f);
        client.Reconcile(
            authoritative: server.State,
            lastProcessedSequence: server.LastProcessedSequence,
            dt: 0.5f
        );

        Assert.Equal(expected: server.State, actual: client.State, precision: 4);
        Assert.Equal(expected: 0, actual: client.PendingInputs);
    }

    // ── Interpolation ──────────────────────────────────────────────────────────
    [Fact]
    public void Interpolator_Lerps_Between_Samples()
    {
        var interp = new SnapshotInterpolator<float>(static (a, b, t) => a + ((b - a) * t));
        interp.Push(time: 0.0, value: 0f);
        interp.Push(time: 1.0, value: 10f);

        Assert.True(interp.TrySample(renderTime: 0.5, value: out float mid));
        Assert.Equal(expected: 5f, actual: mid, precision: 4);
        Assert.True(interp.TrySample(renderTime: 0.0, value: out float start));
        Assert.Equal(expected: 0f, actual: start, precision: 4);
    }

    [Fact]
    public void Interpolator_Clamps_Before_First_And_Extrapolates_After_Last()
    {
        var interp = new SnapshotInterpolator<float>(static (a, b, t) => a + ((b - a) * t));
        interp.Push(time: 1.0, value: 0f);
        interp.Push(time: 2.0, value: 10f);

        Assert.True(interp.TrySample(renderTime: 0.0, value: out float before));
        Assert.Equal(expected: 0f, actual: before, precision: 4); // clamped to first

        Assert.True(interp.TrySample(renderTime: 2.5, value: out float after));
        Assert.Equal(expected: 15f, actual: after, precision: 4); // extrapolated half a step
    }

    [Fact]
    public void Interpolator_Empty_Returns_False()
    {
        var interp = new SnapshotInterpolator<float>(static (a, b, t) => a + ((b - a) * t));
        Assert.False(interp.TrySample(renderTime: 1.0, value: out _));
    }

    // ── Fixed tick ─────────────────────────────────────────────────────────────
    [Fact]
    public void NetTick_Accumulates_Fixed_Steps()
    {
        var tick = new NetTick(0.1f);
        Assert.Equal(expected: 0, actual: tick.Advance(0.05f));
        Assert.Equal(expected: 1, actual: tick.Advance(0.06f)); // accum 0.11 → 1 step
        Assert.Equal(expected: 2, actual: tick.Advance(0.2f)); // accum ~0.21 → 2 steps
        Assert.Equal(expected: 3u, actual: tick.Tick);
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
