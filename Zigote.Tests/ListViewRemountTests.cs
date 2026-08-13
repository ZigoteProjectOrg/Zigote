using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.Tests;

/// <summary>
///     A retained ListView, filled once and then detached and re-attached by a wrapper rebuild
///     (opening a bottom sheet rebuilds the tree around the whole app), keeps its rows: still in
///     <c>Items</c>, still owned and parented, still measured and laid out — without the page
///     refilling it, because a change-guard keyed on the source data will skip that.
///     <para>
///         <b>Scope, honestly:</b> these lock in the contract, they do not reproduce Timbre's
///         blank-list bug (the one its <c>_remount</c> flag works around). All three still pass with
///         the <c>NeedsLayout</c>/<c>NeedsPaint</c> flags in <see cref="Widget.Detach" /> reverted,
///         so they do not exercise the stale-ancestor-measure-cache path that fix exists for. Treat
///         them as coverage of the re-attach contract, not as a regression test for that bug.
///     </para>
/// </summary>
public class ListViewRemountTests
{
    private static readonly App FakeOwner = FakeApp();

    private static App FakeApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App)
            .GetField(
                name: "_repaint",
                bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic
            )!
            .SetValue(obj: app, value: new RepaintTracker());
        return app;
    }

    /// <summary>Mirrors LibraryPage: a retained list, filled only when the source data changed.</summary>
    private sealed class Page : ComposedWidget
    {
        private readonly ListView _list = new() { ItemHeight = 20f };
        private int _builtFrom = -1;
        public int Fills;

        public int Data = 3;

        public ListView List => _list;

        protected override Widget Build(BuildContext context)
        {
            return new Watch(() =>
            {
                if (_builtFrom != Data)
                {
                    _builtFrom = Data;
                    Fills++;
                    _list.SetItems([]);
                    for (var i = 0; i < Data; i++)
                        _list.AddItem(new SizedBox(width: 10f, height: 20f));
                }

                return _list;
            });
        }
    }

    /// <summary>
    ///     Mirrors PlaylistsPage exactly: a retained AnimatedSwitcher whose child is a freshly-built
    ///     Column each build, holding the retained list under a fresh Expanded.
    /// </summary>
    private sealed class SwitcherPage : ComposedWidget
    {
        private readonly ListView _list = new() { ItemHeight = 20f };
        private readonly AnimatedSwitcher _transition = new(SizedBox.Shrink(), 0.18f);
        private int _builtFrom = -1;
        public int Data = 3;
        public int Fills;

        public ListView List => _list;

        protected override Widget Build(BuildContext context)
        {
            return new Watch(() =>
            {
                if (_builtFrom != Data)
                {
                    _builtFrom = Data;
                    Fills++;
                    _list.SetItems([]);
                    for (var i = 0; i < Data; i++)
                        _list.AddItem(new SizedBox(width: 10f, height: 20f));
                }

                _transition.Child = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch)
                {
                    Children =
                    {
                        new Padding(EdgeInsets.All(4f), new SizedBox(width: 10f, height: 10f)),
                        new Expanded(_list)
                    }
                };
                return _transition;
            });
        }
    }

    /// <summary>
    ///     The real trigger: a wrapper rebuild around the whole app (opening the bottom sheet) swaps
    ///     the container above a retained page, detaching and re-attaching it — while the root's
    ///     measure cache is warm at unchanged constraints.
    /// </summary>
    private sealed class Root(Widget page) : ComposedWidget
    {
        public readonly Signal<bool> SheetOpen = new(false);

        protected override Widget Build(BuildContext context)
        {
            return new Watch(() => SheetOpen.Value
                ? new Column { Children = { new SizedBox(width: 10f, height: 10f), page } }
                : new Column { Children = { page } });
        }
    }

    [Fact]
    public void WrapperRebuild_AroundRetainedList_KeepsRowsLaidOut()
    {
        var page = new SwitcherPage();
        var root = new Root(page);
        root.Attach(owner: FakeOwner, parent: null);

        var c = new Constraints(maxWidth: 100f, maxHeight: 100f);
        root.Measure(c);
        root.Layout(Offset.Zero);
        Assert.All(page.List.Items, r => Assert.True(r.Bounds.Height > 0f));

        // Open the sheet: the wrapper Column is replaced, so the page is detached and re-attached.
        root.SheetOpen.Value = true;
        root.Measure(c); // same constraints — the cache-warm path the fix exists for
        root.Layout(Offset.Zero);

        Assert.Equal(expected: 1, actual: page.Fills); // guard skipped the refill, as designed
        Assert.Equal(expected: 3, actual: page.List.Count);
        foreach (var row in page.List.Items)
            Assert.NotNull(row.Owner);
        Assert.All(page.List.Items, r => Assert.True(r.Bounds.Height > 0f));
    }

    [Fact]
    public void RetainedList_UnderAnimatedSwitcher_SurvivesDetachReattach()
    {
        var page = new SwitcherPage();
        page.Attach(owner: FakeOwner, parent: null);
        page.Measure(new Constraints(maxWidth: 100f, maxHeight: 100f));

        Assert.Equal(expected: 3, actual: page.List.Count);
        foreach (var row in page.List.Items)
            Assert.Same(expected: FakeOwner, actual: row.Owner);

        page.Layout(Offset.Zero);
        Assert.All(page.List.Items, r => Assert.True(r.Bounds.Height > 0f));

        page.Detach();
        page.Attach(owner: FakeOwner, parent: null);
        page.Measure(new Constraints(maxWidth: 100f, maxHeight: 100f));
        page.Layout(Offset.Zero);

        Assert.Equal(expected: 1, actual: page.Fills); // guard skipped the refill, as designed
        Assert.Equal(expected: 3, actual: page.List.Count);
        Assert.Same(expected: FakeOwner, actual: page.List.Owner);
        foreach (var row in page.List.Items)
        {
            Assert.Same(expected: page.List, actual: row.Parent);
            Assert.Same(expected: FakeOwner, actual: row.Owner);
        }

        // The symptom: rows present and attached, but never measured/laid out, so nothing paints.
        Assert.True(condition: page.List.Bounds.Height > 0f, userMessage: "list has no bounds");
        Assert.All(page.List.Items, r => Assert.True(r.Bounds.Height > 0f));
    }

    [Fact]
    public void RetainedList_SurvivesDetachReattach_WithoutRefilling()
    {
        var page = new Page();
        page.Attach(owner: FakeOwner, parent: null);
        page.Measure(new Constraints(maxWidth: 100f, maxHeight: 100f));

        Assert.Equal(expected: 3, actual: page.List.Count);
        Assert.Equal(expected: 1, actual: page.Fills);
        foreach (var row in page.List.Items)
            Assert.Same(expected: FakeOwner, actual: row.Owner);

        // The wrapper rebuild: same page instance, out of the tree and back in.
        page.Detach();
        page.Attach(owner: FakeOwner, parent: null);
        page.Measure(new Constraints(maxWidth: 100f, maxHeight: 100f));

        Assert.Equal(expected: 1, actual: page.Fills); // guard skipped the refill, as designed
        Assert.Equal(expected: 3, actual: page.List.Count);
        foreach (var row in page.List.Items)
        {
            Assert.Same(expected: FakeOwner, actual: row.Owner);
            Assert.Same(expected: page.List, actual: row.Parent);
        }
    }
}
