using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Guards the "fill widget inside an unbounded scroll" crash: a vertical <see cref="ScrollView" />
///     gives its child an infinite max-height, so a widget that naively returns <c>c.MaxHeight</c>
///     (TabView / ListView / Scaffold) reported an infinite size. That infinity then flowed into flex
///     alignment math (<c>∞ − ∞ → NaN</c>), producing NaN paint coordinates that crashed
///     <see cref="PaintList" />'s NaN guard. Fill widgets must size to their content on an unbounded
///     axis, and <c>FlexLayout</c> must never emit NaN even when a child is genuinely infinite.
/// </summary>
public class ScrollFillLayoutTests
{
    private static void LayoutInVerticalScroll(Widget child)
    {
        var scroll = new ScrollView(child);
        scroll.Measure(
            new Constraints(
                0,
                800,
                0,
                600
            )
        );
        scroll.Layout(Offset.Zero);
    }

    private static void AssertFinite(Rect b)
    {
        Assert.True(
            float.IsFinite(b.X) && float.IsFinite(b.Y) &&
            float.IsFinite(b.Width) && float.IsFinite(b.Height),
            $"non-finite bounds: {b}"
        );
    }

    private static void PaintNoThrow(Widget root)
    {
        var p = new PaintList();
        var ex = Record.Exception(() => root.Paint(p));
        Assert.Null(ex);
    }

    [Fact]
    public void TabView_InVerticalScroll_SizesToActiveChild()
    {
        var tabs = new TabView();
        tabs.Children.Add(new SizedBox(0, 100));

        // Unbounded height (as a vertical ScrollView supplies) → size to the active child, not ∞.
        var measured = tabs.Measure(new Constraints(0, 776));
        Assert.Equal(100f, measured.Height, 1);
        Assert.Equal(776f, measured.Width, 1);

        // Same shape that crashed in the gallery: a TabView inside a Card inside the scroll.
        var card = new Card(new Column([tabs]) { CrossAxisAlignment = CrossAxisAlignment.Stretch });
        var page = new Column([card]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        LayoutInVerticalScroll(page);

        AssertFinite(card.Bounds);
        AssertFinite(tabs.Bounds);
        Assert.Equal(100f, tabs.Bounds.Height, 1); // sized to its page, not to ∞
        PaintNoThrow(page);
    }

    [Fact]
    public void ListView_InVerticalScroll_SizesToContentHeight()
    {
        var list = new ListView { ItemHeight = 40f };
        list.SetItems(Enumerable.Range(0, 5).Select(_ => (Widget)new SizedBox(0, 40)));

        var page = new Column([list]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        LayoutInVerticalScroll(page);

        AssertFinite(list.Bounds);
        Assert.Equal(200f, list.Bounds.Height, 1); // 5 × 40, not ∞
        PaintNoThrow(page);
    }

    [Fact]
    public void Scaffold_InVerticalScroll_SizesToContent()
    {
        var scaffold = new Scaffold { Body = new SizedBox(0, 150) };

        var page = new Column([scaffold]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        LayoutInVerticalScroll(page);

        AssertFinite(scaffold.Bounds);
        Assert.Equal(150f, scaffold.Bounds.Height, 1); // body content, not ∞
        PaintNoThrow(page);
    }

    [Fact]
    public void ScrollView_WithInfiniteContentChild_DragNeverEmitsNaN()
    {
        // A child that fills the scroll's infinite max-height (the bug GraphInspectorPanel had) reported
        // an infinite content extent. Dragging the scrollbar then drove the offset to ∞, and the thumb
        // geometry computed ∞ / ∞ = NaN → a NaN paint coordinate that crashed PaintList. The ScrollView
        // must clamp a non-finite child extent so there is simply nothing to scroll instead.
        var scroll = new ScrollView(new InfiniteHeightChild()) { ScrollVertical = true };
        scroll.Measure(
            new Constraints(
                0,
                800,
                0,
                600
            )
        );
        scroll.Layout(Offset.Zero);

        // Content was infinite → clamped to the viewport, so there is no scroll extent.
        Assert.Equal(0f, scroll.OffsetY, 3);

        // Simulate a drag on the trailing-edge scrollbar strip (x ≥ Right − HitWidth), which previously
        // pushed the offset to ∞. After the fix the strip is inert (nothing to scroll) and stays finite.
        scroll.OnPointerDown(new Offset(795, 100));
        scroll.OnPointerMove(new Offset(795, 500));
        scroll.OnPointerUp(new Offset(795, 500));

        Assert.True(float.IsFinite(scroll.OffsetY), $"non-finite scroll offset: {scroll.OffsetY}");
        PaintNoThrow(scroll);
    }

    [Fact]
    public void NonStartFlex_WithInfiniteChild_NeverEmitsNaN()
    {
        // SizedBox.Expand legitimately reports ∞ on an unbounded axis. Placing it in a Center-aligned
        // flex previously produced (∞ − ∞)/2 = NaN offsets; the FlexLayout guard must keep paint alive.
        var row = new Row([SizedBox.Expand()]) { MainAxisAlignment = MainAxisAlignment.Start };
        var page = new Column([new SizedBox(0, 30), row]) {
            MainAxisAlignment = MainAxisAlignment.Center,
            CrossAxisAlignment = CrossAxisAlignment.Start,
        };
        LayoutInVerticalScroll(page);

        // The child may still be infinite (it asked to be), but no coordinate may be NaN.
        Assert.False(
            float.IsNaN(row.Bounds.X) || float.IsNaN(row.Bounds.Y) ||
            float.IsNaN(row.Bounds.Width) || float.IsNaN(row.Bounds.Height),
            $"NaN in bounds: {row.Bounds}"
        );
        PaintNoThrow(page);
    }

    [Fact]
    public void StretchRow_InVerticalScroll_SizesToContent()
    {
        // A Row with CrossAxisAlignment.Stretch inside a vertical scroll has an unbounded cross axis
        // (height). Before the FlexLayout guard, Stretch pinned the child's min-height to ∞, forcing an
        // infinite child size; now it degrades to a loose measure (natural content height) instead.
        var row = new Row([new SizedBox(100, 40)]) { CrossAxisAlignment = CrossAxisAlignment.Stretch };
        var page = new Column([row]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        LayoutInVerticalScroll(page);

        AssertFinite(row.Bounds);
        Assert.Equal(40f, row.Bounds.Height, 1); // content height, not ∞
        PaintNoThrow(page);
    }

    /// <summary>A widget that naively fills the constraint's max-height — ∞ inside a vertical scroll.</summary>
    private sealed class InfiniteHeightChild : Widget
    {
        private Size _s;

        public override Size Measure(Constraints c)
        {
            _s = new Size(float.IsFinite(c.MaxWidth) ? c.MaxWidth : 0f, c.MaxHeight);
            return _s;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                _s.Width,
                _s.Height
            );
        }

        public override void Paint(PaintList paint)
        {
        }

        public override IEnumerable<Widget> GetChildren()
        {
            return [];
        }
    }
}