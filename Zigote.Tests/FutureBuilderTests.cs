using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

[Collection("Ticker")] // the widget polls its task on a ticker; AnyActive is process-wide
public class FutureBuilderTests
{
    private static FutureBuilder<int> Mount(Task<int>? future, List<ConnectionState> seen)
    {
        var builder = new FutureBuilder<int>(
            future: future,
            builder: (_, snapshot) =>
            {
                seen.Add(snapshot.ConnectionState);
                return new SizedBox(width: 10f, height: 10f);
            }
        );
        builder.Attach(owner: null!, parent: null);
        return builder;
    }

    private static void Pump(Widget widget)
    {
        widget.Measure(Constraints.Loose(width: 100f, height: 100f));
        widget.Layout(Offset.Zero);
        Ticker.AdvanceAll(0.016f);
    }

    [Fact]
    public void ASettledFutureStartsNoTicker()
    {
        // A running ticker keeps the frame loop awake and repainting; one that has nothing to
        // wait for must not exist.
        var seen = new List<ConnectionState>();
        var builder = Mount(future: Task.FromResult(1), seen: seen);
        Pump(builder);

        Assert.False(Ticker.AnyActive);
        Assert.Equal(expected: ConnectionState.Done, actual: seen[^1]);
        builder.Detach();
    }

    [Fact]
    public void APendingFutureIsWatchedOnlyUntilItCompletes()
    {
        var seen = new List<ConnectionState>();
        var source = new TaskCompletionSource<int>();
        var builder = Mount(future: source.Task, seen: seen);
        Pump(builder);
        Assert.True(Ticker.AnyActive);
        Assert.Equal(expected: ConnectionState.Waiting, actual: seen[^1]);

        source.SetResult(7);
        Pump(builder); // the tick notices completion and asks for a rebuild
        Pump(builder);

        Assert.False(Ticker.AnyActive);
        Assert.Equal(expected: ConnectionState.Done, actual: seen[^1]);
        builder.Detach();
    }
}
