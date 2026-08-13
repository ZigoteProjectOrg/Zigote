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

    private void Apply() => _state.Apply3D(in _s);

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
            outer: outer,
            title: "Diagnostics",
            reset: null,
            CheckRow(
                label: "Diagnostic Mode",
                value: _s.DiagnosticMode != 0f,
                set: v => _s.DiagnosticMode = v ? 1f : 0f
            ),
            DebugViewRow()
        );

        AddGroup(
            outer: outer,
            title: "Editor Viewport",
            reset: null,
            MutedRow(
                "Render the edit viewport without TAA, bloom, SSR and DoF. Play mode always runs the full authored settings."
            ),
            CheckRow(
                label: "Reduced graphics",
                value: _state.ReducedEditorGraphics,
                set: v => _state.ReducedEditorGraphics = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Viewport FPS (testing)",
            reset: null,
            MutedRow(
                "Unlock = Continuous on + VSync off + Limit Off. A limit caps the render loop."
            ),
            CheckRow(
                label: "Continuous Render",
                value: App.Active?.ForceContinuousRender ?? false,
                set: v =>
                {
                    if (App.Active is { } a) a.ForceContinuousRender = v;
                }
            ),
            CheckRow(
                label: "VSync",
                value: App.Active?.VSync ?? true,
                set: v =>
                {
                    if (App.Active is { } a) a.VSync = v;
                }
            ),
            FpsLimitRow()
        );

        AddGroup(
            outer: outer,
            title: "Environment",
            reset: () => ResetGroup("Environment"),
            new Row {
                CrossAxisAlignment = CrossAxisAlignment.Center,
                Children = {
                    new Expanded(
                        new AdwButton(label: "Load HDRI…", onPressed: LoadHdri) { Compact = true }
                    ),
                    new SizedBox(6f),
                    new Expanded(
                        new AdwButton(label: "Procedural", onPressed: UseProceduralEnv) {
                            Compact = true,
                        }
                    ),
                },
            },
            SliderRow(
                label: "Ambient",
                value: _s.AmbientIntensity,
                min: 0f,
                max: 2f,
                set: v => _s.AmbientIntensity = v
            ),
            ColorRows(
                label: "Sky Horizon",
                r: _s.SkyHorizonR,
                g: _s.SkyHorizonG,
                b: _s.SkyHorizonB,
                sr: v => _s.SkyHorizonR = v,
                sg: v => _s.SkyHorizonG = v,
                sb: v => _s.SkyHorizonB = v
            ),
            ColorRows(
                label: "Sky Zenith",
                r: _s.SkyZenithR,
                g: _s.SkyZenithG,
                b: _s.SkyZenithB,
                sr: v => _s.SkyZenithR = v,
                sg: v => _s.SkyZenithG = v,
                sb: v => _s.SkyZenithB = v
            ),
            ColorRows(
                label: "Sky Ground",
                r: _s.SkyGroundR,
                g: _s.SkyGroundG,
                b: _s.SkyGroundB,
                sr: v => _s.SkyGroundR = v,
                sg: v => _s.SkyGroundG = v,
                sb: v => _s.SkyGroundB = v
            ),
            ColorRows(
                label: "Env Average",
                r: _s.EnvAvgR,
                g: _s.EnvAvgG,
                b: _s.EnvAvgB,
                sr: v => _s.EnvAvgR = v,
                sg: v => _s.EnvAvgG = v,
                sb: v => _s.EnvAvgB = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Studio Lights",
            reset: () => ResetGroup("Studio Lights"),
            SliderRow(
                label: "Sun Azimuth",
                value: _s.SunAzimuthDeg,
                min: 0f,
                max: 360f,
                set: v => _s.SunAzimuthDeg = v
            ),
            SliderRow(
                label: "Sun Elevation",
                value: _s.SunElevationDeg,
                min: 0f,
                max: 90f,
                set: v => _s.SunElevationDeg = v
            ),
            SliderRow(
                label: "Sun Intensity",
                value: _s.SunIntensity,
                min: 0f,
                max: 15f,
                set: v => _s.SunIntensity = v
            ),
            SliderRow(
                label: "Sun Sharpness",
                value: _s.SunSharpness,
                min: 1f,
                max: 300f,
                set: v => _s.SunSharpness = v
            ),
            SliderRow(
                label: "Overhead Softbox",
                value: _s.Overhead,
                min: 0f,
                max: 4f,
                set: v => _s.Overhead = v
            ),
            SliderRow(
                label: "Horizon Glow",
                value: _s.HorizonGlow,
                min: 0f,
                max: 3f,
                set: v => _s.HorizonGlow = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Post-processing",
            reset: () => ResetGroup("Post-processing"),
            SliderRow(
                label: "Exposure",
                value: _s.Exposure,
                min: 0.2f,
                max: 3f,
                set: v => _s.Exposure = v
            ),
            SliderRow(
                label: "Contrast",
                value: _s.Contrast,
                min: 0f,
                max: 1f,
                set: v => _s.Contrast = v
            ),
            SliderRow(
                label: "Saturation",
                value: _s.Saturation,
                min: 0.5f,
                max: 2f,
                set: v => _s.Saturation = v
            ),
            SliderRow(
                label: "Bloom Threshold",
                value: _s.BloomThreshold,
                min: 0f,
                max: 4f,
                set: v => _s.BloomThreshold = v
            ),
            SliderRow(
                label: "Bloom Knee",
                value: _s.BloomKnee,
                min: 0.01f,
                max: 1f,
                set: v => _s.BloomKnee = v
            ),
            SliderRow(
                label: "Bloom Intensity",
                value: _s.BloomIntensity,
                min: 0f,
                max: 2f,
                set: v => _s.BloomIntensity = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Ambient Occlusion",
            reset: () => ResetGroup("Ambient Occlusion"),
            SliderRow(
                label: "AO Radius",
                value: _s.SsaoRadius,
                min: 0.05f,
                max: 2f,
                set: v => _s.SsaoRadius = v
            ),
            SliderRow(
                label: "AO Strength",
                value: _s.SsaoStrength,
                min: 0f,
                max: 3f,
                set: v => _s.SsaoStrength = v
            ),
            SliderRow(
                label: "AO Bias",
                value: _s.SsaoBias,
                min: 0f,
                max: 0.1f,
                set: v => _s.SsaoBias = v
            ),
            SliderRow(
                label: "AO Power",
                value: _s.SsaoPower,
                min: 0.5f,
                max: 4f,
                set: v => _s.SsaoPower = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Reflections (SSR)",
            reset: () => ResetGroup("Reflections (SSR)"),
            SliderRow(
                label: "SSR Intensity",
                value: _s.SsrIntensity,
                min: 0f,
                max: 1.5f,
                set: v => _s.SsrIntensity = v
            ),
            SliderRow(
                label: "SSR Distance",
                value: _s.SsrMaxDistance,
                min: 1f,
                max: 20f,
                set: v => _s.SsrMaxDistance = v
            ),
            SliderRow(
                label: "SSR Thickness",
                value: _s.SsrThickness,
                min: 0.05f,
                max: 2f,
                set: v => _s.SsrThickness = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Anti-aliasing (TAA)",
            reset: () => ResetGroup("Anti-aliasing (TAA)"),
            SliderRow(
                label: "TAA Enabled",
                value: _s.TaaEnabled,
                min: 0f,
                max: 1f,
                set: v => _s.TaaEnabled = v
            ),
            SliderRow(
                label: "TAA Feedback",
                value: _s.TaaFeedback,
                min: 0.5f,
                max: 0.97f,
                set: v => _s.TaaFeedback = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Shadows",
            reset: () => ResetGroup("Shadows"),
            SliderRow(
                label: "Shadow Strength",
                value: _s.ShadowStrength,
                min: 0f,
                max: 1f,
                set: v => _s.ShadowStrength = v
            ),
            SliderRow(
                label: "Shadow Bias",
                value: _s.ShadowBias,
                min: 0f,
                max: 0.02f,
                set: v => _s.ShadowBias = v
            ),
            SliderRow(
                label: "Shadow Softness",
                value: _s.ShadowSoftness,
                min: 0.25f,
                max: 6f,
                set: v => _s.ShadowSoftness = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Material",
            reset: () => ResetGroup("Material"),
            SliderRow(
                label: "Clearcoat",
                value: _s.Clearcoat,
                min: 0f,
                max: 1f,
                set: v => _s.Clearcoat = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Depth of Field",
            reset: () => ResetGroup("Depth of Field"),
            CheckRow(
                label: "Enabled",
                value: _s.DofEnabled != 0f,
                set: v => _s.DofEnabled = v ? 1f : 0f
            ),
            SliderRow(
                label: "Focus Distance",
                value: _s.DofFocusDistance,
                min: 1f,
                max: 30f,
                set: v => _s.DofFocusDistance = v
            ),
            SliderRow(
                label: "F-Stop",
                value: _s.DofFStop,
                min: 1f,
                max: 16f,
                set: v => _s.DofFStop = v
            ),
            SliderRow(
                label: "Max Blur (px)",
                value: _s.DofMaxCoc,
                min: 0f,
                max: 40f,
                set: v => _s.DofMaxCoc = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Fog",
            reset: () => ResetGroup("Fog"),
            SliderRow(
                label: "Density",
                value: _s.FogDensity,
                min: 0f,
                max: 1f,
                set: v => _s.FogDensity = v
            ),
            ColorRows(
                label: "Colour",
                r: _s.FogColorR,
                g: _s.FogColorG,
                b: _s.FogColorB,
                sr: v => _s.FogColorR = v,
                sg: v => _s.FogColorG = v,
                sb: v => _s.FogColorB = v
            ),
            SliderRow(
                label: "Height",
                value: _s.FogHeight,
                min: -20f,
                max: 20f,
                set: v => _s.FogHeight = v
            ),
            SliderRow(
                label: "Height Falloff",
                value: _s.FogHeightFalloff,
                min: 0f,
                max: 2f,
                set: v => _s.FogHeightFalloff = v
            ),
            SliderRow(
                label: "Sun In-scatter",
                value: _s.FogSunInscatter,
                min: 0f,
                max: 4f,
                set: v => _s.FogSunInscatter = v
            ),
            SliderRow(
                label: "Anisotropy",
                value: _s.FogAnisotropy,
                min: -0.95f,
                max: 0.95f,
                set: v => _s.FogAnisotropy = v
            )
        );

        AddGroup(
            outer: outer,
            title: "Auto-exposure",
            reset: () => ResetGroup("Auto-exposure"),
            CheckRow(
                label: "Enabled",
                value: _s.AutoExposureEnabled != 0f,
                set: v => _s.AutoExposureEnabled = v ? 1f : 0f
            ),
            SliderRow(
                label: "Key (mid-grey)",
                value: _s.AutoExposureKey,
                min: 0.02f,
                max: 0.6f,
                set: v => _s.AutoExposureKey = v
            ),
            SliderRow(
                label: "Min Luminance",
                value: _s.AutoExposureMin,
                min: 0.001f,
                max: 1f,
                set: v => _s.AutoExposureMin = v
            ),
            SliderRow(
                label: "Max Luminance",
                value: _s.AutoExposureMax,
                min: 0.5f,
                max: 40f,
                set: v => _s.AutoExposureMax = v
            ),
            SliderRow(
                label: "Adapt Speed",
                value: _s.AutoExposureSpeed,
                min: 0.01f,
                max: 1f,
                set: v => _s.AutoExposureSpeed = v
            )
        );

        outer.Children.Add(
            new Padding(
                padding: EdgeInsets.Only(top: Spacing.Md),
                child: new AdwButton(label: "Reset all to defaults", onPressed: ResetDefaults) {
                    Style = AdwButtonStyle.Destructive,
                }
            )
        );

        return new Padding(padding: EdgeInsets.Only(top: Spacing.Xs), child: outer);
    }

    // ── Group machinery ─────────────────────────────────────────────────────────

    private void AddGroup(Column outer, string title, Action? reset, params Widget[] rows)
    {
        bool collapsed = _collapsedGroups.Contains(title);
        outer.Children.Add(
            new GroupHeader(
                title: title,
                theme: _theme,
                collapsed: collapsed,
                onToggle: () =>
                {
                    if (!_collapsedGroups.Remove(title)) _collapsedGroups.Add(title);
                    Rebuild();
                },
                onReset: reset
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
                padding: EdgeInsets.Only(bottom: Spacing.Sm),
                child: new ColoredBox(
                    color: _theme.SurfaceAlt,
                    child: new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: Spacing.Sm, vertical: Spacing.Xs),
                        child: body
                    )
                )
            )
        );
    }

    private Widget DebugViewRow()
    {
        var debugViews = (DebugView[])Enum.GetValues(typeof(DebugView));
        return new Padding(
            padding: EdgeInsets.Symmetric(horizontal: 0f, vertical: Spacing.Xxs),
            child: new Row {
                Children = {
                    new SizedBox(
                        width: LabelColW,
                        child: new Label(
                            text: "Debug View",
                            fontSize: _theme.FontSizeCaption,
                            color: _theme.OnSurface
                        )
                    ),
                    new Expanded(
                        new AdwDropDown(
                            items: [.. debugViews.Select(PrettyDebugView)],
                            selectedIndex: Array.IndexOf(
                                array: debugViews,
                                value: (DebugView)(int)_s.DebugView
                            ),
                            onSelected: i =>
                            {
                                _s.DebugView = (int)debugViews[i];
                                Apply();
                            }
                        ) { Compact = true }
                    ),
                },
            }
        );
    }

    private Widget CheckRow(string label, bool value, Action<bool> set)
    {
        return new Padding(
            padding: EdgeInsets.Symmetric(horizontal: 0f, vertical: Spacing.Xxs),
            child: new Row {
                Children = {
                    new SizedBox(
                        width: LabelColW,
                        child: new Label(
                            text: label,
                            fontSize: _theme.FontSizeCaption,
                            color: _theme.OnSurface
                        )
                    ),
                    new AdwSwitch(
                        value: value,
                        onChanged: v =>
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
            padding: EdgeInsets.Symmetric(horizontal: 0f, vertical: Spacing.Xxs),
            child: new Label(text: text, fontSize: _theme.FontSizeCaption, color: _theme.Hint)
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
                new AdwButton(
                    label: label,
                    onPressed: () =>
                    {
                        if (App.Active is not { } a) return;
                        a.FrameRateLimit = fps;
                        if (fps != 0) a.ForceContinuousRender = true;
                        Rebuild();
                    }
                ) { Compact = true }
            );
        }

        int current = App.Active?.FrameRateLimit ?? 0;
        return new Padding(
            padding: EdgeInsets.Symmetric(horizontal: 0f, vertical: Spacing.Xxs),
            child: new Column {
                CrossAxisAlignment = CrossAxisAlignment.Stretch,
                MainAxisSize = MainAxisSize.Min,
                Children = {
                    new Label(
                        text:
                        $"FPS Limit  (current: {(current == 0 ? $"display {App.Active?.DisplayRefreshHz ?? 60f:0} Hz" : current.ToString())})",
                        fontSize: _theme.FontSizeCaption,
                        color: _theme.Hint
                    ),
                    new SizedBox(4f),
                    new Row {
                        Children = {
                            Preset(label: "Off", fps: 0),
                            new SizedBox(4f),
                            Preset(label: "30", fps: 30),
                            new SizedBox(4f),
                            Preset(label: "60", fps: 60),
                            new SizedBox(4f),
                            Preset(label: "120", fps: 120),
                            new SizedBox(4f),
                            Preset(label: "144", fps: 144),
                            new SizedBox(4f),
                            Preset(label: "240", fps: 240),
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
                padding: EdgeInsets.Only(top: 4f),
                child: new Label(text: label, fontSize: _theme.FontSizeCaption, color: _theme.Hint)
            )
        );
        c.Children.Add(
            SliderRow(
                label: "  R",
                value: r,
                min: 0f,
                max: 1f,
                set: sr
            )
        );
        c.Children.Add(
            SliderRow(
                label: "  G",
                value: g,
                min: 0f,
                max: 1f,
                set: sg
            )
        );
        c.Children.Add(
            SliderRow(
                label: "  B",
                value: b,
                min: 0f,
                max: 1f,
                set: sb
            )
        );
        return c;
    }

    private Widget SliderRow(string label, float value, float min, float max, Action<float> set)
    {
        var valLabel = new Label(
            text: value.ToString("0.###"),
            fontSize: _theme.FontSizeCaption,
            color: _theme.Hint
        );
        var slider = new AdwSlider(value) {
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
            padding: EdgeInsets.Symmetric(horizontal: 0f, vertical: Spacing.Xxs),
            child: new Row {
                Children = {
                    new SizedBox(
                        width: LabelColW,
                        child: new Label(
                            text: label,
                            fontSize: _theme.FontSizeCaption,
                            color: _theme.OnSurface
                        )
                    ),
                    new Expanded(slider),
                    new SizedBox(width: ValueColW, child: valLabel),
                },
            }
        );
    }

    private void LoadHdri()
    {
        var app = App.Active;
        if (app is null) return;
        string root = Directory.Exists("examples")
            ? Path.GetFullPath("examples")
            : Directory.GetCurrentDirectory();

        Load();
        return;

        // FileDialog routes to the native OS dialog or the in-app browser automatically.
        async void Load()
        {
            try
            {
                string? path = await FileDialog.OpenFileAsync(
                    title: "Load HDRI / Environment",
                    startDirectory: root,
                    filters: [
                        new FileDialogFilter(
                            name: "Images",
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
    private static ZgRenderSettings3D Defaults() => RenderDefaults.Settings3D();

    public override Size Measure(Constraints c)
    {
        _size = _content.Measure(c);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _content.Layout(origin);
    }

    public override void Paint(PaintList paint) => _content.Paint(paint);

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _content.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren() => [_content];

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
            x: Bounds.Right - 22f,
            y: Bounds.Y,
            width: 22f,
            height: Bounds.Height
        );

        public override Size Measure(Constraints c)
        {
            _size = c.Constrain(new Size(width: c.MaxWidth, height: 26f));
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _size.Width,
                height: _size.Height
            );
        }

        public override void Paint(PaintList paint)
        {
            if (_hovered)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X,
                        y: Bounds.Y,
                        width: Bounds.Width,
                        height: Bounds.Height - 1f
                    ),
                    color: _theme.ControlHover,
                    radius: 4f
                );
            }

            const float cs = 14f;
            string chevron = _collapsed ? Icons.ChevronRight : Icons.ChevronDown;
            Icons.Draw(
                paint: paint,
                glyph: chevron,
                box: new Rect(
                    x: Bounds.X,
                    y: Bounds.Y,
                    width: cs,
                    height: Bounds.Height
                ),
                color: _theme.TextSecondary,
                size: cs
            );

            float fs = _theme.FontSizeBody;
            float ty = Bounds.Y + ((Bounds.Height - fs) / 2f) + (fs * 0.8f);
            paint.AddText(
                text: _title,
                baselineX: Bounds.X + cs + 2f,
                baselineY: ty,
                color: _theme.OnSurface,
                fontSize: fs,
                fontWeight: FontWeight.SemiBold
            );

            if (_onReset != null)
            {
                var rr = ResetRect;
                Icons.Draw(
                    paint: paint,
                    glyph: Icons.Refresh,
                    box: rr,
                    color: _resetHovered ? _theme.OnSurface : _theme.Hint.WithAlpha(0.6f),
                    size: 13f
                );
            }

            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: Bounds.Bottom - 1f,
                    width: Bounds.Width,
                    height: 1f
                ),
                color: _theme.Separator
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
            bool rh = _onReset != null && ResetRect.Contains(px: point.X, py: point.Y);
            if (rh != _resetHovered)
            {
                _resetHovered = rh;
                MarkNeedsPaint();
            }
        }

        public override void OnPointerUp(Offset point)
        {
            if (!Bounds.Contains(px: point.X, py: point.Y)) return;
            if (_onReset != null && ResetRect.Contains(px: point.X, py: point.Y)) _onReset();
            else _onToggle();
        }
    }
}
