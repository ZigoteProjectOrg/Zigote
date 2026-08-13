using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Rendering;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     GPU memory + load. Shows tracked wgpu buffer/texture memory over time (populated by the native
///     accounting in <c>ZgEngineStats</c>), the buffer-vs-texture split, and the live per-frame pipeline
///     load (draw calls / triangles / passes / visible) that is the real GPU-work signal. Available in
///     both 2D and 3D apps — a 2D app still allocates GPU atlases + vertex buffers; its pipeline counters
///     just sit idle. Honest about gaps: unpopulated counters read "not instrumented", not a fake zero.
/// </summary>
public sealed class GpuPanel : IDevPanel
{
    private static readonly Color Blue = Color.Rgb(10, 132, 255);
    private static readonly Color Cyan = Color.Rgb(100, 210, 255);
    private static readonly Color Green = Color.Rgb(48, 209, 88);
    private static readonly Color Purple = Color.Rgb(191, 90, 242);

    private readonly DevKeyValue _backend = new("Backend");
    private readonly DevChartCard _memCard;
    private readonly DevMeter _buffers = new("Buffers", Blue);
    private readonly DevKeyValue _draws = new("Draw calls", valueColor: Blue);
    private readonly DevKeyValue _passes = new("Render passes", valueColor: Purple);
    private readonly DevKeyValue _surface = new("Surface");
    private readonly DevMeter _textures = new("Textures", Cyan);
    private readonly DevKeyValue _total = new("Tracked total");
    private readonly DevKeyValue _tris = new("Triangles", valueColor: Cyan);
    private readonly DevKeyValue _visible = new("Visible", valueColor: Green);
    private readonly DevNote _memNote = new("");

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tSurface = new();
    private readonly CachedText _tDraws = new();
    private readonly CachedText _tVisible = new();
    private readonly CachedText _tPasses = new();
    private RenderBackend? _backendKey;
    private string _backendText = "—";
    private ulong _bufKey = ulong.MaxValue;
    private string _bufText = "—";
    private ulong _texKey = ulong.MaxValue;
    private string _texText = "—";
    private ulong _totalKey = ulong.MaxValue;
    private string _totalText = "—";
    private long _trisKey = -1;
    private string _trisText = "—";

    public GpuPanel()
    {
        var mem = DevChart.Sparkline();
        var m = AreaMark.Of(DevChartData.GpuTotalMb, s => s.Time, s => s.Value);
        m.Name = "GPU MB";
        m.Color = Purple;
        m.Opacity = 0.22f;
        mem.Marks.Add(m);
        _memCard = new DevChartCard(
            mem,
            74f,
            120f,
            "GPU memory — 2 min"
        );
    }

    public string Title => "GPU";
    public DevCategory Category => DevCategory.Generic;

    public Widget Build(BuildContext context)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                new DevSectionHeader("Device"),
                _backend,
                _surface,
                new DevSectionHeader("Memory"),
                _memCard,
                _buffers,
                _textures,
                _total,
                _memNote,
                new DevSectionHeader("Pipeline load (per frame)"),
                _draws,
                _tris,
                _visible,
                _passes,
            },
        };
    }

    public void Refresh(float dt)
    {
        var t = App.Active?.Theme ?? ThemeData.Dark;
        _memCard.Sync(DevChartData.Revision, DevChartData.Time, t);

        var engine = ZigoteEngine.Instance;
        // Enum.ToString allocates each call, so re-run it only when the backend changed.
        var backendNow = engine?.Caps.ActiveBackend;
        if (backendNow != _backendKey)
        {
            _backendKey = backendNow;
            _backendText = backendNow?.ToString() ?? "—";
        }

        _backend.Value = _backendText;
        _surface.Value = engine is not null
            ? _tSurface.Update(
                $"{engine.LogicalWidth:F0}×{engine.LogicalHeight:F0} @{engine.Scale:0.#}x"
            )
            : "—";

        if (!DebugStats.EngineOk)
        {
            _total.Value = _draws.Value = _tris.Value = _visible.Value = _passes.Value = "n/a";
            _buffers.Value = _textures.Value = "n/a";
            _memNote.Text = "Renderer stats unavailable (headless).";
            _memNote.Color = t.Hint;
            return;
        }

        var s = DebugStats.Engine;
        var buf = s.GpuBufferMemory;
        var tex = s.GpuTextureMemory;
        var total = buf + tex;

        if (total == 0)
        {
            _buffers.Value = _textures.Value = "0";
            _buffers.Fraction = _textures.Fraction = 0f;
            _total.Value = "0";
            _memNote.Text = "GPU memory not instrumented on this build.";
            _memNote.Color = t.Hint;
        }
        else
        {
            // DevFormat.Bytes allocates, so re-run it only when the byte count changed.
            if (buf != _bufKey)
            {
                _bufKey = buf;
                _bufText = DevFormat.Bytes(buf);
            }

            if (tex != _texKey)
            {
                _texKey = tex;
                _texText = DevFormat.Bytes(tex);
            }

            if (total != _totalKey)
            {
                _totalKey = total;
                _totalText = DevFormat.Bytes(total);
            }

            _buffers.Value = _bufText;
            _buffers.Fraction = (float)((double)buf / total);
            _textures.Value = _texText;
            _textures.Fraction = (float)((double)tex / total);
            _total.Value = _totalText;
            _total.ValueColor = Purple;
            _memNote.Text = "Tracked: render targets · meshes · atlases · rings.";
            _memNote.Color = t.Hint;
        }

        _draws.Value = _tDraws.Update($"{s.DrawCalls}");
        // DevFormat.Count allocates, so re-run it only when the count changed.
        long trisNow = s.Triangles;
        if (trisNow != _trisKey)
        {
            _trisKey = trisNow;
            _trisText = DevFormat.Count(trisNow);
        }

        _tris.Value = _trisText;
        _visible.Value = _tVisible.Update($"{s.VisibleObjects}");
        _passes.Value = _tPasses.Update($"{s.RenderPasses}");
        if (!DevChartData.Rendering3D)
        {
            _draws.ValueColor = t.Hint;
            _passes.ValueColor = t.Hint;
        }
    }
}
