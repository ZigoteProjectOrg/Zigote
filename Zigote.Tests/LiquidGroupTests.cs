using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Zigote.Core;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="LiquidGroup" /> is nothing but paint order: every pane's lens before any pane's
///     content, so the engine sees one run of glass and captures the backdrop once. Left alone,
///     panes interleave lens and content — one capture each.
/// </summary>
public class LiquidGroupTests
{
    private static LiquidPane Pane() => new() {
        Elevation = 4f,
        Child = new DecoratedBox {
            Fill = Color.White,
            Child = new SizedBox(width: 40f, height: 20f),
        },
    };

    /// <summary>The shadow, glass and fill ops in paint order, by name — nothing else is emitted.</summary>
    private static string[] Ops(Widget root)
    {
        root.Measure(Constraints.Loose(width: 200, height: 100));
        root.Layout(Offset.Zero);
        var paint = new PaintList();
        root.Paint(paint);
        return Enumerable.Range(start: 0, count: paint.RecordCount)
            .Select(i => paint.RecordKind(i).ToString())
            .Where(kind => kind is "Shadow" or "LiquidGlass" or "Rect")
            .ToArray();
    }

    [Fact]
    public void GroupedPanesLayEveryLensBeforeAnyContent()
    {
        var group = new LiquidGroup(new Row(spacing: 8f) { Children = { Pane(), Pane() } });
        string[] ops = Ops(group);
        Assert.Equal(expected: 2, actual: group.PaneCount);
        // Every shadow, then every lens, then the content: one run of glass, one capture.
        Assert.Equal(
            expected: ["Shadow", "Shadow", "LiquidGlass", "LiquidGlass", "Rect", "Rect"],
            actual: ops
        );
    }

    [Fact]
    public void LonePanesInterleaveLensAndContent()
    {
        string[] ops = Ops(new Row(spacing: 8f) { Children = { Pane(), Pane() } });
        Assert.Equal(
            expected: ["Shadow", "LiquidGlass", "Rect", "Shadow", "LiquidGlass", "Rect"],
            actual: ops
        );
    }

    /// <summary>
    ///     The wrap/unwrap build pattern re-parents retained content without detaching it: no
    ///     unmount fires below the swapped wrapper, and the measure cache above the pane stays
    ///     valid. A pane that only resolved its group on measure kept pointing at the detached
    ///     group after an unwrap and painted no glass at all; resolving on layout — never cached —
    ///     is what makes the pane follow in both directions.
    /// </summary>
    [Fact]
    public void PaneFollowsAWrapOrUnwrapOfItsGroupAroundRetainedContent()
    {
        var content = new Retained(Pane());
        var toggle = new Toggle(content) { Wrap = true };
        toggle.Attach(owner: FakeOwner, parent: null);

        Assert.Equal(expected: ["Shadow", "LiquidGlass", "Rect"], actual: Ops(toggle));
        Assert.Equal(expected: 1, actual: toggle.Group!.PaneCount);

        // Unwrap: the group leaves the tree, the content and its pane stay.
        toggle.Wrap = false;
        toggle.Invalidate();
        Assert.Equal(expected: ["Shadow", "LiquidGlass", "Rect"], actual: Ops(toggle));

        // Wrap again with a fresh group: the pane joins it, so the lens is the group's to draw.
        toggle.Wrap = true;
        toggle.Invalidate();
        Assert.Equal(expected: ["Shadow", "LiquidGlass", "Rect"], actual: Ops(toggle));
        Assert.Equal(expected: 1, actual: toggle.Group!.PaneCount);
    }

    // A real App needs a window and a GPU. Attach only touches the repaint tracker, so an
    // uninitialized instance with that one field filled in stands in for one headlessly.
    private static readonly App FakeOwner = FakeApp();

    private static App FakeApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App)
            .GetField(
                name: "_repaint",
                bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic
            )!
            .SetValue(obj: app, value: new RepaintTracker());
        return app;
    }

    /// <summary>Retained content: built once, then handed to whichever wrapper the toggle picks.</summary>
    private sealed class Retained(Widget child) : ComposedWidget
    {
        protected override Widget Build(BuildContext context) => child;
    }

    /// <summary>A build that returns its content bare one frame and wrapped in a group the next.</summary>
    private sealed class Toggle(Widget content) : ComposedWidget
    {
        public bool Wrap { get; set; }
        public LiquidGroup? Group { get; private set; }

        protected override Widget Build(BuildContext context)
        {
            Group = Wrap ? new LiquidGroup(content) : null;
            return (Widget?)Group ?? content;
        }
    }

    /// <summary>
    ///     The group is invisible glue: a miss inside its bounds falls through to whatever is
    ///     stacked beneath. A group whose child blankets a window — bars in Positioned corners
    ///     of a full-size Stack — used to answer <c>this</c> for every off-bar point, eating the
    ///     clicks (and, with them, the wheel) over the very content its bars float above.
    /// </summary>
    [Fact]
    public void GroupPassesMissesThrough()
    {
        var group = new LiquidGroup(
            new SizedBox(
                width: 200f,
                height: 100f,
                child: new Stack {
                    Children = { new Positioned(left: 0f, top: 0f, child: Pane()) },
                }
            )
        );
        Ops(group);
        Assert.NotNull(group.HitTest(new Offset(x: 5f, y: 5f)));
        Assert.Null(group.HitTest(new Offset(x: 150f, y: 90f)));
    }

    [Fact]
    public void PaneMeasuredOutsideItsGroupLeavesIt()
    {
        var pane = Pane();
        var group = new LiquidGroup(pane);
        Ops(group);
        Assert.Equal(expected: 1, actual: group.PaneCount);

        // Re-parented into a plain row and measured there: the pane paints its own lens again.
        group.Child = null;
        string[] ops = Ops(new Row { Children = { pane } });
        Assert.Equal(expected: 0, actual: group.PaneCount);
        Assert.Equal(expected: ["Shadow", "LiquidGlass", "Rect"], actual: ops);
    }
}

/// <summary>
///     The clear-to-tinted dial: 0 must be the clear variant exactly — the white haze at its floor
///     — whatever the tint, and 1 the pane's own tint at its own strength. A dark tint faded to
///     nothing used to leave a faintly dark lens at the clear end.
/// </summary>
public class LiquidPaneTests
{
    private static PaintCommandView Lens(LiquidPane pane)
    {
        pane.Measure(Constraints.Loose(width: 200, height: 100));
        pane.Layout(Offset.Zero);
        var paint = new PaintList();
        pane.Paint(paint);
        return paint.DebugCommands.Single(c => c.Kind == ZgPaintOp.LiquidGlass);
    }

    [Theory]
    [InlineData(0f, 1f, 1f, 1f, 0.10f)] // clear: white at the floor
    [InlineData(1f, 0.1f, 0.2f, 0.3f, 0.9f)] // tinted: the tint itself
    [InlineData(0.5f, 0.55f, 0.6f, 0.65f, 0.5f)] // halfway in colour and strength alike
    public void TintedRunsFromWhiteHazeToTheTint(float tinted, float r, float g, float b, float a)
    {
        var lens = Lens(
            new LiquidPane {
                Elevation = 0f,
                Tint = new Color(r: 0.1f, g: 0.2f, b: 0.3f, a: 0.9f),
                Tinted = tinted,
                Child = new SizedBox(width: 40f, height: 20f),
            }
        );
        Assert.Equal(expected: r, actual: lens.ColorR, precision: 3);
        Assert.Equal(expected: g, actual: lens.ColorG, precision: 3);
        Assert.Equal(expected: b, actual: lens.ColorB, precision: 3);
        Assert.Equal(expected: a, actual: lens.ColorA, precision: 3);
    }

    [Fact]
    public void BevelIsNeverMoreThanAFifthOfTheShorterSide()
    {
        // A tall capsule: the material's own rim.
        var capsule = Lens(new LiquidPane { Elevation = 0f, Child = new SizedBox(width: 120f, height: 56f) });
        Assert.InRange(actual: capsule.BorderWidth, low: 4f, high: 9f);

        // A chip: capped by the shape, so the centre stays clear.
        var chip = Lens(new LiquidPane { Elevation = 0f, Child = new SizedBox(width: 60f, height: 16f) });
        Assert.InRange(actual: chip.BorderWidth, low: 1f, high: 3.2f);
    }

    [Fact]
    public void DefaultTintIsTheThemeHazeAtOneAndTheClearFloorAtZero()
    {
        // No theme provider in scope: the dark default, whose haze is white at 0.14.
        var regular = Lens(new LiquidPane { Elevation = 0f, Child = new SizedBox(width: 40f, height: 20f) });
        Assert.Equal(expected: 0.14f, actual: regular.ColorA, precision: 3);
        Assert.Equal(expected: 1f, actual: regular.ColorR, precision: 3);

        var clear = Lens(LiquidPane.Clear(new SizedBox(width: 40f, height: 20f)));
        Assert.Equal(expected: 0.10f, actual: clear.ColorA, precision: 3);
        Assert.Equal(expected: 1f, actual: clear.ColorR, precision: 3);
    }
}
