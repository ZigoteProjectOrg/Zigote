using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Debug;
using Zigote.UI.DevTools;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Panels;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Headless coverage of the widget/chart devtools overlay: every App-independent panel is built,
///     refreshed and painted into a real <see cref="PaintList" /> (whose NaN validation is the
///     canary),
///     plus the profile gating, formatting, and diagnostics rings. Panels that need a live
///     <c>App</c>/engine (UI Inspector, Semantics, Renderer stats) are exercised at runtime instead.
/// </summary>
public class DevToolsTests
{
    private static IEnumerable<IDevPanel> AppIndependentPanels()
    {
        yield return new OverviewPanel();
        yield return new PerformancePanel();
        yield return new MemoryPanel();
        yield return new GpuPanel();
        yield return new LogsPanel();
        yield return new ConsolePanel();
        yield return new VariablesPanel();
        yield return new ReactivePanel();
        yield return new UiPaintPanel();
        yield return new PipelinePanel();
        yield return new RendererPanel();
    }

    [Fact]
    public void EveryPanel_BuildRefreshPaint_DoesNotThrow()
    {
        DevChartData.Install();
        for (int i = 0; i < 5; i++) DebugStats.Sample(0.016f);

        foreach (var panel in AppIndependentPanels())
        {
            var root = new ThemeProvider(
                data: ThemeData.Dark,
                child: DevPage.Group(panel.Build(BuildContext.Current))
            );
            Measure(root);
            root.Paint(new PaintList());

            // Two refresh cycles: the first populates live labels, the second exercises the
            // change-gated dynamic-list rebuild paths (logs / scopes / variables).
            panel.Refresh(0.016f);
            Measure(root);
            root.Paint(new PaintList());
            panel.Refresh(0.016f);
            Measure(root);
            root.Paint(new PaintList());
        }
    }

    private static void Measure(Widget w)
    {
        w.Measure(Constraints.Tight(width: DevToolsPanel.PanelWidth - 24f, height: 1400f));
        w.Layout(Offset.Zero);
    }

    /// <summary>
    ///     Every panel also has to survive a phone: same build/refresh/paint cycle under a 390pt
    ///     <see cref="MediaQuery" />, where the kit switches to finger-sized rows and the panel strip to
    ///     its scrollable arm.
    /// </summary>
    [Fact]
    public void EveryPanel_BuildRefreshPaint_AtPhoneWidth_DoesNotThrow()
    {
        DevChartData.Install();
        for (int i = 0; i < 5; i++) DebugStats.Sample(0.016f);

        foreach (var panel in AppIndependentPanels())
        {
            var root = new MediaQuery(
                data: new MediaQueryData(width: 390f, height: 780f),
                child: new ThemeProvider(
                    data: ThemeData.Dark,
                    child: DevPage.Group(panel.Build(BuildContext.Current))
                )
            );
            root.Measure(Constraints.Tight(width: 390f, height: 780f));
            root.Layout(Offset.Zero);
            root.Paint(new PaintList());

            panel.Refresh(0.016f);
            root.Measure(Constraints.Tight(width: 390f, height: 780f));
            root.Layout(Offset.Zero);
            root.Paint(new PaintList());
        }
    }

    /// <summary>
    ///     Adjacent readout rows collapse into one boxed list; everything else stays between the cards
    ///     in source order. This is what gives every panel its grouped layout without panels knowing.
    /// </summary>
    [Fact]
    public void DevPage_GroupsAdjacentRowsAndKeepsTheRest()
    {
        var note = new DevNote("note");
        var header = new DevSectionHeader("Section");
        var source = new Column {
            Children = {
                header,
                new DevKeyValue("a"),
                new DevKeyValue("b"),
                note,
                new DevKeyValue("c"),
            },
        };

        var grouped = Assert.IsType<Column>(DevPage.Group(source));
        // header, [a b] card, note, [c] card
        Assert.Equal(expected: 4, actual: grouped.Children.Count);
        Assert.Same(expected: header, actual: grouped.Children[0]);
        Assert.Same(expected: note, actual: grouped.Children[2]);
        Assert.Equal(expected: 2, actual: GroupRows(grouped.Children[1]));
        Assert.Equal(expected: 1, actual: GroupRows(grouped.Children[3]));

        static int GroupRows(Widget w)
        {
            var padding = Assert.IsType<Padding>(w);
            return Assert.IsType<AdwPreferencesGroup>(padding.Child).Rows.Count;
        }
    }

    /// <summary>A readout row is dense on a pointer screen and a 44pt touch target on a phone.</summary>
    [Theory]
    [InlineData(1200f, DevKit.RowHeight)]
    [InlineData(390f, ControlMetrics.MinTouchTarget)]
    public void KeyValueRow_HeightFollowsSizeClass(float screenWidth, float expected)
    {
        var root = new MediaQuery(
            data: new MediaQueryData(width: screenWidth, height: 800f),
            child: new ThemeProvider(
                data: ThemeData.Dark,
                child: new DevKeyValue(key: "key", value: "value")
            )
        );
        var size = root.Measure(
            new Constraints(
                minWidth: 0f,
                maxWidth: 300f,
                minHeight: 0f,
                maxHeight: 800f
            )
        );
        Assert.Equal(expected: expected, actual: size.Height);
    }

    // ── Profile gating ──

    [Fact]
    public void TwoDProfile_HidesRender3D() => Assert.False(DevToolsProfile.TwoD.ShowsRender3D());

    [Fact]
    public void ThreeDProfile_ShowsRender3D() =>
        Assert.True(DevToolsProfile.ThreeD.ShowsRender3D());

    [Fact]
    public void Categories_HaveLabels()
    {
        Assert.Equal(expected: "General", actual: DevCategory.Generic.Label());
        Assert.Equal(expected: "2D · UI", actual: DevCategory.Ui2D.Label());
        Assert.Equal(expected: "3D · Render", actual: DevCategory.Render3D.Label());
    }

    // ── Formatting ──

    [Theory]
    [InlineData(500L, "500")]
    [InlineData(12_345L, "12.3K")]
    [InlineData(4_500_000L, "4.5M")]
    public void DevFormat_Count_Abbreviates(long n, string expected) => Assert.Equal(
        expected: expected,
        actual: DevFormat.Count(n)
    );

    [Theory]
    [InlineData(512UL, "512 B")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(5UL * 1024 * 1024, "5.0 MB")]
    public void DevFormat_Bytes_Scales(ulong bytes, string expected) => Assert.Equal(
        expected: expected,
        actual: DevFormat.Bytes(bytes)
    );

    [Fact]
    public void DevFormat_Uptime_Formats()
    {
        Assert.Equal(expected: "1h 2m 5s", actual: DevFormat.Uptime(3725f));
        Assert.Equal(expected: "45s", actual: DevFormat.Uptime(45f));
    }

    // ── Diagnostics rings ──

    [Fact]
    public void TimeSeriesRing_IsChronologicalAndBounded()
    {
        var ring = new TimeSeriesRing(3);
        ring.Push(time: 0f, value: 1f);
        ring.Push(time: 1f, value: 2f);
        ring.Push(time: 2f, value: 3f);
        ring.Push(time: 3f, value: 4f); // evicts the oldest
        Assert.Equal(expected: 3, actual: ring.Count);
        Assert.Equal(expected: 2f, actual: ring[0].Value); // oldest survivor
        Assert.Equal(expected: 4f, actual: ring.Latest.Value);
        Assert.Equal(expected: 4f, actual: ring.Max());
    }

    [Fact]
    public void TimeSeriesRing_SanitizesNonFinite()
    {
        var ring = new TimeSeriesRing(2);
        ring.Push(time: 0f, value: float.NaN);
        ring.Push(time: 1f, value: float.PositiveInfinity);
        Assert.Equal(expected: 0f, actual: ring[0].Value);
        Assert.Equal(expected: 0f, actual: ring[1].Value);
    }

    [Fact]
    public void DevChartData_Sample_AdvancesRevisionAndRings()
    {
        DevChartData.Install();
        int beforeRev = DevChartData.Revision;
        // The fast ring pushes at a 0.25 s cadence; a single 0.3 s frame guarantees a wave.
        DebugStats.Sample(0.3f);
        Assert.True(DevChartData.Revision > beforeRev);
        Assert.True(DevChartData.Fps.Count > 0);
    }

    [Fact]
    public void DebugDraws_StayActive_WhilePanelsAreClosed()
    {
        var c = new DevToolsController(app: null!, profile: DevToolsProfile.TwoD);
        Assert.False(c.DebugDrawActive);
        Assert.False(c.WantsContinuousFrame);

        c.ShowLayoutBounds = true;

        // Enabled draw, no panel open: still paints, still pumps frames — but never swallows clicks.
        Assert.True(c.DebugDrawActive);
        Assert.True(c.WantsContinuousFrame);
        Assert.False(c.PanelsMounted);
    }
}
