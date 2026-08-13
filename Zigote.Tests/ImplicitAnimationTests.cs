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
        minWidth: 0f,
        maxWidth: 200f,
        minHeight: 0f,
        maxHeight: 200f
    );

    [Fact]
    public void AnimatingAfterReAttachStillRequestsLayout()
    {
        var box = new AnimatedSize(new SizedBox(width: 100f, height: 20f));
        box.Attach(owner: null!, parent: null);
        box.Measure(Room); // first measure settles at the natural size

        box.Detach();
        box.Attach(owner: null!, parent: null);

        box.Child = new SizedBox(width: 100f, height: 80f);
        box.Measure(Room); // sees the new target and starts the transition

        box.NeedsLayout = false;
        Ticker.AdvanceAll(0.05f);

        Assert.True(
            condition: box.NeedsLayout,
            userMessage: "a tick after re-attach did not mark the widget for layout"
        );
    }

    [Fact]
    public void SwitcherSwappedWhileDetachedShowsTheNewChildOnceMounted()
    {
        var first = new SizedBox(width: 100f, height: 20f);
        var second = new SizedBox(width: 100f, height: 20f);
        var switcher = new AnimatedSwitcher(child: first, duration: 0.1f);
        switcher.Attach(owner: null!, parent: null);
        switcher.Measure(Room);

        // Unmounted (the pane is off-screen), swapped, then mounted again.
        switcher.Detach();
        switcher.Child = second;
        switcher.Attach(owner: null!, parent: null);
        switcher.Measure(Room);

        switcher.NeedsLayout = false;
        Ticker.AdvanceAll(0.2f);
        switcher.Measure(Room);

        Assert.True(
            condition: switcher.NeedsLayout,
            userMessage: "the cross-fade never asked for a frame after re-attach"
        );
        Assert.Same(expected: second, actual: switcher.Child);
    }
}
