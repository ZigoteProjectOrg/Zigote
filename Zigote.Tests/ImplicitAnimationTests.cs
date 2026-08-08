using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.Tests;

/// <summary>
///     An implicitly-animated widget that is unmounted and remounted — a split view's content pane
///     folding on a narrow window, a route coming back — must still ask for frames afterwards.
///     Detach unsubscribed its controller tick handler; if Attach does not restore it, the
///     controller advances while nothing marks the tree dirty, so the subtree paints its stale
///     progress until something unrelated (a window resize) forces a relayout.
/// </summary>
[Collection(
    "Ticker"
)] // static Ticker.Active is shared; AdvanceAll in one class ticks another class's widgets
public class ImplicitAnimationTests
{
    private static readonly Constraints Room = new(
        0f,
        200f,
        0f,
        200f
    );

    [Fact]
    public void AnimatingAfterReAttachStillRequestsLayout()
    {
        var box = new AnimatedSize(new SizedBox(100f, 20f));
        box.Attach(null!, null);
        box.Measure(Room); // first measure settles at the natural size

        box.Detach();
        box.Attach(null!, null);

        box.Child = new SizedBox(100f, 80f);
        box.Measure(Room); // sees the new target and starts the transition

        box.NeedsLayout = false;
        Ticker.AdvanceAll(0.05f);

        Assert.True(
            box.NeedsLayout,
            "a tick after re-attach did not mark the widget for layout"
        );
    }

    [Fact]
    public void SwitcherSwappedWhileDetachedShowsTheNewChildOnceMounted()
    {
        var first = new SizedBox(100f, 20f);
        var second = new SizedBox(100f, 20f);
        var switcher = new AnimatedSwitcher(first, 0.1f);
        switcher.Attach(null!, null);
        switcher.Measure(Room);

        // Unmounted (the pane is off-screen), swapped, then mounted again.
        switcher.Detach();
        switcher.Child = second;
        switcher.Attach(null!, null);
        switcher.Measure(Room);

        switcher.NeedsLayout = false;
        Ticker.AdvanceAll(0.2f);
        switcher.Measure(Room);

        Assert.True(
            switcher.NeedsLayout,
            "the cross-fade never asked for a frame after re-attach"
        );
        Assert.Same(second, switcher.Child);
    }
}