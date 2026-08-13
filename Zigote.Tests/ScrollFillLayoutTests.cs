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
                minWidth: 0,
                maxWidth: 800,
                minHeight: 0,
                maxHeight: 600
            )
        );
        scroll.Layout(Offset.Zero);
    }

    private static void AssertFinite(Rect b)
    {
        Assert.True(
            condition: float.IsFinite(b.X) && float.IsFinite(b.Y) &&
                       float.IsFinite(b.Width) && float.IsFinite(b.Height),
            userMessage: $"non-finite bounds: {b}"
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
        tabs.Children.Add(new SizedBox(width: 0, height: 100));

        // Unbounded height (as a vertical ScrollView supplies) → size to the active child, not ∞.
        var measured = tabs.Measure(new Constraints(minWidth: 0, maxWidth: 776));
        Assert.Equal(expected: 100f, actual: measured.Height, precision: 1);
        Assert.Equal(expected: 776f, actual: measured.Width, precision: 1);

        // Same shape that crashed in the gallery: a TabView inside a Card inside the scroll.
        var card = new Card(new Column([tabs]) { CrossAxisAlignment = CrossAxisAlignment.Stretch });
        var page = new Column([card]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        LayoutInVerticalScroll(page);

        AssertFinite(card.Bounds);
        AssertFinite(tabs.Bounds);
        Assert.Equal(
            expected: 100f,
            actual: tabs.Bounds.Height,
            precision: 1
        ); // sized to its page, not to ∞
        PaintNoThrow(page);
    }

    [Fact]
    public void ListView_InVerticalScroll_SizesToContentHeight()
    {
        var list = new ListView { ItemHeight = 40f };
        list.SetItems(
            Enumerable.Range(start: 0, count: 5)
                .Select(_ => (Widget)new SizedBox(width: 0, height: 40))
        );

        var page = new Column([list]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        LayoutInVerticalScroll(page);

        AssertFinite(list.Bounds);
        Assert.Equal(expected: 200f, actual: list.Bounds.Height, precision: 1); // 5 × 40, not ∞
        PaintNoThrow(page);
    }

    [Fact]
    public void Scaffold_InVerticalScroll_SizesToContent()
    {
        var scaffold = new Scaffold { Body = new SizedBox(width: 0, height: 150) };

        var page = new Column([scaffold]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        LayoutInVerticalScroll(page);

        AssertFinite(scaffold.Bounds);
        Assert.Equal(
            expected: 150f,
            actual: scaffold.Bounds.Height,
            precision: 1
        ); // body content, not ∞
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
                minWidth: 0,
                maxWidth: 800,
                minHeight: 0,
                maxHeight: 600
            )
        );
        scroll.Layout(Offset.Zero);

        // Content was infinite → clamped to the viewport, so there is no scroll extent.
        Assert.Equal(expected: 0f, actual: scroll.OffsetY, precision: 3);

        // Simulate a drag on the trailing-edge scrollbar strip (x ≥ Right − HitWidth), which previously
        // pushed the offset to ∞. After the fix the strip is inert (nothing to scroll) and stays finite.
        scroll.OnPointerDown(new Offset(x: 795, y: 100));
        scroll.OnPointerMove(new Offset(x: 795, y: 500));
        scroll.OnPointerUp(new Offset(x: 795, y: 500));

        Assert.True(
            condition: float.IsFinite(scroll.OffsetY),
            userMessage: $"non-finite scroll offset: {scroll.OffsetY}"
        );
        PaintNoThrow(scroll);
    }

    [Fact]
    public void NonStartFlex_WithInfiniteChild_NeverEmitsNaN()
    {
        // SizedBox.Expand legitimately reports ∞ on an unbounded axis. Placing it in a Center-aligned
        // flex previously produced (∞ − ∞)/2 = NaN offsets; the FlexLayout guard must keep paint alive.
        var row = new Row([SizedBox.Expand()]) { MainAxisAlignment = MainAxisAlignment.Start };
        var page = new Column([new SizedBox(width: 0, height: 30), row]) {
            MainAxisAlignment = MainAxisAlignment.Center,
            CrossAxisAlignment = CrossAxisAlignment.Start,
        };
        LayoutInVerticalScroll(page);

        // The child may still be infinite (it asked to be), but no coordinate may be NaN.
        Assert.False(
            condition: float.IsNaN(row.Bounds.X) || float.IsNaN(row.Bounds.Y) ||
                       float.IsNaN(row.Bounds.Width) || float.IsNaN(row.Bounds.Height),
            userMessage: $"NaN in bounds: {row.Bounds}"
        );
        PaintNoThrow(page);
    }

    [Fact]
    public void StretchRow_InVerticalScroll_SizesToContent()
    {
        // A Row with CrossAxisAlignment.Stretch inside a vertical scroll has an unbounded cross axis
        // (height). Before the FlexLayout guard, Stretch pinned the child's min-height to ∞, forcing an
        // infinite child size; now it degrades to a loose measure (natural content height) instead.
        var row = new Row([new SizedBox(width: 100, height: 40)]) {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
        };
        var page = new Column([row]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        LayoutInVerticalScroll(page);

        AssertFinite(row.Bounds);
        Assert.Equal(
            expected: 40f,
            actual: row.Bounds.Height,
            precision: 1
        ); // content height, not ∞
        PaintNoThrow(page);
    }

    /// <summary>A widget that naively fills the constraint's max-height — ∞ inside a vertical scroll.</summary>
    private sealed class InfiniteHeightChild : Widget
    {
        private Size _s;

        public override Size Measure(Constraints c)
        {
            _s = new Size(width: float.IsFinite(c.MaxWidth) ? c.MaxWidth : 0f, height: c.MaxHeight);
            return _s;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _s.Width,
                height: _s.Height
            );
        }

        public override void Paint(PaintList paint) { }

        public override IEnumerable<Widget> GetChildren() => [];
    }
}
