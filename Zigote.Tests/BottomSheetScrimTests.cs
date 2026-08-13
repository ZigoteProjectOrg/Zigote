using Xunit;
using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.BottomSheets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     A modal sheet leaves a strip of scrim above itself, and a click on that strip closes it.
///     This pins the hit path end to end — the strip must reach the sheet's scrim and not the content
///     behind it.
/// </summary>
// The sheet drives its position through a Signal, so it shares the reactive graph the
// serialized reactive tests assert global counters on.
[Collection("Reactive-serial")]
public class BottomSheetScrimTests
{
    private static AdwBottomSheet Laid(out int closes, float w = 400f, float h = 600f)
    {
        var count = 0;
        var sheet = new AdwBottomSheet(
            new SizedBox(w, h), // content behind, claims nothing
            new Container { Height = 200f }
        ) {
            Modal = true,
            ShowDragHandle = true,
        };
        sheet.OnOpenChanged = open =>
        {
            if (!open) count++;
        };
        sheet.Open = true; // unattached: snaps open, no animation
        sheet.Measure(Constraints.Tight(w, h));
        sheet.Layout(Offset.Zero);
        closes = count;
        return sheet;
    }

    [Fact]
    public void TopStrip_HitsTheScrim_AndClosesOnTap()
    {
        var sheet = Laid(out _);

        var hit = sheet.HitTest(new Offset(200f, 4f)); // inside the strip the sheet never covers
        var scrim = Assert.IsType<FlexibleBottomSheet>(hit);

        scrim.OnPointerUp(new Offset(200f, 4f));
        Assert.False(sheet.Open);
    }

    [Fact]
    public void InsideTheSheet_DoesNotClose()
    {
        var sheet = Laid(out _);

        var hit = sheet.HitTest(new Offset(200f, 580f)); // low in the card
        Assert.IsNotType<FlexibleBottomSheet>(hit);
        Assert.True(sheet.Open);
    }

    /// <summary>
    ///     The sheet layer is mounted even while closed (so dragging the bottom bar can pull it out),
    ///     which makes it a full-bleed widget sitting over the page: it must be completely transparent
    ///     to hit-testing while parked, wrappers included. Wrapping it in a <c>Positioned.Fill</c>
    ///     once broke exactly this — a <c>Positioned</c> answers a child miss with itself, and the
    ///     whole app stopped taking clicks.
    /// </summary>
    [Fact]
    public void ClosedSheet_LetsClicksThroughToTheContent()
    {
        var content = new Container { Height = 600f };
        var sheet = new AdwBottomSheet(content, new Container { Height = 200f }) { Modal = true };

        sheet.Open = true; // unattached: snaps open…
        sheet.Measure(Constraints.Tight(400f, 600f));
        sheet.Layout(Offset.Zero);
        sheet.Open = false; // …and shut again
        sheet.Measure(Constraints.Tight(400f, 600f));
        sheet.Layout(Offset.Zero);

        Assert.Same(content, sheet.HitTest(new Offset(200f, 300f)));
    }
}
