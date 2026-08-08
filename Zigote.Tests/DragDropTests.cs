using Xunit;
using Zigote.Core;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.DragDrop;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Headless coverage of the in-app drag-and-drop widgets. The App-mediated routing (StartDrag /
///     feedback ghost) needs a live engine, but the acceptance/delivery contracts on
///     <see cref="DragTarget{T}" />, <see cref="Draggable{T}" />, and <see cref="DragData" /> are pure
///     widget logic and assert directly.
/// </summary>
public class DragDropTests
{
    private static DragTarget<string> StringTarget(Action<string> onAccept,
        Func<string, bool>? will = null)
    {
        return new DragTarget<string>(_ => new Label("drop here")) {
            OnAccept = onAccept,
            WillAccept = will,
        };
    }

    [Fact]
    public void DragData_ForPayload_SetsPayloadAndText()
    {
        var d = DragData.ForPayload("hello", "hello");
        Assert.Equal("hello", d.Payload);
        Assert.Equal("hello", d.Text);
        Assert.False(d.IsExternal);
        Assert.False(d.HasFiles);
    }

    [Fact]
    public void DragTarget_Accepts_MatchingPayload_Only()
    {
        var target = StringTarget(_ => { });
        Assert.True(target.CanAcceptDrop(DragData.ForPayload("a")));
        Assert.False(target.CanAcceptDrop(DragData.ForPayload(42)));
    }

    [Fact]
    public void DragTarget_WillAccept_Filters()
    {
        var target = StringTarget(_ => { }, s => s.StartsWith('y'));
        Assert.True(target.CanAcceptDrop(DragData.ForPayload("yes")));
        Assert.False(target.CanAcceptDrop(DragData.ForPayload("no")));
    }

    [Fact]
    public void DragTarget_OnDrop_DeliversPayload()
    {
        string? got = null;
        var target = StringTarget(s => got = s);
        target.OnDrop(DragData.ForPayload("payload"), Offset.Zero);
        Assert.Equal("payload", got);
    }

    [Fact]
    public void DragTarget_ExternalFiles_DeliverEachPath_WhenEnabled()
    {
        var got = new List<string>();
        var target = new DragTarget<string>(_ => new Label("files")) {
            OnAccept = got.Add,
            AcceptExternalFiles = true,
        };
        var data = new DragData {
            IsExternal = true,
            Files = ["/a.png", "/b.png"],
        };

        Assert.True(target.CanAcceptDrop(data));
        target.OnDrop(data, Offset.Zero);
        Assert.Equal(["/a.png", "/b.png"], got);
    }

    [Fact]
    public void DragTarget_ExternalFiles_Rejected_WhenDisabled()
    {
        var target = new DragTarget<string>(_ => new Label("x")) { AcceptExternalFiles = false };
        Assert.False(
            target.CanAcceptDrop(
                new DragData {
                    IsExternal = true,
                    Files = ["/a.png"],
                }
            )
        );
    }

    [Fact]
    public void DragTarget_Builder_ReceivesHoverState_OnEnterLeave()
    {
        var hoverStates = new List<bool>();
        var target = new DragTarget<string>(hover =>
            {
                hoverStates.Add(hover);
                return new Label(hover ? "over" : "idle");
            }
        );

        // Initial build (ctor) is idle.
        Assert.Equal([false], hoverStates);

        target.OnDragEnter(DragData.ForPayload("x"));
        target.OnDragLeave();
        Assert.Equal([false, true, false], hoverStates);
    }

    [Fact]
    public void Draggable_DelegatesLayoutToChild()
    {
        var child = new SizedBox(40f, 24f);
        var drag = new Draggable<string>("item", child);
        drag.Measure(
            new Constraints(
                0f,
                200f,
                0f,
                200f
            )
        );
        drag.Layout(new Offset(5f, 7f));

        Assert.Equal(40f, drag.Bounds.Width);
        Assert.Equal(24f, drag.Bounds.Height);
        Assert.Equal(5f, child.Bounds.X);
        Assert.Equal(7f, child.Bounds.Y);
    }

    [Fact]
    public void Draggable_HitTest_ReturnsSelf_ForGestureCapture()
    {
        var drag = new Draggable<string>("item", new SizedBox(40f, 24f));
        drag.Measure(
            new Constraints(
                0f,
                200f,
                0f,
                200f
            )
        );
        drag.Layout(Offset.Zero);
        Assert.Same(drag, drag.HitTest(new Offset(10f, 10f)));
        Assert.Null(drag.HitTest(new Offset(100f, 100f)));
    }

    [Fact]
    public void Draggable_BelowThreshold_DoesNotThrow_WithoutOwner()
    {
        var drag = new Draggable<string>("item", new SizedBox(40f, 24f));
        drag.Measure(
            new Constraints(
                0f,
                200f,
                0f,
                200f
            )
        );
        drag.Layout(Offset.Zero);
        drag.OnPointerDown(new Offset(10f, 10f));
        drag.OnPointerMove(new Offset(12f, 11f)); // < threshold, no Owner → no drag started
        drag.OnPointerUp(new Offset(12f, 11f));
    }
}