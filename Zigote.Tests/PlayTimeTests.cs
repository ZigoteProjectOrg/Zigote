using Xunit;
using Zigote.Scripting;

namespace Zigote.Tests;

public class PlayTimeTests
{
    // A play session must start its clock at 0 — Time.Elapsed must not carry across replays.
    // (GameSession ctor calls Time.Reset(); ScriptWorld.Update accumulates += dt thereafter.)
    [Fact]
    public void Reset_Zeroes_Elapsed_And_Delta()
    {
        // Simulate a session having advanced the clock.
        Time._deltaTime = 0.016f;
        Time._elapsed = 42f;

        Time.Reset();

        Assert.Equal(expected: 0f, actual: Time.DeltaTime);
        Assert.Equal(expected: 0f, actual: Time.Elapsed);
    }
}

public class FixedStepBacklogTests
{
    // GameSession.MaxStepsPerUpdate drops the whole ticks it did not run and keeps the sub-tick
    // remainder, so interpolation stays continuous.
    [Fact]
    public void DropBacklog_RemovesWholeSteps_AndKeepsTheRemainder()
    {
        const float step = 1f / 120f;
        float accumulator = step * 5.5f;

        float dropped = Zigote.Runtime.Scene.GameSession.DropBacklog(accumulator: ref accumulator, step: step);

        Assert.Equal(expected: step * 5f, actual: dropped, precision: 5);
        Assert.InRange(actual: accumulator, low: 0f, high: step);
        Assert.Equal(expected: step * 0.5f, actual: accumulator, precision: 5);
    }
}
