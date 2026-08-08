using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.Editor.Scene;
using Zigote.Runtime.Scene;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Panels;

/// <summary>
///     Settings tab: live editing of the 3D renderer settings (environment, studio lights,
///     post-processing, shadows, material) grouped into collapsible, card-style sections. Each control
///     writes straight through to the native renderer via
///     <see cref="ZigoteEngine.SetRenderSettings3D" />,
///     and each group has a reset-to-defaults affordance.
/// </summary>
public sealed class SettingsPanel : Widget
{
    private const float LabelColW = 124f;
    private const float ValueColW = 44f;

    // Groups collapsed by default — advanced sections start closed so the panel opens compact.
    private readonly HashSet<string> _collapsedGroups = [
        "Ambient Occlusion", "Reflections (SSR)", "Anti-aliasing (TAA)", "Material",
        "Depth of Field",
    ];

    private readonly EditorState _state;
    private readonly ThemeData _theme;
    private Widget _content;
    private ZgRenderSettings3D _s;
    private Size _size;

    public SettingsPanel(EditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;
        // Edit the AUTHORED settings, not the engine's live ones — with the reduced edit-mode preset
        // active the engine holds zeroed TAA/bloom/SSR/DoF that must never round-trip into authoring.
        _s = state.Authored3D;
        _content = Build();
    }

    private void Apply()
    {
        _state.Apply3D(in _s);
    }

    private void Rebuild()
    {
        _content = Build();
        RequestLayout();
    }

    private Widget Build()
    {
        var outer = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };

        AddGroup(
            outer,
            "Diagnostics",
            null,
            CheckRow(
                "Diagnostic Mode",
                _s.DiagnosticMode != 0f,
                v => _s.DiagnosticMode = v ? 1f : 0f
            ),
            DebugViewRow()
        );

        AddGroup(
            outer,
            "Editor Viewport",
            null,
            MutedRow(
                "Render the edit viewport without TAA, bloom, SSR and DoF. Play mode always runs the full authored settings."
            ),
            CheckRow(
                "Reduced graphics",
                _state.ReducedEditorGraphics,
                v => _state.ReducedEditorGraphics = v
            )
        );

        AddGroup(
            outer,
            "Viewport FPS (testing)",
            null,
            MutedRow(
                "Unlock = Continuous on + VSync off + Limit Off. A limit caps the render loop."
            ),
            CheckRow(
                "Continuous Render",
                App.Active?.ForceContinuousRender ?? false,
                v =>
                {
                    if (App.Active is { } a) a.ForceContinuousRender = v;
                }
            ),
            CheckRow(
                "VSync",
                App.Active?.VSync ?? true,
                v =>
                {
                    if (App.Active is { } a) a.VSync = v;
                }
            ),
            FpsLimitRow()
        );

        AddGroup(
            outer,
            "Environment",
            () => ResetGroup("Environment"),
            new Row {
                CrossAxisAlignment = CrossAxisAlignment.Center,
                Children = {
                    new Expanded(
                        new SizedBox(height: 26f, child: new Button("Load HDRI…", LoadHdri))
                    ),
                    new SizedBox(6f),
                    new Expanded(
                        new SizedBox(height: 26f, child: new Button("Procedural", UseProceduralEnv))
                    ),
                },
            },
            SliderRow(
                "Ambient",
                _s.AmbientIntensity,
                0f,
                2f,
                v => _s.AmbientIntensity = v
            ),
            ColorRows(
                "Sky Horizon",
                _s.SkyHorizonR,
                _s.SkyHorizonG,
                _s.SkyHorizonB,
                v => _s.SkyHorizonR = v,
                v => _s.SkyHorizonG = v,
                v => _s.SkyHorizonB = v
            ),
            ColorRows(
                "Sky Zenith",
                _s.SkyZenithR,
                _s.SkyZenithG,
                _s.SkyZenithB,
                v => _s.SkyZenithR = v,
                v => _s.SkyZenithG = v,
                v => _s.SkyZenithB = v
            ),
            ColorRows(
                "Sky Ground",
                _s.SkyGroundR,
                _s.SkyGroundG,
                _s.SkyGroundB,
                v => _s.SkyGroundR = v,
                v => _s.SkyGroundG = v,
                v => _s.SkyGroundB = v
            ),
            ColorRows(
                "Env Average",
                _s.EnvAvgR,
                _s.EnvAvgG,
                _s.EnvAvgB,
                v => _s.EnvAvgR = v,
                v => _s.EnvAvgG = v,
                v => _s.EnvAvgB = v
            )
        );

        AddGroup(
            outer,
            "Studio Lights",
            () => ResetGroup("Studio Lights"),
            SliderRow(
                "Sun Azimuth",
                _s.SunAzimuthDeg,
                0f,
                360f,
                v => _s.SunAzimuthDeg = v
            ),
            SliderRow(
                "Sun Elevation",
                _s.SunElevationDeg,
                0f,
                90f,
                v => _s.SunElevationDeg = v
            ),
            SliderRow(
                "Sun Intensity",
                _s.SunIntensity,
                0f,
                15f,
                v => _s.SunIntensity = v
            ),
            SliderRow(
                "Sun Sharpness",
                _s.SunSharpness,
                1f,
                300f,
                v => _s.SunSharpness = v
            ),
            SliderRow(
                "Overhead Softbox",
                _s.Overhead,
                0f,
                4f,
                v => _s.Overhead = v
            ),
            SliderRow(
                "Horizon Glow",
                _s.HorizonGlow,
                0f,
                3f,
                v => _s.HorizonGlow = v
            )
        );

        AddGroup(
            outer,
            "Post-processing",
            () => ResetGroup("Post-processing"),
            SliderRow(
                "Exposure",
                _s.Exposure,
                0.2f,
                3f,
                v => _s.Exposure = v
            ),
            SliderRow(
                "Contrast",
                _s.Contrast,
                0f,
                1f,
                v => _s.Contrast = v
            ),
            SliderRow(
                "Saturation",
                _s.Saturation,
                0.5f,
                2f,
                v => _s.Saturation = v
            ),
            SliderRow(
                "Bloom Threshold",
                _s.BloomThreshold,
                0f,
                4f,
                v => _s.BloomThreshold = v
            ),
            SliderRow(
                "Bloom Knee",
                _s.BloomKnee,
                0.01f,
                1f,
                v => _s.BloomKnee = v
            ),
            SliderRow(
                "Bloom Intensity",
                _s.BloomIntensity,
                0f,
                2f,
                v => _s.BloomIntensity = v
            )
        );

        AddGroup(
            outer,
            "Ambient Occlusion",
            () => ResetGroup("Ambient Occlusion"),
            SliderRow(
                "AO Radius",
                _s.SsaoRadius,
                0.05f,
                2f,
                v => _s.SsaoRadius = v
            ),
            SliderRow(
                "AO Strength",
                _s.SsaoStrength,
                0f,
                3f,
                v => _s.SsaoStrength = v
            ),
            SliderRow(
                "AO Bias",
                _s.SsaoBias,
                0f,
                0.1f,
                v => _s.SsaoBias = v
            ),
            SliderRow(
                "AO Power",
                _s.SsaoPower,
                0.5f,
                4f,
                v => _s.SsaoPower = v
            )
        );

        AddGroup(
            outer,
            "Reflections (SSR)",
            () => ResetGroup("Reflections (SSR)"),
            SliderRow(
                "SSR Intensity",
                _s.SsrIntensity,
                0f,
                1.5f,
                v => _s.SsrIntensity = v
            ),
            SliderRow(
                "SSR Distance",
                _s.SsrMaxDistance,
                1f,
                20f,
                v => _s.SsrMaxDistance = v
            ),
            SliderRow(
                "SSR Thickness",
                _s.SsrThickness,
                0.05f,
                2f,
                v => _s.SsrThickness = v
            )
        );

        AddGroup(
            outer,
            "Anti-aliasing (TAA)",
            () => ResetGroup("Anti-aliasing (TAA)"),
            SliderRow(
                "TAA Enabled",
                _s.TaaEnabled,
                0f,
                1f,
                v => _s.TaaEnabled = v
            ),
            SliderRow(
                "TAA Feedback",
                _s.TaaFeedback,
                0.5f,
                0.97f,
                v => _s.TaaFeedback = v
            )
        );

        AddGroup(
            outer,
            "Shadows",
            () => ResetGroup("Shadows"),
            SliderRow(
                "Shadow Strength",
                _s.ShadowStrength,
                0f,
                1f,
                v => _s.ShadowStrength = v
            ),
            SliderRow(
                "Shadow Bias",
                _s.ShadowBias,
                0f,
                0.02f,
                v => _s.ShadowBias = v
            ),
            SliderRow(
                "Shadow Softness",
                _s.ShadowSoftness,
                0.25f,
                6f,
                v => _s.ShadowSoftness = v
            )
        );

        AddGroup(
            outer,
            "Material",
            () => ResetGroup("Material"),
            SliderRow(
                "Clearcoat",
                _s.Clearcoat,
                0f,
                1f,
                v => _s.Clearcoat = v
            )
        );

        AddGroup(
            outer,
            "Depth of Field",
            () => ResetGroup("Depth of Field"),
            CheckRow("Enabled", _s.DofEnabled != 0f, v => _s.DofEnabled = v ? 1f : 0f),
            SliderRow(
                "Focus Distance",
                _s.DofFocusDistance,
                1f,
                30f,
                v => _s.DofFocusDistance = v
            ),
            SliderRow(
                "F-Stop",
                _s.DofFStop,
                1f,
                16f,
                v => _s.DofFStop = v
            ),
            SliderRow(
                "Max Blur (px)",
                _s.DofMaxCoc,
                0f,
                40f,
                v => _s.DofMaxCoc = v
            )
        );

        AddGroup(
            outer,
            "Fog",
            () => ResetGroup("Fog"),
            SliderRow(
                "Density",
                _s.FogDensity,
                0f,
                1f,
                v => _s.FogDensity = v
            ),
            ColorRows(
                "Colour",
                _s.FogColorR,
                _s.FogColorG,
                _s.FogColorB,
                v => _s.FogColorR = v,
                v => _s.FogColorG = v,
                v => _s.FogColorB = v
            ),
            SliderRow(
                "Height",
                _s.FogHeight,
                -20f,
                20f,
                v => _s.FogHeight = v
            ),
            SliderRow(
                "Height Falloff",
                _s.FogHeightFalloff,
                0f,
                2f,
                v => _s.FogHeightFalloff = v
            ),
            SliderRow(
                "Sun In-scatter",
                _s.FogSunInscatter,
                0f,
                4f,
                v => _s.FogSunInscatter = v
            ),
            SliderRow(
                "Anisotropy",
                _s.FogAnisotropy,
                -0.95f,
                0.95f,
                v => _s.FogAnisotropy = v
            )
        );

        AddGroup(
            outer,
            "Auto-exposure",
            () => ResetGroup("Auto-exposure"),
            CheckRow(
                "Enabled",
                _s.AutoExposureEnabled != 0f,
                v => _s.AutoExposureEnabled = v ? 1f : 0f
            ),
            SliderRow(
                "Key (mid-grey)",
                _s.AutoExposureKey,
                0.02f,
                0.6f,
                v => _s.AutoExposureKey = v
            ),
            SliderRow(
                "Min Luminance",
                _s.AutoExposureMin,
                0.001f,
                1f,
                v => _s.AutoExposureMin = v
            ),
            SliderRow(
                "Max Luminance",
                _s.AutoExposureMax,
                0.5f,
                40f,
                v => _s.AutoExposureMax = v
            ),
            SliderRow(
                "Adapt Speed",
                _s.AutoExposureSpeed,
                0.01f,
                1f,
                v => _s.AutoExposureSpeed = v
            )
        );

        outer.Children.Add(
            new Padding(
                EdgeInsets.Only(top: Spacing.Md),
                new SizedBox(height: 28f, child: new Button("Reset all to defaults", ResetDefaults))
            )
        );

        return new Padding(EdgeInsets.Only(top: Spacing.Xs), outer);
    }

    // ── Group machinery ─────────────────────────────────────────────────────────

    private void AddGroup(Column outer, string title, Action? reset, params Widget[] rows)
    {
        var collapsed = _collapsedGroups.Contains(title);
        outer.Children.Add(
            new GroupHeader(
                title,
                _theme,
                collapsed,
                () =>
                {
                    if (!_collapsedGroups.Remove(title)) _collapsedGroups.Add(title);
                    Rebuild();
                },
                reset
            )
        );

        if (collapsed) return;

        var body = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };
        body.Children.AddRange(rows);
        outer.Children.Add(
            new Padding(
                EdgeInsets.Only(bottom: Spacing.Sm),
                new ColoredBox(
                    _theme.SurfaceAlt,
                    new Padding(EdgeInsets.Symmetric(Spacing.Sm, Spacing.Xs), body)
                )
            )
        );
    }

    private Widget DebugViewRow()
    {
        var debugViews = (DebugView[])Enum.GetValues(typeof(DebugView));
        return new Padding(
            EdgeInsets.Symmetric(0f, Spacing.Xxs),
            new Row {
                Children = {
                    new SizedBox(
                        LabelColW,
                        child: new Label("Debug View", _theme.FontSizeCaption, _theme.OnSurface)
                    ),
                    new Expanded(
                        new Dropdown<DebugView>(
                            debugViews,
                            Array.IndexOf(debugViews, (DebugView)(int)_s.DebugView),
                            PrettyDebugView,
                            (_, dv) =>
                            {
                                _s.DebugView = (int)dv;
                                Apply();
                            }
                        )
                    ),
                },
            }
        );
    }

    private Widget CheckRow(string label, bool value, Action<bool> set)
    {
        return new Padding(
            EdgeInsets.Symmetric(0f, Spacing.Xxs),
            new Row {
                Children = {
                    new SizedBox(
                        LabelColW,
                        child: new Label(label, _theme.FontSizeCaption, _theme.OnSurface)
                    ),
                    new Checkbox(
                        value,
                        v =>
                        {
                            set(v);
                            Apply();
                        }
                    ),
                },
            }
        );
    }

    private Widget MutedRow(string text)
    {
        return new Padding(
            EdgeInsets.Symmetric(0f, Spacing.Xxs),
            new Label(text, _theme.FontSizeCaption, _theme.Hint)
        );
    }

    // Preset buttons for the viewport frame-rate cap. Picking a non-Off preset also forces
    // continuous rendering so the cap actually governs the loop. Off (0) = follow the monitor the
    // window is on; a preset above that refresh has no effect (the display rate is the ceiling).
    private Widget FpsLimitRow()
    {
        Widget Preset(string label, int fps)
        {
            return new Expanded(
                new SizedBox(
                    height: 24f,
                    child: new Button(
                        label,
                        () =>
                        {
                            if (App.Active is not { } a) return;
                            a.FrameRateLimit = fps;
                            if (fps != 0) a.ForceContinuousRender = true;
                            Rebuild();
                        }
                    )
                )
            );
        }

        var current = App.Active?.FrameRateLimit ?? 0;
        return new Padding(
            EdgeInsets.Symmetric(0f, Spacing.Xxs),
            new Column {
                CrossAxisAlignment = CrossAxisAlignment.Stretch,
                MainAxisSize = MainAxisSize.Min,
                Children = {
                    new Label(
                        $"FPS Limit  (current: {(current == 0 ? $"display {App.Active?.DisplayRefreshHz ?? 60f:0} Hz" : current.ToString())})",
                        _theme.FontSizeCaption,
                        _theme.Hint
                    ),
                    new SizedBox(4f),
                    new Row {
                        Children = {
                            Preset("Off", 0),
                            new SizedBox(4f),
                            Preset("30", 30),
                            new SizedBox(4f),
                            Preset("60", 60),
                            new SizedBox(4f),
                            Preset("120", 120),
                            new SizedBox(4f),
                            Preset("144", 144),
                            new SizedBox(4f),
                            Preset("240", 240),
                        },
                    },
                },
            }
        );
    }

    private Widget ColorRows(string label,
        float r, float g, float b, Action<float> sr, Action<float> sg, Action<float> sb)
    {
        var c = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };
        c.Children.Add(
            new Padding(
                EdgeInsets.Only(top: 4f),
                new Label(label, _theme.FontSizeCaption, _theme.Hint)
            )
        );
        c.Children.Add(
            SliderRow(
                "  R",
                r,
                0f,
                1f,
                sr
            )
        );
        c.Children.Add(
            SliderRow(
                "  G",
                g,
                0f,
                1f,
                sg
            )
        );
        c.Children.Add(
            SliderRow(
                "  B",
                b,
                0f,
                1f,
                sb
            )
        );
        return c;
    }

    private Widget SliderRow(string label, float value, float min, float max, Action<float> set)
    {
        var valLabel = new Label(value.ToString("0.###"), _theme.FontSizeCaption, _theme.Hint);
        var slider = new Slider(value) {
            Min = min,
            Max = max,
        };
        slider.OnChanged = v =>
        {
            set(v);
            valLabel.Text = v.ToString("0.###");
            Apply();
        };
        return new Padding(
            EdgeInsets.Symmetric(0f, Spacing.Xxs),
            new Row {
                Children = {
                    new SizedBox(
                        LabelColW,
                        child: new Label(label, _theme.FontSizeCaption, _theme.OnSurface)
                    ),
                    new Expanded(slider),
                    new SizedBox(ValueColW, child: valLabel),
                },
            }
        );
    }

    private void LoadHdri()
    {
        var app = App.Active;
        if (app is null) return;
        var root = Directory.Exists("examples")
            ? Path.GetFullPath("examples")
            : Directory.GetCurrentDirectory();

        Load();
        return;

        // FileDialog routes to the native OS dialog or the in-app browser automatically.
        async void Load()
        {
            try
            {
                var path = await FileDialog.OpenFileAsync(
                    "Load HDRI / Environment",
                    root,
                    [
                        new FileDialogFilter(
                            "Images",
                            "hdr",
                            "png",
                            "jpg",
                            "jpeg",
                            "webp"
                        ),
                    ]
                );
                if (path is not null) ApplyHdri(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Settings] File dialog failed: {ex.Message}");
                app.ShowSnackbar($"File dialog failed: {ex.Message}");
            }
        }

        void ApplyHdri(string path)
        {
            try
            {
                ZigoteEngine.Instance?.SetEnvironmentHdri(File.ReadAllBytes(path));
                _state
                    .InvalidateViewport(); // env bytes aren't in the settings struct the viewport diffs
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Settings] HDRI load failed: {ex.Message}");
            }
        }
    }

    private void UseProceduralEnv()
    {
        try
        {
            ZigoteEngine.Instance?.SetEnvironmentProcedural();
            _state.InvalidateViewport();
        }
        catch
        {
            /* engine not ready */
        }
    }

    private static string PrettyDebugView(DebugView dv)
    {
        return dv switch {
            DebugView.None => "Off (shaded)",
            DebugView.BaseColor => "Base Color",
            DebugView.WorldNormal => "World Normal",
            DebugView.ViewNormal => "View Normal",
            DebugView.Roughness => "Roughness",
            DebugView.Metallic => "Metallic",
            DebugView.Alpha => "Alpha",
            DebugView.Emissive => "Emissive",
            DebugView.Depth => "Depth",
            DebugView.ViewPosition => "View Position",
            DebugView.ShadowFactor => "Shadow Factor",
            DebugView.AmbientOcclusion => "Ambient Occlusion",
            DebugView.SsrContribution => "SSR Contribution",
            DebugView.SsrHitMiss => "SSR Hit/Miss",
            DebugView.Bloom => "Bloom",
            DebugView.HdrLuminance => "HDR Luminance",
            _ => dv.ToString(),
        };
    }

    /// <summary>Reset only the fields belonging to one group to their defaults, then re-apply + rebuild.</summary>
    private void ResetGroup(string title)
    {
        var d = Defaults();
        switch (title)
        {
            case "Environment":
                _s.AmbientIntensity = d.AmbientIntensity;
                _s.SkyHorizonR = d.SkyHorizonR;
                _s.SkyHorizonG = d.SkyHorizonG;
                _s.SkyHorizonB = d.SkyHorizonB;
                _s.SkyZenithR = d.SkyZenithR;
                _s.SkyZenithG = d.SkyZenithG;
                _s.SkyZenithB = d.SkyZenithB;
                _s.SkyGroundR = d.SkyGroundR;
                _s.SkyGroundG = d.SkyGroundG;
                _s.SkyGroundB = d.SkyGroundB;
                _s.EnvAvgR = d.EnvAvgR;
                _s.EnvAvgG = d.EnvAvgG;
                _s.EnvAvgB = d.EnvAvgB;
                break;
            case "Studio Lights":
                _s.SunAzimuthDeg = d.SunAzimuthDeg;
                _s.SunElevationDeg = d.SunElevationDeg;
                _s.SunIntensity = d.SunIntensity;
                _s.SunSharpness = d.SunSharpness;
                _s.Overhead = d.Overhead;
                _s.HorizonGlow = d.HorizonGlow;
                break;
            case "Post-processing":
                _s.Exposure = d.Exposure;
                _s.Contrast = d.Contrast;
                _s.Saturation = d.Saturation;
                _s.BloomThreshold = d.BloomThreshold;
                _s.BloomKnee = d.BloomKnee;
                _s.BloomIntensity = d.BloomIntensity;
                break;
            case "Ambient Occlusion":
                _s.SsaoRadius = d.SsaoRadius;
                _s.SsaoStrength = d.SsaoStrength;
                _s.SsaoBias = d.SsaoBias;
                _s.SsaoPower = d.SsaoPower;
                break;
            case "Reflections (SSR)":
                _s.SsrIntensity = d.SsrIntensity;
                _s.SsrMaxDistance = d.SsrMaxDistance;
                _s.SsrThickness = d.SsrThickness;
                break;
            case "Anti-aliasing (TAA)":
                _s.TaaEnabled = d.TaaEnabled;
                _s.TaaFeedback = d.TaaFeedback;
                break;
            case "Shadows":
                _s.ShadowStrength = d.ShadowStrength;
                _s.ShadowBias = d.ShadowBias;
                _s.ShadowSoftness = d.ShadowSoftness;
                break;
            case "Material":
                _s.Clearcoat = d.Clearcoat;
                break;
            case "Depth of Field":
                _s.DofEnabled = d.DofEnabled;
                _s.DofFocusDistance = d.DofFocusDistance;
                _s.DofFStop = d.DofFStop;
                _s.DofMaxCoc = d.DofMaxCoc;
                break;
            case "Fog":
                _s.FogDensity = d.FogDensity;
                _s.FogColorR = d.FogColorR;
                _s.FogColorG = d.FogColorG;
                _s.FogColorB = d.FogColorB;
                _s.FogHeight = d.FogHeight;
                _s.FogHeightFalloff = d.FogHeightFalloff;
                _s.FogSunInscatter = d.FogSunInscatter;
                _s.FogAnisotropy = d.FogAnisotropy;
                break;
            case "Auto-exposure":
                _s.AutoExposureEnabled = d.AutoExposureEnabled;
                _s.AutoExposureKey = d.AutoExposureKey;
                _s.AutoExposureMin = d.AutoExposureMin;
                _s.AutoExposureMax = d.AutoExposureMax;
                _s.AutoExposureSpeed = d.AutoExposureSpeed;
                break;
        }

        Apply();
        Rebuild();
    }

    private void ResetDefaults()
    {
        _s = Defaults();
        Apply();
        Rebuild();
    }

    // Canonical defaults live in RenderDefaults (shared with the project-settings reset path).
    private static ZgRenderSettings3D Defaults()
    {
        return RenderDefaults.Settings3D();
    }

    public override Size Measure(Constraints c)
    {
        _size = _content.Measure(c);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _content.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _content.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _content.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_content];
    }

    // ── Collapsible group header ────────────────────────────────────────────────

    private sealed class GroupHeader : Widget
    {
        private readonly bool _collapsed;
        private readonly Action? _onReset;
        private readonly Action _onToggle;
        private readonly ThemeData _theme;
        private readonly string _title;
        private bool _hovered;
        private bool _resetHovered;
        private Size _size;

        public GroupHeader(string title, ThemeData theme, bool collapsed, Action onToggle,
            Action? onReset)
        {
            _title = title;
            _theme = theme;
            _collapsed = collapsed;
            _onToggle = onToggle;
            _onReset = onReset;
        }

        private Rect ResetRect => new(
            Bounds.Right - 22f,
            Bounds.Y,
            22f,
            Bounds.Height
        );

        public override Size Measure(Constraints c)
        {
            _size = c.Constrain(new Size(c.MaxWidth, 26f));
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                _size.Width,
                _size.Height
            );
        }

        public override void Paint(PaintList paint)
        {
            if (_hovered)
                paint.AddRect(
                    new Rect(
                        Bounds.X,
                        Bounds.Y,
                        Bounds.Width,
                        Bounds.Height - 1f
                    ),
                    _theme.ControlHover,
                    4f
                );

            const float cs = 14f;
            var chevron = _collapsed ? Icons.ChevronRight : Icons.ChevronDown;
            Icons.Draw(
                paint,
                chevron,
                new Rect(
                    Bounds.X,
                    Bounds.Y,
                    cs,
                    Bounds.Height
                ),
                _theme.TextSecondary,
                cs
            );

            var fs = _theme.FontSizeBody;
            var ty = Bounds.Y + (Bounds.Height - fs) / 2f + fs * 0.8f;
            paint.AddText(
                _title,
                Bounds.X + cs + 2f,
                ty,
                _theme.OnSurface,
                fs,
                fontWeight: FontWeight.SemiBold
            );

            if (_onReset != null)
            {
                var rr = ResetRect;
                Icons.Draw(
                    paint,
                    Icons.Refresh,
                    rr,
                    _resetHovered ? _theme.OnSurface : _theme.Hint.WithAlpha(0.6f),
                    13f
                );
            }

            paint.AddRect(
                new Rect(
                    Bounds.X,
                    Bounds.Bottom - 1f,
                    Bounds.Width,
                    1f
                ),
                _theme.Separator
            );
        }

        public override void OnPointerEnter()
        {
            _hovered = true;
            MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            _hovered = false;
            _resetHovered = false;
            MarkNeedsPaint();
        }

        public override void OnPointerMove(Offset point)
        {
            var rh = _onReset != null && ResetRect.Contains(point.X, point.Y);
            if (rh != _resetHovered)
            {
                _resetHovered = rh;
                MarkNeedsPaint();
            }
        }

        public override void OnPointerUp(Offset point)
        {
            if (!Bounds.Contains(point.X, point.Y)) return;
            if (_onReset != null && ResetRect.Contains(point.X, point.Y)) _onReset();
            else _onToggle();
        }
    }
}