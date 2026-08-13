using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     <see cref="Reactive.HasPendingDeferred" /> is what tells a frame loop it must not go to sleep:
///     a background thread that writes a signal only <em>parks</em> the
///     <see cref="EffectAffinity.Deferred" /> effects reacting to it, and an idle host that ignored
///     this flag would sit on that work until some unrelated event happened to wake it.
/// </summary>
[Collection("Reactive-serial")] // signals/effects share process-static reactive state
public class ReactiveDeferredDrainTests
{
    [Fact]
    public void PendingFlag_TracksTheParkedQueue_AndClearsOnDrain()
    {
        Reactive.DrainDeferred(); // start from a clean queue
        Assert.False(Reactive.HasPendingDeferred);

        var s = new Signal<int>(0);
        int runs = 0;
        using var e = new Effect(
            body: () =>
            {
                _ = s.Value;
                runs++;
            },
            affinity: EffectAffinity.Deferred
        );

        // Construction runs the body once inline, so it subscribes; nothing is parked yet.
        Reactive.DrainDeferred();
        int atRest = runs;
        Assert.False(Reactive.HasPendingDeferred);

        s.Value = 1;
        Assert.True(Reactive.HasPendingDeferred); // parked, NOT run
        Assert.Equal(expected: atRest, actual: runs);

        Reactive.DrainDeferred();
        Assert.False(Reactive.HasPendingDeferred);
        Assert.Equal(expected: atRest + 1, actual: runs);
    }

    [Fact]
    public void PendingFlag_IsSetByAWriteFromAnotherThread()
    {
        Reactive.DrainDeferred();
        var s = new Signal<int>(0);
        using var e = new Effect(body: () => _ = s.Value, affinity: EffectAffinity.Deferred);
        Reactive.DrainDeferred();

        var writer = new Thread(() => s.Value = 42);
        writer.Start();
        writer.Join();

        Assert.True(Reactive.HasPendingDeferred);
        Reactive.DrainDeferred();
        Assert.False(Reactive.HasPendingDeferred);
    }
}
