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
        var wrapper = new ThemeProvider(data: ThemeData.Dark, child: w);
        wrapper.Measure(Constraints.Tight(width: width, height: height));
        wrapper.Layout(new Offset(x: 0f, y: 0f));
    }

    private static AdwBreakpointBin Bin(out SizedBox wide, out SizedBox narrow)
    {
        var w = new SizedBox(width: 10f, height: 10f);
        var n = new SizedBox(width: 20f, height: 20f);
        wide = w;
        narrow = n;
        var bin = new AdwBreakpointBin(w);
        bin.Breakpoints.Add(new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(600f)) { Child = n });
        return bin;
    }

    [Fact]
    public void TheBinFollowsItsOwnAllocationNotTheWindow()
    {
        var bin = Bin(wide: out var wide, narrow: out var narrow);

        Lay(w: bin, width: 900f, height: 400f);
        Assert.Null(bin.CurrentBreakpoint);
        Assert.Contains(expected: wide, collection: bin.GetChildren());

        // Same "window", narrower allocation — the bin folds anyway.
        Lay(w: bin, width: 500f, height: 400f);
        Assert.NotNull(bin.CurrentBreakpoint);
        Assert.Contains(expected: narrow, collection: bin.GetChildren());
    }

    [Fact]
    public void TheLastMatchingBreakpointWins()
    {
        var a = new SizedBox(width: 1f, height: 1f);
        var b = new SizedBox(width: 2f, height: 2f);
        var bin = new AdwBreakpointBin(new SizedBox(width: 3f, height: 3f));
        // Both match at 400px; the later one is the answer.
        bin.Breakpoints.Add(new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(800f)) { Child = a });
        bin.Breakpoints.Add(new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(600f)) { Child = b });

        Lay(w: bin, width: 400f, height: 300f);
        Assert.Contains(expected: b, collection: bin.GetChildren());
    }

    [Fact]
    public void ApplyAndUnapplyFireOnlyOnTheEdges()
    {
        int applied = 0, unapplied = 0;
        var bin = new AdwBreakpointBin(new SizedBox(width: 1f, height: 1f));
        bin.Breakpoints.Add(
            new AdwBreakpoint(AdwBreakpointCondition.MaxWidth(600f)) {
                Apply = () => applied++,
                Unapply = () => unapplied++,
            }
        );

        Lay(w: bin, width: 500f, height: 300f);
        Lay(w: bin, width: 480f, height: 300f); // still inside — no re-fire
        Assert.Equal(expected: 1, actual: applied);
        Assert.Equal(expected: 0, actual: unapplied);

        Lay(w: bin, width: 900f, height: 300f);
        Assert.Equal(expected: 1, actual: unapplied);
    }

    [Theory]
    [InlineData(400f, 300f, true)] // 4:3
    [InlineData(300f, 400f, false)] // 3:4
    public void AspectRatioConditionsCompareWidthOverHeight(float w, float h, bool expected)
    {
        Assert.Equal(
            expected: expected,
            actual: AdwBreakpointCondition.MinAspectRatio(1f)
                .Evaluate(new Size(width: w, height: h))
        );
    }

    /// <summary>A zero-height box has no ratio; it must answer false, not divide by zero.</summary>
    [Fact]
    public void AspectRatioOnADegenerateBoxIsFalse()
    {
        Assert.False(
            AdwBreakpointCondition.MinAspectRatio(1f).Evaluate(new Size(width: 100f, height: 0f))
        );
        Assert.False(
            AdwBreakpointCondition.MaxAspectRatio(1f).Evaluate(new Size(width: 100f, height: 0f))
        );
    }

    [Fact]
    public void CombinedConditionsAndOr()
    {
        var both = AdwBreakpointCondition.MaxWidth(600f)
            .And(AdwBreakpointCondition.MinHeight(400f));
        Assert.True(both.Evaluate(new Size(width: 500f, height: 500f)));
        Assert.False(both.Evaluate(new Size(width: 500f, height: 300f)));

        var either = AdwBreakpointCondition.MaxWidth(600f)
            .Or(AdwBreakpointCondition.MinHeight(400f));
        Assert.True(either.Evaluate(new Size(width: 500f, height: 300f)));
        Assert.False(either.Evaluate(new Size(width: 900f, height: 300f)));
    }

    /// <summary>
    ///     The point of slots: the SAME child instance appears in whichever layout is showing, so
    ///     state living in that child survives the swap.
    /// </summary>
    [Fact]
    public void MultiLayoutViewRebindsOneChildBetweenArrangements()
    {
        var shared = new SizedBox(width: 5f, height: 5f);
        var view = new AdwMultiLayoutView {
            Children = { ["content"] = shared },
            Layouts = {
                new AdwLayout(
                    name: "wide",
                    content: new Row { Children = { new AdwLayoutSlot("content") } }
                ),
                new AdwLayout(name: "narrow", content: new AdwLayoutSlot("content")),
            },
        };

        Lay(w: view, width: 800f, height: 400f);
        Assert.Equal(expected: "wide", actual: view.CurrentLayout!.Name);

        view.LayoutName = "narrow";
        Lay(w: view, width: 400f, height: 400f);
        Assert.Equal(expected: "narrow", actual: view.CurrentLayout!.Name);

        // The narrow layout's slot now owns it, and the wide layout's slot has let go — otherwise
        // the child would be laid out (and painted) twice.
        var narrowSlot = (AdwLayoutSlot)view.Layouts[1].Content;
        var wideSlot = (AdwLayoutSlot)view.Layouts[0].Content.GetChildren().First();
        Assert.Contains(expected: shared, collection: narrowSlot.GetChildren());
        Assert.Empty(wideSlot.GetChildren());
    }

    [Fact]
    public void AnUnknownLayoutNameFallsBackToTheFirst()
    {
        var view = new AdwMultiLayoutView {
            Layouts = { new AdwLayout(name: "only", content: new SizedBox(width: 1f, height: 1f)) },
            LayoutName = "does-not-exist",
        };
        Assert.Equal(expected: "only", actual: view.CurrentLayout!.Name);
    }
}
