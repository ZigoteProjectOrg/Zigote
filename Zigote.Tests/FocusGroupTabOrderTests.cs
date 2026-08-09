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
    /// <summary>A focus group of N buttons, the <paramref name="target" />th being its Tab target.</summary>
    private sealed class Group : Widget, IFocusGroup
    {
        private readonly Column _column = new() {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };

        public Group(int count, int target)
        {
            for (var i = 0; i < count; i++)
                _column.Children.Add(
                    new Pressable {
                        SemanticsLabel = $"row{i}",
                        Child = new SizedBox(80f, 20f),
                    }
                );
            TabTarget = Row(target);
        }

        public Widget? TabTarget { get; }

        public Widget Row(int i)
        {
            return _column.Children[i];
        }

        public override Size Measure(Constraints c)
        {
            return _column.Measure(c);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                80f,
                _column.Children.Count * 20f
            );
            _column.Layout(origin);
        }

        public override void Paint(PaintList paint)
        {
            _column.Paint(paint);
        }

        public override IEnumerable<Widget> GetChildren()
        {
            return [_column];
        }
    }

    private static (Widget Root, Group Group, Pressable Before, Pressable After) Tree()
    {
        var before = new Pressable {
            SemanticsLabel = "before",
            Child = new SizedBox(80f, 20f),
        };
        var after = new Pressable { SemanticsLabel = "after", Child = new SizedBox(80f, 20f) };
        var group = new Group(5, 2);
        var root = new ThemeProvider(
            ThemeData.Dark,
            new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) { Children = { before, group, after } }
        );
        root.Measure(Constraints.Tight(200f, 400f));
        root.Layout(new Offset(0f, 0f));
        return (root, group, before, after);
    }

    [Fact]
    public void TabOrderCollapsesTheGroupToOneStop()
    {
        var (root, group, before, after) = Tree();

        // Every row is focusable; only one of them is a Tab stop, between the outside buttons.
        var all = FocusTraversal.Focusables(root);
        var order = FocusTraversal.TabOrder(root, null);
        Assert.True(all.Count > order.Count, $"expected grouping to shrink {all.Count}");
        Assert.Equal([before, group.TabTarget!, after], order);
    }

    [Fact]
    public void TabLeavesTheGroupFromWhereverTheArrowsLeftOff()
    {
        var (root, group, _, after) = Tree();
        var inside = group.Row(4); // arrowed down to the last row

        var order = FocusTraversal.TabOrder(root, inside);
        Assert.Contains(inside, order);
        Assert.DoesNotContain(group.TabTarget!, order);
        Assert.Equal(after, FocusTraversal.NextInTab(order, inside, false));
    }

    [Fact]
    public void ArrowTraversalStillReachesEveryRow()
    {
        var (root, group, _, _) = Tree();
        var all = FocusTraversal.Focusables(root);

        // Directional traversal uses the ungrouped list, so Down walks row to row.
        var next = FocusTraversal.Directional(all, group.Row(1), 0f, 1f);
        Assert.Equal(group.Row(2), next);
    }

    [Fact]
    public void ATreeWithNoGroupsIsUnchanged()
    {
        var a = new Pressable { Child = new SizedBox(80f, 20f) };
        var b = new Pressable { Child = new SizedBox(80f, 20f) };
        var root = new ThemeProvider(
            ThemeData.Dark,
            new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) { Children = { a, b } }
        );
        root.Measure(Constraints.Tight(200f, 200f));
        root.Layout(new Offset(0f, 0f));

        Assert.Equal(FocusTraversal.Focusables(root), FocusTraversal.TabOrder(root, null));
    }

    /// <summary>The real subject: AdwSidebar is a group whose Tab target is the selected row.</summary>
    [Fact]
    public void AdwSidebarIsOneTabStopAtTheSelectedRow()
    {
        var sidebar = new AdwSidebar(
            new AdwSidebarSection(
                null,
                new AdwSidebarItem("General", Icons.Settings),
                new AdwSidebarItem("Appearance", Icons.Palette),
                new AdwSidebarItem("Advanced", Icons.Tune)
            )
        ) { Selected = 1 };
        var root = new ThemeProvider(ThemeData.Dark, sidebar);
        root.Measure(Constraints.Tight(260f, 400f));
        root.Layout(new Offset(0f, 0f));

        Assert.Equal(3, FocusTraversal.Focusables(root).Count);
        var order = FocusTraversal.TabOrder(root, null);
        Assert.Single(order);
        Assert.Same(((IFocusGroup)sidebar).TabTarget, order[0]);
    }
}
