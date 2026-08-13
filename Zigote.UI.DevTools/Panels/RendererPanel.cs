using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Native;
using Zigote.Core.Rendering;
using Zigote.UI.Debug;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     Live renderer controls (3D · Render): backend/surface + frame counters, the 16-channel G-buffer
///     debug-view selector, feature toggles (bloom / SSAO / SSR / shadows / TAA / DoF / wireframe /
///     diagnostic), and exposure/ambient tuning — every write goes straight through
///     <c>SetRenderSettings3D</c>. Toggling an "amount" effect off zeros its field and remembers the
///     last
///     value so toggling it back on restores the previous strength.
/// </summary>
public sealed class RendererPanel : IDevPanel
{
    private readonly DevKeyValue _backend = new("Backend");
    private readonly DevKeyValue _counters = new("Draws / tris / passes");
    private readonly Dictionary<string, float> _remembered = new();
    private readonly DevKeyValue _surface = new("Surface");
    private readonly CachedText _tAmbient = new();
    private readonly CachedText _tCounters = new();
    private readonly CachedText _tExposure = new();

    // Per-readout caches: Refresh runs every frame while the panel is open, so all formatting goes
    // through CachedText (zero-alloc while the rendered text is unchanged).
    private readonly CachedText _tSurface = new();
    private readonly CachedText _tVisible = new();
    private readonly DevKeyValue _visible = new("Visible");
    private RenderBackend? _backendKey;
    private string _backendText = "—";

    private DevToggle _bloom = null!, _ssao = null!, _ssr = null!, _shadows = null!;
    private DevToggle _taa = null!, _dof = null!, _wire = null!, _diag = null!;
    private long _trisKey = -1;
    private string _trisText = "—";
    private DevStepper _view = null!, _exposure = null!, _ambient = null!;
    private int _viewKey = -1;
    private string _viewText = "Shaded";

    public string Title => "Renderer";
    public DevCategory Category => DevCategory.Render3D;
    public bool IsAvailable => ZigoteEngine.Instance is not null;

    public Widget Build(BuildContext context)
    {
        _view = new DevStepper(
            label: "Debug view",
            value: "Shaded",
            onPrev: () => StepView(-1),
            onNext: () => StepView(1)
        );
        _bloom = Amount(
            label: "Bloom",
            get: s => s.BloomIntensity,
            set: (ref s, v) => s.BloomIntensity = v,
            @default: 0.45f
        );
        _ssao = Amount(
            label: "SSAO",
            get: s => s.SsaoStrength,
            set: (ref s, v) => s.SsaoStrength = v,
            @default: 0.5f
        );
        _ssr = Amount(
            label: "SSR",
            get: s => s.SsrIntensity,
            set: (ref s, v) => s.SsrIntensity = v,
            @default: 0.5f
        );
        _shadows = Amount(
            label: "Shadows",
            get: s => s.ShadowStrength,
            set: (ref s, v) => s.ShadowStrength = v,
            @default: 0.55f
        );
        _taa = Flag(
            label: "TAA",
            get: s => s.TaaEnabled,
            set: (ref s, v) => s.TaaEnabled = v
        );
        _dof = Flag(
            label: "Depth of field",
            get: s => s.DofEnabled,
            set: (ref s, v) => s.DofEnabled = v
        );
        _wire = Flag(
            label: "Wireframe",
            get: s => s.Wireframe,
            set: (ref s, v) => s.Wireframe = v
        );
        _diag = Flag(
            label: "Diagnostic mode",
            get: s => s.DiagnosticMode,
            set: (ref s, v) => s.DiagnosticMode = v
        );
        _exposure = new DevStepper(
            label: "Exposure",
            value: "1.00",
            onPrev: () => Tune((ref s) =>
                s.Exposure = Clamp(v: s.Exposure - 0.05f, lo: 0.2f, hi: 3f)
            ),
            onNext: () => Tune((ref s) =>
                s.Exposure = Clamp(v: s.Exposure + 0.05f, lo: 0.2f, hi: 3f)
            )
        );
        _ambient = new DevStepper(
            label: "Ambient",
            value: "0.00",
            onPrev: () => Tune((ref s) =>
                s.AmbientIntensity = Clamp(v: s.AmbientIntensity - 0.05f, lo: 0f, hi: 2f)
            ),
            onNext: () => Tune((ref s) =>
                s.AmbientIntensity = Clamp(v: s.AmbientIntensity + 0.05f, lo: 0f, hi: 2f)
            )
        );

        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                new DevSectionHeader("Live"),
                _backend,
                _surface,
                _counters,
                _visible,
                new DevSectionHeader("Debug view"),
                _view,
                new DevSectionHeader("Features"),
                _bloom,
                _ssao,
                _ssr,
                _shadows,
                _taa,
                _dof,
                _wire,
                _diag,
                new DevSectionHeader("Tuning"),
                _exposure,
                _ambient,
            },
        };
    }

    public void Refresh(float dt)
    {
        var t = App.Active?.Theme ?? ThemeData.Dark;
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

        if (DebugStats.EngineOk)
        {
            var e = DebugStats.Engine;
            // DevFormat.Count allocates, so re-run it only when the count changed.
            long trisNow = e.Triangles;
            if (trisNow != _trisKey)
            {
                _trisKey = trisNow;
                _trisText = DevFormat.Count(trisNow);
            }

            _counters.Value = _tCounters.Update($"{e.DrawCalls} / {_trisText} / {e.RenderPasses}");
            _visible.Value = _tVisible.Update($"{e.VisibleObjects}");
            _visible.ValueColor = t.Hint;
        }

        var s = Read();
        // ViewName allocates via Enum.ToString, so re-run it only when the view changed.
        int viewNow = (int)s.DebugView;
        if (viewNow != _viewKey)
        {
            _viewKey = viewNow;
            _viewText = ViewName(viewNow);
        }

        _view.Value = _viewText;
        _bloom.Value = s.BloomIntensity > 0f;
        _ssao.Value = s.SsaoStrength > 0f;
        _ssr.Value = s.SsrIntensity > 0f;
        _shadows.Value = s.ShadowStrength > 0f;
        _taa.Value = s.TaaEnabled != 0f;
        _dof.Value = s.DofEnabled != 0f;
        _wire.Value = s.Wireframe != 0f;
        _diag.Value = s.DiagnosticMode != 0f;
        _exposure.Value = _tExposure.Update($"{s.Exposure:0.00}");
        _ambient.Value = _tAmbient.Update($"{s.AmbientIntensity:0.00}");
    }

    private DevToggle Amount(string label, Func<ZgRenderSettings3D, float> get, SetFloat set,
        float @default)
    {
        return new DevToggle(
            label: label,
            value: get(Read()) > 0f,
            onChanged: on => Tune((ref s) =>
                {
                    if (on)
                    {
                        set(
                            s: ref s,
                            v: _remembered.TryGetValue(key: label, value: out float v) && v > 0f
                                ? v
                                : @default
                        );
                    }
                    else
                    {
                        _remembered[label] = get(s);
                        set(s: ref s, v: 0f);
                    }
                }
            )
        );
    }

    private DevToggle Flag(string label, Func<ZgRenderSettings3D, float> get, SetFloat set)
    {
        return new DevToggle(
            label: label,
            value: get(Read()) != 0f,
            onChanged: on => Tune((ref s) => set(s: ref s, v: on ? 1f : 0f))
        );
    }

    private void StepView(int dir)
    {
        Tune((ref s) =>
            {
                int v = ((int)s.DebugView + dir + 16) % 16;
                s.DebugView = v;
            }
        );
    }

    private static string ViewName(int v) => v == 0 ? "Shaded" : ((DebugView)v).ToString();

    private static float Clamp(float v, float lo, float hi) =>
        Math.Clamp(value: v, min: lo, max: hi);

    private static ZgRenderSettings3D Read()
    {
        try
        {
            return ZigoteEngine.Instance?.GetRenderSettings3D() ?? default;
        }
        catch
        {
            return default;
        }
    }

    private static void Tune(Mutate f)
    {
        try
        {
            var e = ZigoteEngine.Instance;
            if (e is null) return;
            var s = e.GetRenderSettings3D();
            f(ref s);
            e.SetRenderSettings3D(s);
        }
        catch
        {
            /* engine not ready */
        }
    }

    // ── Feature helpers ──

    private delegate void Mutate(ref ZgRenderSettings3D s);

    private delegate void SetFloat(ref ZgRenderSettings3D s, float v);
}
