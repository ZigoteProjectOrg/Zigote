using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Zigote.Core;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Navigation;

namespace Zigote.Tests;

/// <summary>
///     The context a widget's Build receives must keep working after the walk: a click handler
///     closing over it runs when the ambient inherited stack is empty, and used to find no
///     navigator (Push threw) and no theme (silent Dark fallback).
/// </summary>
public class BuildContextAnchorTests
{
    private static readonly Constraints Box = Constraints.Tight(width: 200, height: 200);

    private sealed class Probe : ComposedWidget
    {
        public BuildContext? Ctx;
        public Navigator? DuringBuild;

        protected override Widget Build(BuildContext context)
        {
            Ctx = context;
            DuringBuild = Navigator.MaybeOf(context);
            return new SizedBox(width: 10, height: 10);
        }
    }

    [Fact]
    public void Navigator_found_from_kept_context_after_the_walk()
    {
        var probe = new Probe();
        var nav = new Navigator { Home = probe };
        nav.Attach(owner: FakeApp(), parent: null);
        nav.Measure(Box);

        Assert.Same(expected: nav, actual: probe.DuringBuild);
        // Outside the walk — where a click handler runs. This threw "No Navigator found" before.
        Assert.Same(expected: nav, actual: Navigator.Of(probe.Ctx!));
    }

    [Fact]
    public void Theme_found_from_kept_context_after_the_walk()
    {
        var probe = new Probe();
        var theme = ThemeData.Light;
        var root = new ThemeProvider(theme, child: probe);
        root.Attach(owner: FakeApp(), parent: null);
        root.Measure(Box);

        Assert.Same(expected: theme, actual: ThemeProvider.Of(probe.Ctx!));
    }

    [Fact]
    public void Off_tree_measure_still_resolves_through_the_walk_stack()
    {
        // Never attached: the parent chain is empty, so the lookup must fall back to the stack.
        var probe = new Probe();
        var nav = new Navigator { Home = new ThemeProvider(ThemeData.Light, child: probe) };
        nav.Measure(Box);

        Assert.NotNull(probe.Ctx);
        Assert.Same(expected: nav, actual: probe.DuringBuild);
    }

    private static App FakeApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App).GetField(name: "_repaint", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(obj: app, value: new RepaintTracker());
        var overlays = typeof(App).GetField(name: "_overlays", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!;
        overlays.SetValue(obj: app, value: Activator.CreateInstance(overlays.FieldType));
        // A Navigator registers a system-back handler when it builds.
        var back = typeof(App).GetField(name: "_backHandlers", bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)!;
        back.SetValue(obj: app, value: Activator.CreateInstance(back.FieldType));
        return app;
    }
}
