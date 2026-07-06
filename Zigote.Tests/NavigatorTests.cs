using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Navigation;

namespace Zigote.Tests;

/// <summary>
///     Exercises the Navigator stack semantics — push/pop with typed results, named routes,
///     declarative page reconciliation (Navigator 2.0), and replacement — through the public API.
///     Routes use zero-duration transitions so every mutation settles synchronously, keeping the
///     tests deterministic and free of the global <c>Ticker</c>.
/// </summary>
public class NavigatorTests
{
    private static readonly Constraints Tight = Constraints.Tight(200, 200);

    private static InstantRoute<T> Page<T>(string? name = null)
    {
        return new InstantRoute<T>(
            _ => new SizedBox(10, 10),
            name is null ? null : new RouteSettings(name)
        );
    }

    // Build the navigator into existence: Measure runs the StatefulWidget lifecycle (InitState),
    // which creates the NavigatorState and initial routes.
    private static NavigatorState Mount(Navigator nav)
    {
        nav.Measure(Tight);
        return nav.State!;
    }

    [Fact]
    public void Home_BecomesTheInitialRoute()
    {
        var state = Mount(new Navigator { Home = new SizedBox(10, 10) });

        Assert.Single(state.History);
        Assert.False(state.CanPop);
    }

    [Fact]
    public void Push_AddsRoute_AndBecomesCurrent()
    {
        var state = Mount(new Navigator { Home = new SizedBox(10, 10) });

        var route = Page<object?>();
        state.Push(route);

        Assert.Equal(2, state.History.Count);
        Assert.True(state.CanPop);
        Assert.Same(route, state.CurrentRoute);
    }

    [Fact]
    public async Task Pop_RemovesRoute_AndCompletesResult()
    {
        var state = Mount(new Navigator { Home = new SizedBox(10, 10) });

        var task = state.Push(Page<string>());
        Assert.False(task.IsCompleted);

        state.Pop("done");

        Assert.True(task.IsCompleted);
        Assert.Equal("done", await task);
        Assert.Single(state.History);
        Assert.False(state.CanPop);
    }

    [Fact]
    public void Pop_OnRootRoute_IsNoOp()
    {
        var state = Mount(new Navigator { Home = new SizedBox(10, 10) });

        state.Pop(); // nothing to pop — the base route stays

        Assert.Single(state.History);
    }

    [Fact]
    public async Task Dispose_CompletesPendingPushTasks()
    {
        // Regression: tearing the navigator down mid-flow must complete any awaited Push, or the
        // awaiter (and its captured continuation) leaks forever. Dispose previously only DisposeRoute'd.
        var nav = new Navigator { Home = new SizedBox(10, 10) };
        var state = Mount(nav);

        var task = state.Push(Page<string>());
        Assert.False(task.IsCompleted);

        state.Dispose();

        Assert.True(task.IsCompleted);
        Assert.Null(await task); // no result supplied → completes with default
    }

    [Fact]
    public void PushNamed_ResolvesFromRouteTable()
    {
        var state = Mount(
            new Navigator {
                Home = new SizedBox(10, 10),
                Routes = new Dictionary<string, WidgetBuilder> {
                    ["/details"] = _ => new SizedBox(20, 20),
                },
            }
        );

        state.PushNamed("/details");

        Assert.Equal(2, state.History.Count);
        Assert.Equal("/details", state.CurrentRoute!.Settings.Name);
    }

    [Fact]
    public void InitialRoute_SelectsFromRouteTable()
    {
        var state = Mount(
            new Navigator {
                InitialRoute = "/start",
                Routes = new Dictionary<string, WidgetBuilder> {
                    ["/start"] = _ => new SizedBox(20, 20),
                },
            }
        );

        Assert.Single(state.History);
        Assert.Equal("/start", state.CurrentRoute!.Settings.Name);
    }

    [Fact]
    public void PushReplacement_RemovesReplacedRoute()
    {
        var state = Mount(new Navigator { Home = new SizedBox(10, 10) });

        var first = Page<object?>();
        var firstTask = state.Push(first);
        Assert.Equal(2, state.History.Count);

        state.PushReplacement(Page<object?>());

        // The replaced route is gone (its task completes); the stack height is unchanged.
        Assert.Equal(2, state.History.Count);
        Assert.True(firstTask.IsCompleted);
        Assert.DoesNotContain(first, state.History);
    }

    [Fact]
    public void PopUntil_UnwindsToNamedRoute()
    {
        var state = Mount(new Navigator { Home = new SizedBox(10, 10) });

        state.Push(Page<object?>("a"));
        state.Push(Page<object?>("b"));
        state.Push(Page<object?>("c"));
        Assert.Equal(4, state.History.Count);

        state.PopUntil(Navigator.WithName("a"));

        Assert.Equal(2, state.History.Count); // home + "a"
        Assert.Equal("a", state.CurrentRoute!.Settings.Name);
    }

    [Fact]
    public void Pages_DescribeTheInitialStack()
    {
        var state = Mount(
            new Navigator {
                Pages = [
                    new MaterialPage(new SizedBox(10, 10)) { Key = new ValueKey<int>(1) },
                    new MaterialPage(new SizedBox(10, 10)) { Key = new ValueKey<int>(2) },
                ],
            }
        );

        Assert.Equal(2, state.History.Count);
    }

    [Fact]
    public void SetPages_ReconcilesByKey_PreservingRoutes()
    {
        var p1 = new MaterialPage(new SizedBox(10, 10)) {
            Key = new ValueKey<int>(1),
            Animate = false,
        };
        var p2 = new MaterialPage(new SizedBox(10, 10)) {
            Key = new ValueKey<int>(2),
            Animate = false,
        };
        var state = Mount(new Navigator { Pages = [p1, p2] });

        var routeForKey1 = state.History[0];
        var routeForKey2 = state.History[1];

        // Reorder + add a third page. Matched pages must keep their live route instance.
        var p3 = new MaterialPage(new SizedBox(10, 10)) {
            Key = new ValueKey<int>(3),
            Animate = false,
        };
        state.SetPages([p2, p1, p3]);

        Assert.Equal(3, state.History.Count);
        Assert.Same(routeForKey2, state.History[0]);
        Assert.Same(routeForKey1, state.History[1]);
        Assert.NotSame(routeForKey1, state.History[2]);
        Assert.NotSame(routeForKey2, state.History[2]);
    }

    [Fact]
    public void SetPages_RemovingPage_DropsItsRoute()
    {
        var p1 = new MaterialPage(new SizedBox(10, 10)) {
            Key = new ValueKey<int>(1),
            Animate = false,
        };
        var p2 = new MaterialPage(new SizedBox(10, 10)) {
            Key = new ValueKey<int>(2),
            Animate = false,
        };
        var state = Mount(new Navigator { Pages = [p1, p2] });
        var dropped = state.History[1];

        state.SetPages([p1]);

        Assert.Single(state.History);
        Assert.DoesNotContain(dropped, state.History);
    }

    [Fact]
    public void RenderPath_RunsMeasureLayoutPaint_AcrossTheStack()
    {
        var nav = new Navigator { Home = new SizedBox(10, 10) };
        var state = Mount(nav);
        state.Push(Page<object?>());

        // A full Measure → Layout → Paint cycle must run the route stack (build + attach + lay out new
        // content) without error, and the navigator fills the window.
        var size = nav.Measure(Tight);
        nav.Layout(Offset.Zero);
        nav.Paint(new PaintList());

        Assert.Equal(200f, size.Width, 2);
        Assert.Equal(200f, size.Height, 2);
        Assert.Equal(2, state.History.Count);
    }

    [Fact]
    public void OnPopPage_CanVetoAPagePop()
    {
        var p1 = new MaterialPage(new SizedBox(10, 10)) {
            Key = new ValueKey<int>(1),
            Animate = false,
        };
        var p2 = new MaterialPage(new SizedBox(10, 10)) {
            Key = new ValueKey<int>(2),
            Animate = false,
        };

        var allow = false;
        var state = Mount(
            new Navigator {
                Pages = [p1, p2],
                OnPopPage = (_, _) => allow,
            }
        );

        state.Pop(); // vetoed
        Assert.Equal(2, state.History.Count);

        allow = true;
        state.Pop(); // permitted
        Assert.Single(state.History);
    }

    // A page route with no transition — push/pop/replace complete in the same call.
    private sealed class InstantRoute<T>(WidgetBuilder builder, RouteSettings? settings = null)
        : MaterialPageRoute<T>(builder, settings)
    {
        public override float TransitionDuration => 0f;
    }
}