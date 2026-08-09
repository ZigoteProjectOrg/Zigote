using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="AdwBreakpointBin" /> answers for its OWN allocation, not the window — that is what
///     separates it from a media query — and the last matching breakpoint wins so a list can be
///     written narrowest-first like a stylesheet. <see cref="AdwMultiLayoutView" /> then re-parents
///     one set of children between arrangements rather than rebuilding them.
/// </summary>
public class AdwAdaptiveContainerTests
{
    private static void Lay(Widget w, float width, float height)
    {
        var wrapper = new ThemeProvider(ThemeData.Dark, w);
        wrapper.Measure(Constraints.Tight(width, height));
        wrapper.Layout(new Offset(0f, 0f));
    }

    private static AdwBreakpointBin Bin(out SizedBox wide, out SizedBox narrow)
    {
        var w = new SizedBox(10f, 10f);
        var n = new SizedBox(20f, 20f);
        wide = w;
        narrow = n;
        var bin = new AdwBreakpointBin(w);
        bin.Breakpoints.Add(
            new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(600f)) { Child = n }
        );
        return bin;
    }

    [Fact]
    public void TheBinFollowsItsOwnAllocationNotTheWindow()
    {
        var bin = Bin(out var wide, out var narrow);

        Lay(bin, 900f, 400f);
        Assert.Null(bin.CurrentBreakpoint);
        Assert.Contains(wide, bin.GetChildren());

        // Same "window", narrower allocation — the bin folds anyway.
        Lay(bin, 500f, 400f);
        Assert.NotNull(bin.CurrentBreakpoint);
        Assert.Contains(narrow, bin.GetChildren());
    }

    [Fact]
    public void TheLastMatchingBreakpointWins()
    {
        var a = new SizedBox(1f, 1f);
        var b = new SizedBox(2f, 2f);
        var bin = new AdwBreakpointBin(new SizedBox(3f, 3f));
        // Both match at 400px; the later one is the answer.
        bin.Breakpoints.Add(new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(800f)) { Child = a });
        bin.Breakpoints.Add(new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(600f)) { Child = b });

        Lay(bin, 400f, 300f);
        Assert.Contains(b, bin.GetChildren());
    }

    [Fact]
    public void ApplyAndUnapplyFireOnlyOnTheEdges()
    {
        int applied = 0, unapplied = 0;
        var bin = new AdwBreakpointBin(new SizedBox(1f, 1f));
        bin.Breakpoints.Add(
            new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(600f)) {
                Apply = () => applied++,
                Unapply = () => unapplied++,
            }
        );

        Lay(bin, 500f, 300f);
        Lay(bin, 480f, 300f); // still inside — no re-fire
        Assert.Equal(1, applied);
        Assert.Equal(0, unapplied);

        Lay(bin, 900f, 300f);
        Assert.Equal(1, unapplied);
    }

    [Theory]
    [InlineData(400f, 300f, true)] // 4:3
    [InlineData(300f, 400f, false)] // 3:4
    public void AspectRatioConditionsCompareWidthOverHeight(float w, float h, bool expected)
    {
        Assert.Equal(
            expected,
            AdwBreakpointCondition.MinAspectRatio(1f).Evaluate(new Size(w, h))
        );
    }

    /// <summary>A zero-height box has no ratio; it must answer false, not divide by zero.</summary>
    [Fact]
    public void AspectRatioOnADegenerateBoxIsFalse()
    {
        Assert.False(AdwBreakpointCondition.MinAspectRatio(1f).Evaluate(new Size(100f, 0f)));
        Assert.False(AdwBreakpointCondition.MaxAspectRatio(1f).Evaluate(new Size(100f, 0f)));
    }

    [Fact]
    public void CombinedConditionsAndOr()
    {
        var both = AdwBreakpointCondition.MaxWidth(600f).And(AdwBreakpointCondition.MinHeight(400f));
        Assert.True(both.Evaluate(new Size(500f, 500f)));
        Assert.False(both.Evaluate(new Size(500f, 300f)));

        var either = AdwBreakpointCondition.MaxWidth(600f).Or(AdwBreakpointCondition.MinHeight(400f));
        Assert.True(either.Evaluate(new Size(500f, 300f)));
        Assert.False(either.Evaluate(new Size(900f, 300f)));
    }

    /// <summary>
    ///     The point of slots: the SAME child instance appears in whichever layout is showing, so
    ///     state living in that child survives the swap.
    /// </summary>
    [Fact]
    public void MultiLayoutViewRebindsOneChildBetweenArrangements()
    {
        var shared = new SizedBox(5f, 5f);
        var view = new AdwMultiLayoutView {
            Children = { ["content"] = shared },
            Layouts = {
                new AdwLayout(
                    "wide",
                    new Row { Children = { new AdwLayoutSlot("content") } }
                ),
                new AdwLayout("narrow", new AdwLayoutSlot("content")),
            },
        };

        Lay(view, 800f, 400f);
        Assert.Equal("wide", view.CurrentLayout!.Name);

        view.LayoutName = "narrow";
        Lay(view, 400f, 400f);
        Assert.Equal("narrow", view.CurrentLayout!.Name);

        // The narrow layout's slot now owns it, and the wide layout's slot has let go — otherwise
        // the child would be laid out (and painted) twice.
        var narrowSlot = (AdwLayoutSlot)view.Layouts[1].Content;
        var wideSlot = (AdwLayoutSlot)view.Layouts[0].Content.GetChildren().First();
        Assert.Contains(shared, narrowSlot.GetChildren());
        Assert.Empty(wideSlot.GetChildren());
    }

    [Fact]
    public void AnUnknownLayoutNameFallsBackToTheFirst()
    {
        var view = new AdwMultiLayoutView {
            Layouts = { new AdwLayout("only", new SizedBox(1f, 1f)) },
            LayoutName = "does-not-exist",
        };
        Assert.Equal("only", view.CurrentLayout!.Name);
    }
}
