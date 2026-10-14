using Xunit;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Tests;

/// <summary>
///     <c>Image.LoadAsync</c> admits a bounded number of loads at once, fetch included, so a flood
///     of requests cannot park an unbounded pile of encoded payloads behind the decoder — and a load
///     cancelled while it waits for a slot never fetches at all.
/// </summary>
public class ImageLoadGateTests
{
    [Fact]
    public async Task Flood_FetchesOnlyUpToTheCap_AndCancelledWaitersNeverFetch()
    {
        int cap = Image.MaxInFlightLoads;
        int started = 0;
        var images = new Image[cap * 3];
        var tasks = new Task[images.Length];
        bool[] fetching = new bool[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            images[i] = new Image();
            int index = i;
            tasks[i] = images[i].LoadAsync(async ct =>
                {
                    Volatile.Write(location: ref fetching[index], value: true);
                    Interlocked.Increment(ref started);
                    await Task.Delay(millisecondsDelay: Timeout.Infinite, cancellationToken: ct);
                    return [];
                }
            );
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref started) < cap && DateTime.UtcNow < deadline) await Task.Delay(5);
        await Task.Delay(50); // room for an over-admitted fetch to show itself
        Assert.Equal(expected: cap, actual: Volatile.Read(ref started));

        // Waiters first: disposing a slot holder frees its slot for any waiter still live.
        for (int i = 0; i < images.Length; i++)
            if (!Volatile.Read(ref fetching[i]))
                images[i].Dispose();
        foreach (var image in images) image.Dispose();
        // Task.Run reports a cancelled token as a cancelled task; either way the load is over.
        await Task.WhenAll(tasks).ContinueWith(
            continuationAction: _ => { },
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: cap, actual: Volatile.Read(ref started));

        // Every slot came back: a fresh load is admitted.
        var admitted = new TaskCompletionSource();
        using var again = new Image();
        _ = again.LoadAsync(ct =>
            {
                admitted.TrySetResult();
                return Task.FromCanceled<byte[]>(new CancellationToken(canceled: true));
            }
        );
        await admitted.Task.WaitAsync(
            timeout: TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken
        );
    }
}
