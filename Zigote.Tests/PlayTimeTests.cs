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
