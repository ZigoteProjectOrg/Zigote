using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     In a retained tree a property that is read during Measure/Build but written through a plain
///     auto-property is a silent no-op: the write lands in the field and nothing schedules the pass
///     that would notice. Nothing throws, nothing logs — the widget just stops responding, usually
///     only in the one code path that mutates it after the first frame. These drive each widget the
///     way a caller would and assert the layout actually moved.
/// </summary>
public class AdwRetainedModeTests
{
    private static void Lay(Widget w, float width = 600f, float height = 400f)
    {
        var wrapper = new ThemeProvider(data: ThemeData.Dark, child: w);
        wrapper.Measure(Constraints.Tight(width: width, height: height));
        wrapper.Layout(new Offset(x: 0f, y: 0f));
    }

    /// <summary>
    ///     Measure at an intrinsic size — a loose box, the way a Row or Column offers space. Tight
    ///     constraints force the widget to the parent's size, which would make every size assertion
    ///     below pass no matter what the widget did.
    /// </summary>
    private static void LayLoose(Widget w, float maxWidth = 600f, float maxHeight = 400f)
    {
        var wrapper = new ThemeProvider(data: ThemeData.Dark, child: w);
        wrapper.Measure(
            new Constraints(
                minWidth: 0f,
                maxWidth: maxWidth,
                minHeight: 0f,
                maxHeight: maxHeight
            )
        );
        wrapper.Layout(new Offset(x: 0f, y: 0f));
    }

    [Fact]
    public void PanedRespondsToEveryPropertyAfterTheFirstLayout()
    {
        var first = new SizedBox();
        var second = new SizedBox();
        var paned = new AdwPaned(first: first, second: second) { Position = 0.5f };
        Lay(paned);
        float half = first.Bounds.Width;

        paned.Position = 0.25f;
        Lay(paned);
        Assert.True(first.Bounds.Width < half);

        paned.Vertical = true;
        Lay(paned);
        // Vertical splits height and gives both panes the full width.
        Assert.Equal(expected: 600f, actual: first.Bounds.Width, precision: 3);
        Assert.True(first.Bounds.Height < 400f);

        paned.HandleWidth = 20f;
        Lay(paned);
        Assert.Equal(expected: first.Bounds.Bottom + 20f, actual: second.Bounds.Y, precision: 3);
    }

    [Fact]
    public void PanedMinPaneSizeAppliesToAnAlreadyLaidOutSplit()
    {
        var first = new SizedBox();
        var paned = new AdwPaned(first: first, second: new SizedBox()) {
            Position = 0.05f,
            MinPaneSize = 50f,
        };
        Lay(paned);
        Assert.InRange(actual: first.Bounds.Width, low: 49f, high: 51f);

        paned.MinPaneSize = 200f;
        Lay(paned);
        Assert.InRange(actual: first.Bounds.Width, low: 199f, high: 201f);
    }

    [Fact]
    public void ColorButtonResizesWhenItsWidthChanges()
    {
        // Constructible with no running app: the popover resolves its window when opened.
        var button = new AdwColorButton(Color.Rgb(r: 255, g: 0, b: 0));
        LayLoose(button);
        Assert.Equal(expected: 52f, actual: button.Bounds.Width, precision: 3);

        button.Width = 96f;
        LayLoose(button);
        Assert.Equal(expected: 96f, actual: button.Bounds.Width, precision: 3);
    }

    [Fact]
    public void SeparatorFollowsItsLengthAndOrientation()
    {
        var sep = new AdwSeparator();
        LayLoose(w: sep, maxWidth: 300f, maxHeight: 300f);
        Assert.Equal(expected: 1f, actual: sep.Bounds.Height, precision: 3);

        sep.Vertical = true;
        sep.Length = 40f;
        LayLoose(w: sep, maxWidth: 300f, maxHeight: 300f);
        Assert.Equal(expected: 1f, actual: sep.Bounds.Width, precision: 3);
        Assert.Equal(expected: 40f, actual: sep.Bounds.Height, precision: 3);
    }

    [Fact]
    public void ShortcutLabelRelaysOutWhenTheAcceleratorChanges()
    {
        var label = new AdwShortcutLabel("<Primary>s");
        LayLoose(label);
        float narrow = label.Bounds.Width;

        label.Accelerator = "<Primary><Shift><Alt>s";
        LayLoose(label);
        Assert.True(label.Bounds.Width > narrow);

        // An empty accelerator falls back to the disabled text rather than collapsing to nothing.
        label.Accelerator = "";
        LayLoose(label);
        Assert.True(label.Bounds.Width > 0f);
    }

    /// <summary>
    ///     The switcher sidebar must not re-create its rows when the visible page changes — that is
    ///     a selection move. Rebuilding would also drop focus and any hover mid-click.
    /// </summary>
    [Fact]
    public void ViewSwitcherSidebarMovesSelectionWithoutRebuildingRows()
    {
        var stack = new AdwViewStack(
            new AdwViewStackPage(name: "a", title: "Alpha", child: new SizedBox()),
            new AdwViewStackPage(name: "b", title: "Beta", child: new SizedBox())
        );
        var sidebar = new AdwViewSwitcherSidebar(stack);
        Lay(w: sidebar, width: 260f, height: 300f);

        int before = sidebar.RebuildCount;
        stack.VisibleName = "b";
        Lay(w: sidebar, width: 260f, height: 300f);

        Assert.Equal(expected: before, actual: sidebar.RebuildCount);
    }
}
