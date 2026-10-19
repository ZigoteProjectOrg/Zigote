using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="AdwPullToRefresh" /> driven headlessly by touch: a short pull springs back, a
///     long one runs the refresh exactly once and holds until its task completes.
/// </summary>
public class AdwPullToRefreshTests
{
    private static (AdwPullToRefresh Pull, SizedBox Content, ThemeProvider Root) Laid(Func<Task> onRefresh)
    {
        var content = new SizedBox();
        var pull = new AdwPullToRefresh(child: content, onRefresh: onRefresh);
        var root = new ThemeProvider(data: ThemeData.Dark, child: pull);
        Relayout(root);
        return (pull, content, root);
    }

    private static void Relayout(Widget root)
    {
        root.Measure(Constraints.Tight(width: 400f, height: 600f));
        root.Layout(new Offset(x: 0f, y: 0f));
    }

    [Fact]
    public void ShortPull_DoesNotRefresh()
    {
        int calls = 0;
        var (pull, _, _) = Laid(() => { calls++; return Task.CompletedTask; });

        pull.OnTouchScroll(dx: 0f, dy: pull.Threshold); // half of it after resistance
        pull.OnTouchFling(velocityX: 0f, velocityY: 500f);

        Assert.Equal(expected: 0, actual: calls);
        Assert.False(pull.IsRefreshing);
    }

    [Fact]
    public void LongPull_ShiftsContent_RefreshesOnce_AndHoldsUntilDone()
    {
        int calls = 0;
        var done = new TaskCompletionSource();
        var (pull, content, root) = Laid(() => { calls++; return done.Task; });

        pull.OnTouchScroll(dx: 0f, dy: pull.Threshold * 3f);
        Relayout(root);
        Assert.True(content.Bounds.Y >= pull.Threshold);

        pull.OnTouchFling(velocityX: 0f, velocityY: 500f);
        pull.OnTouchFling(velocityX: 0f, velocityY: 500f);
        Assert.Equal(expected: 1, actual: calls);
        Assert.True(pull.IsRefreshing);

        done.SetResult();
        SpinWait.SpinUntil(condition: () => !pull.IsRefreshing, millisecondsTimeout: 2000);
        Assert.False(pull.IsRefreshing);
    }
}
