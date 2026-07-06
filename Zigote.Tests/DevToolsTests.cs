using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Debug;
using Zigote.UI.DevTools;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Panels;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Host;

namespace Zigote.Tests;

/// <summary>
///     Headless coverage of the widget/chart devtools overlay: every App-independent panel is built,
///     refreshed and painted into a real <see cref="PaintList" /> (whose NaN validation is the canary),
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
        yield return new UiPaintPanel();
        yield return new PipelinePanel();
        yield return new RendererPanel();
    }

    [Fact]
    public void EveryPanel_BuildRefreshPaint_DoesNotThrow()
    {
        DevChartData.Install();
        for (var i = 0; i < 5; i++) DebugStats.Sample(0.016f);

        foreach (var panel in AppIndependentPanels())
        {
            var root = new ThemeProvider(ThemeData.Dark, panel.Build(BuildContext.Current));
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
        w.Measure(Constraints.Tight(DevToolsPanel.PanelWidth - 24f, 1400f));
        w.Layout(Offset.Zero);
    }

    // ── Profile gating ──

    [Fact]
    public void TwoDProfile_HidesRender3D()
    {
        Assert.False(DevToolsProfile.TwoD.ShowsRender3D());
    }

    [Fact]
    public void ThreeDProfile_ShowsRender3D()
    {
        Assert.True(DevToolsProfile.ThreeD.ShowsRender3D());
    }

    [Fact]
    public void Categories_HaveLabels()
    {
        Assert.Equal("General", DevCategory.Generic.Label());
        Assert.Equal("2D · UI", DevCategory.Ui2D.Label());
        Assert.Equal("3D · Render", DevCategory.Render3D.Label());
    }

    // ── Formatting ──

    [Theory]
    [InlineData(500L, "500")]
    [InlineData(12_345L, "12.3K")]
    [InlineData(4_500_000L, "4.5M")]
    public void DevFormat_Count_Abbreviates(long n, string expected)
    {
        Assert.Equal(expected, DevFormat.Count(n));
    }

    [Theory]
    [InlineData(512UL, "512 B")]
    [InlineData(1536UL, "1.5 KB")]
    [InlineData(5UL * 1024 * 1024, "5.0 MB")]
    public void DevFormat_Bytes_Scales(ulong bytes, string expected)
    {
        Assert.Equal(expected, DevFormat.Bytes(bytes));
    }

    [Fact]
    public void DevFormat_Uptime_Formats()
    {
        Assert.Equal("1h 2m 5s", DevFormat.Uptime(3725f));
        Assert.Equal("45s", DevFormat.Uptime(45f));
    }

    // ── Diagnostics rings ──

    [Fact]
    public void TimeSeriesRing_IsChronologicalAndBounded()
    {
        var ring = new TimeSeriesRing(3);
        ring.Push(0f, 1f);
        ring.Push(1f, 2f);
        ring.Push(2f, 3f);
        ring.Push(3f, 4f); // evicts the oldest
        Assert.Equal(3, ring.Count);
        Assert.Equal(2f, ring[0].Value); // oldest survivor
        Assert.Equal(4f, ring.Latest.Value);
        Assert.Equal(4f, ring.Max());
    }

    [Fact]
    public void TimeSeriesRing_SanitizesNonFinite()
    {
        var ring = new TimeSeriesRing(2);
        ring.Push(0f, float.NaN);
        ring.Push(1f, float.PositiveInfinity);
        Assert.Equal(0f, ring[0].Value);
        Assert.Equal(0f, ring[1].Value);
    }

    [Fact]
    public void DevChartData_Sample_AdvancesRevisionAndRings()
    {
        DevChartData.Install();
        var beforeRev = DevChartData.Revision;
        // The fast ring pushes at a 0.25 s cadence; a single 0.3 s frame guarantees a wave.
        DebugStats.Sample(0.3f);
        Assert.True(DevChartData.Revision > beforeRev);
        Assert.True(DevChartData.Fps.Count > 0);
    }
}