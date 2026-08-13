using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Functional.Tests;

/// <summary>
///     The View contract, headless: the builder sees the real inherited scope (never the dark-theme
///     fallback), signals it reads rebuild the subtree while closure state survives, a theme flip
///     rebuilds with the new tokens, and OnMounted is paired 1:1 with the mount period — including
///     across the detach/re-attach cycle that trips a bare Watch.
/// </summary>
public class ViewTests
{
    private static readonly App FakeOwner = FakeApp();

    private static readonly Constraints Box = new(maxWidth: 200f, maxHeight: 200f);

    // The engine's own widget tests drive Measure/Layout against an uninitialized App with its
    // repaint fields seeded — enough for the MarkNeeds*/Request* paths widgets hit outside a window.
    // _overlays is needed on top of _repaint because Watch's in-place relayout requests a scoped
    // repaint, and App.MarkPaintFor checks the overlay list to resolve the widget's layer.
    private static App FakeApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        Seed(name: "_repaint", value: new RepaintTracker());
        Seed(name: "_overlays", value: new List<Widget>());
        // A View's signal-change path enqueues itself for cross-thread invalidation.
        Seed(name: "_crossThreadInvalidations", value: new ConcurrentQueue<Widget>());
        return app;

        void Seed(string name, object value) =>
            typeof(App)
                .GetField(name: name, bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(obj: app, value: value);
    }

    private static ThemeProvider Mount(Widget child, ThemeData? theme = null)
    {
        var provider = new ThemeProvider(data: theme ?? ThemeData.Light, child: child);
        provider.Attach(owner: FakeOwner, parent: null);
        provider.Measure(Box);
        provider.Layout(Offset.Zero);
        return provider;
    }

    [Fact]
    public void Build_SeesTheInheritedScope_NotTheFallback()
    {
        ThemeData? seen = null;
        var view = new View(ctx =>
        {
            seen = ThemeProvider.Of(ctx);
            return new Label("hi");
        });

        Mount(view);

        Assert.Same(expected: ThemeData.Light, actual: seen);
    }

    [Fact]
    public void SignalChange_RebuildsTheSubtree()
    {
        var count = new Signal<int>(0);
        var builds = 0;
        Label? label = null;
        var view = new View(_ =>
        {
            builds++;
            return label = new Label($"{count.Value}");
        });

        var provider = Mount(view);
        Assert.Equal(expected: 1, actual: builds);
        Assert.Equal(expected: "0", actual: label!.Text);

        count.Value = 3; // schedules a rebuild — it lands in the next walk, not in place
        Assert.True(view.NeedsBuild);
        provider.Measure(Box);
        provider.Layout(Offset.Zero);

        Assert.Equal(expected: 2, actual: builds);
        Assert.Equal(expected: "3", actual: label!.Text);
        Assert.Same(expected: FakeOwner, actual: label.Owner); // the fresh subtree is attached
    }

    [Fact]
    public void ThemeFlip_Rebuilds_AndClosureStateSurvives()
    {
        var count = new Signal<int>(0);
        ThemeData? seen = null;
        Label? label = null;
        var view = new View(ctx =>
        {
            seen = ThemeProvider.Of(ctx);
            return label = new Label($"{count.Value}");
        });

        var provider = Mount(view);
        count.Value = 5;
        provider.Measure(Box);
        provider.Layout(Offset.Zero);
        Assert.Equal(expected: "5", actual: label!.Text);

        provider.Data = ThemeData.Dark; // NotifyDependents → the View is marked for rebuild
        provider.Measure(Box); // the rebuild lands inside the next walk
        provider.Layout(Offset.Zero);

        Assert.Same(expected: ThemeData.Dark, actual: seen);
        Assert.Equal(expected: "5", actual: label!.Text); // the closure signal kept its value
    }

    [Fact]
    public void Reattach_RebuildsInsideTheWalk_WithTheRealScope()
    {
        var themes = new List<ThemeData>();
        var view = new View(ctx =>
        {
            themes.Add(ThemeProvider.Of(ctx));
            return new Label("hi");
        });

        var provider = Mount(view);
        provider.Detach();
        provider.Attach(owner: FakeOwner, parent: null);
        provider.Measure(Box);
        provider.Layout(Offset.Zero);

        // Detach forces NeedsBuild, so the re-attach's first walk rebuilds — under the provider,
        // never against the empty out-of-walk scope a retained Watch re-evaluates in.
        Assert.Same(expected: ThemeData.Light, actual: themes[^1]);
    }

    [Fact]
    public void ThemeRead_RegistersTheView_AsProviderDependent()
    {
        var view = new View(ctx =>
        {
            ThemeProvider.Of(ctx);
            return new Label("x");
        });
        var provider = Mount(view);
        Assert.False(view.NeedsBuild);

        // The regression a Watch-wrapped builder reintroduces: its first evaluation happens during
        // the attach cascade, after ComposedWidget restored BuildOwner, so DependOn registers
        // nothing and a theme flip changes nothing.
        provider.Data = ThemeData.Dark;
        Assert.True(view.NeedsBuild);
    }

    [Fact]
    public void OnMounted_IsPairedWithTheMountPeriod()
    {
        var started = 0;
        var disposed = 0;
        var view = new View(_ => new Label("hi")) {
            OnMounted = () =>
            {
                started++;
                return new Stopper(() => disposed++);
            },
        };

        var provider = Mount(view);
        Assert.Equal(expected: 1, actual: started);
        Assert.Equal(expected: 0, actual: disposed);

        provider.Detach();
        Assert.Equal(expected: 1, actual: disposed);

        provider.Attach(owner: FakeOwner, parent: null);
        provider.Measure(Box);
        Assert.Equal(expected: 2, actual: started);
        Assert.Equal(expected: 1, actual: disposed);
    }

    [Fact]
    public void MissedHit_FallsThroughToContentBeneath()
    {
        // Align fills the constraints, so the View's bounds are 200×200 with hittable content only
        // in the 10×10 corner.
        var view = new View(_ => new Align(
            alignment: Alignment.TopLeft,
            child: new SizedBox(width: 10f, height: 10f)
        ));
        Mount(view);

        // Inside the View's bounds but past its child: a plain ComposedWidget would answer `this`
        // and swallow the click; a functional wrapper must let it fall through.
        Assert.Null(view.HitTest(new Offset(x: 150f, y: 150f)));
    }

    private sealed class Stopper(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
