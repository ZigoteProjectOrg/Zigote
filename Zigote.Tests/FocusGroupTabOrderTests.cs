using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Focus;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="IFocusGroup" /> collapses a list to one Tab stop — GTK's "tab-behavior: item",
///     which libadwaita 1.10 (GNOME 51) switched AdwSidebar to. The distinction that matters is that
///     it only affects <b>Tab</b>: arrow traversal must still see every row, or a keyboard user can
///     reach the list but never move inside it.
/// </summary>
public class FocusGroupTabOrderTests
{
    private static (Widget Root, Group Group, Pressable Before, Pressable After) Tree()
    {
        var before = new Pressable {
            SemanticsLabel = "before",
            Child = new SizedBox(width: 80f, height: 20f),
        };
        var after = new Pressable {
            SemanticsLabel = "after",
            Child = new SizedBox(width: 80f, height: 20f),
        };
        var group = new Group(count: 5, target: 2);
        var root = new ThemeProvider(
            data: ThemeData.Dark,
            child: new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    before,
                    group,
                    after,
                },
            }
        );
        root.Measure(Constraints.Tight(width: 200f, height: 400f));
        root.Layout(new Offset(x: 0f, y: 0f));
        return (root, group, before, after);
    }

    [Fact]
    public void TabOrderCollapsesTheGroupToOneStop()
    {
        var (root, group, before, after) = Tree();

        // Every row is focusable; only one of them is a Tab stop, between the outside buttons.
        var all = FocusTraversal.Focusables(root);
        var order = FocusTraversal.TabOrder(scope: root, focused: null);
        Assert.True(
            condition: all.Count > order.Count,
            userMessage: $"expected grouping to shrink {all.Count}"
        );
        Assert.Equal(expected: [before, group.TabTarget!, after], actual: order);
    }

    [Fact]
    public void TabLeavesTheGroupFromWhereverTheArrowsLeftOff()
    {
        var (root, group, _, after) = Tree();
        var inside = group.Row(4); // arrowed down to the last row

        var order = FocusTraversal.TabOrder(scope: root, focused: inside);
        Assert.Contains(expected: inside, collection: order);
        Assert.DoesNotContain(expected: group.TabTarget!, collection: order);
        Assert.Equal(
            expected: after,
            actual: FocusTraversal.NextInTab(order: order, current: inside, backwards: false)
        );
    }

    [Fact]
    public void ArrowTraversalStillReachesEveryRow()
    {
        var (root, group, _, _) = Tree();
        var all = FocusTraversal.Focusables(root);

        // Directional traversal uses the ungrouped list, so Down walks row to row.
        var next = FocusTraversal.Directional(
            order: all,
            current: group.Row(1),
            dx: 0f,
            dy: 1f
        );
        Assert.Equal(expected: group.Row(2), actual: next);
    }

    [Fact]
    public void ATreeWithNoGroupsIsUnchanged()
    {
        var a = new Pressable { Child = new SizedBox(width: 80f, height: 20f) };
        var b = new Pressable { Child = new SizedBox(width: 80f, height: 20f) };
        var root = new ThemeProvider(
            data: ThemeData.Dark,
            child: new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    a,
                    b,
                },
            }
        );
        root.Measure(Constraints.Tight(width: 200f, height: 200f));
        root.Layout(new Offset(x: 0f, y: 0f));

        Assert.Equal(
            expected: FocusTraversal.Focusables(root),
            actual: FocusTraversal.TabOrder(scope: root, focused: null)
        );
    }

    /// <summary>The real subject: AdwSidebar is a group whose Tab target is the selected row.</summary>
    [Fact]
    public void AdwSidebarIsOneTabStopAtTheSelectedRow()
    {
        var sidebar = new AdwSidebar(
            new AdwSidebarSection(
                title: null,
                new AdwSidebarItem(title: "General", iconName: Icons.Settings),
                new AdwSidebarItem(title: "Appearance", iconName: Icons.Palette),
                new AdwSidebarItem(title: "Advanced", iconName: Icons.Tune)
            )
        ) { Selected = 1 };
        var root = new ThemeProvider(data: ThemeData.Dark, child: sidebar);
        root.Measure(Constraints.Tight(width: 260f, height: 400f));
        root.Layout(new Offset(x: 0f, y: 0f));

        Assert.Equal(expected: 3, actual: FocusTraversal.Focusables(root).Count);
        var order = FocusTraversal.TabOrder(scope: root, focused: null);
        Assert.Single(order);
        Assert.Same(expected: sidebar.TabTarget, actual: order[0]);
    }

    /// <summary>A focus group of N buttons, the <paramref name="target" />th being its Tab target.</summary>
    private sealed class Group : Widget, IFocusGroup
    {
        private readonly Column _column = new() {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };

        public Group(int count, int target)
        {
            for (int i = 0; i < count; i++)
            {
                _column.Children.Add(
                    new Pressable {
                        SemanticsLabel = $"row{i}",
                        Child = new SizedBox(width: 80f, height: 20f),
                    }
                );
            }

            TabTarget = Row(target);
        }

        public Widget? TabTarget { get; }

        public Widget Row(int i) => _column.Children[i];

        public override Size Measure(Constraints c) => _column.Measure(c);

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: 80f,
                height: _column.Children.Count * 20f
            );
            _column.Layout(origin);
        }

        public override void Paint(PaintList paint) => _column.Paint(paint);

        public override IEnumerable<Widget> GetChildren() => [_column];
    }
}
