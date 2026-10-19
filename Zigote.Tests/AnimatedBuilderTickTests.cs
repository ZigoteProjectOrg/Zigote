using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.Tests;

/// <summary>
///     A controller tick must re-run an AnimatedBuilder that sits under a cached ComposedWidget —
///     the app-wide layout request alone lets the ancestor skip re-measuring it.
/// </summary>
public class AnimatedBuilderTickTests
{
    private sealed class Host(Widget child) : ComposedWidget
    {
        protected override Widget Build(BuildContext context) => child;
    }

    [Fact]
    public void Tick_rebuilds_under_cached_ancestor()
    {
        var controller = new AnimationController(durationSeconds: 1f);
        int builds = 0;
        var builder = new AnimatedBuilder(controller, (_, _) =>
        {
            builds++;
            return new SizedBox(width: 10, height: 10);
        });
        var host = new Host(builder);
        host.Attach(owner: FakeApp(), parent: null);
        var box = new Constraints(minWidth: 0, maxWidth: 100, minHeight: 0, maxHeight: 100);
        host.Measure(box);
        Assert.Equal(expected: 1, actual: builds);

        controller.Seek(0.5f); // fires OnTick, as a playing controller does every frame
        host.Measure(box);
        Assert.Equal(expected: 2, actual: builds);
    }

    internal static App FakeApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App).GetField(name: "_repaint", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(obj: app, value: new RepaintTracker());
        var overlays = typeof(App).GetField(name: "_overlays", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!;
        overlays.SetValue(obj: app, value: Activator.CreateInstance(overlays.FieldType));
        return app;
    }
}

/// <summary>An AnimatedContainer under a cached ComposedWidget must actually change size as it ticks.</summary>
public class AnimatedContainerSizeTests
{
    private sealed class Host(Widget child) : ComposedWidget
    {
        protected override Widget Build(BuildContext context) => child;
    }

    [Fact]
    public void Width_animates_under_cached_ancestor()
    {
        var box = new AnimatedContainer(width: 80, height: 40, color: new Color(0xFF00FF00));
        var host = new Host(new Center(box));
        host.Attach(owner: AnimatedBuilderTickTests.FakeApp(), parent: null);
        var c = new Constraints(minWidth: 0, maxWidth: 400, minHeight: 0, maxHeight: 400);
        host.Measure(c);
        host.Layout(Offset.Zero);
        Assert.Equal(expected: 80f, actual: box.Bounds.Width, precision: 1);

        box.AnimateTo(width: 200);
        box.Controller.Seek(1f); // ticks the controller to its end
        host.Measure(c);
        host.Layout(Offset.Zero);
        Assert.Equal(expected: 200f, actual: box.Bounds.Width, precision: 1);
    }
}
