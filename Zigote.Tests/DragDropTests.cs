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
        var d = DragData.ForPayload(payload: "hello", text: "hello");
        Assert.Equal(expected: "hello", actual: d.Payload);
        Assert.Equal(expected: "hello", actual: d.Text);
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
        var target = StringTarget(onAccept: _ => { }, will: s => s.StartsWith('y'));
        Assert.True(target.CanAcceptDrop(DragData.ForPayload("yes")));
        Assert.False(target.CanAcceptDrop(DragData.ForPayload("no")));
    }

    [Fact]
    public void DragTarget_OnDrop_DeliversPayload()
    {
        string? got = null;
        var target = StringTarget(s => got = s);
        target.OnDrop(data: DragData.ForPayload("payload"), point: Offset.Zero);
        Assert.Equal(expected: "payload", actual: got);
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
        target.OnDrop(data: data, point: Offset.Zero);
        Assert.Equal(expected: ["/a.png", "/b.png"], actual: got);
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
        Assert.Equal(expected: [false], actual: hoverStates);

        target.OnDragEnter(DragData.ForPayload("x"));
        target.OnDragLeave();
        Assert.Equal(expected: [false, true, false], actual: hoverStates);
    }

    [Fact]
    public void Draggable_DelegatesLayoutToChild()
    {
        var child = new SizedBox(width: 40f, height: 24f);
        var drag = new Draggable<string>(data: "item", child: child);
        drag.Measure(
            new Constraints(
                minWidth: 0f,
                maxWidth: 200f,
                minHeight: 0f,
                maxHeight: 200f
            )
        );
        drag.Layout(new Offset(x: 5f, y: 7f));

        Assert.Equal(expected: 40f, actual: drag.Bounds.Width);
        Assert.Equal(expected: 24f, actual: drag.Bounds.Height);
        Assert.Equal(expected: 5f, actual: child.Bounds.X);
        Assert.Equal(expected: 7f, actual: child.Bounds.Y);
    }

    [Fact]
    public void Draggable_HitTest_ReturnsSelf_ForGestureCapture()
    {
        var drag = new Draggable<string>(
            data: "item",
            child: new SizedBox(width: 40f, height: 24f)
        );
        drag.Measure(
            new Constraints(
                minWidth: 0f,
                maxWidth: 200f,
                minHeight: 0f,
                maxHeight: 200f
            )
        );
        drag.Layout(Offset.Zero);
        Assert.Same(expected: drag, actual: drag.HitTest(new Offset(x: 10f, y: 10f)));
        Assert.Null(drag.HitTest(new Offset(x: 100f, y: 100f)));
    }

    [Fact]
    public void Draggable_BelowThreshold_DoesNotThrow_WithoutOwner()
    {
        var drag = new Draggable<string>(
            data: "item",
            child: new SizedBox(width: 40f, height: 24f)
        );
        drag.Measure(
            new Constraints(
                minWidth: 0f,
                maxWidth: 200f,
                minHeight: 0f,
                maxHeight: 200f
            )
        );
        drag.Layout(Offset.Zero);
        drag.OnPointerDown(new Offset(x: 10f, y: 10f));
        drag.OnPointerMove(new Offset(x: 12f, y: 11f)); // < threshold, no Owner → no drag started
        drag.OnPointerUp(new Offset(x: 12f, y: 11f));
    }
}
