using Xunit;
using Zigote.Cinematics;
using Zigote.Core.Native;

namespace Zigote.Tests;

/// <summary>
///     Pure optics / photometry of the physical-camera model (<see cref="Zigote.Cinematics" />). No
///     native
///     library, no editor — just the formulas and the render-settings merge.
/// </summary>
public class PhysicalCameraTests
{
    private const float Deg = 180f / MathF.PI;

    [Fact]
    public void VerticalFov_MmAndSensor()
    {
        // 50 mm on full-frame (24 mm gate height) ≈ 26.99° vertical.
        Assert.Equal(
            expected: 26.99f,
            actual: PhysicalCameraResolver.VerticalFov(focalLengthMm: 50f, sensorHeightMm: 24f) *
                    Deg,
            precision: 2
        );
        // Wider lens → wider FOV.
        Assert.Equal(
            expected: 53.13f,
            actual: PhysicalCameraResolver.VerticalFov(focalLengthMm: 24f, sensorHeightMm: 24f) *
                    Deg,
            precision: 2
        );
        // Telephoto → narrow FOV.
        Assert.Equal(
            expected: 6.87f,
            actual: PhysicalCameraResolver.VerticalFov(focalLengthMm: 200f, sensorHeightMm: 24f) *
                    Deg,
            precision: 2
        );
    }

    [Fact]
    public void CropFactor_NarrowsFovForSameFocal()
    {
        float ff = PhysicalCameraResolver.VerticalFov(
            focalLengthMm: 50f,
            sensorHeightMm: SensorFormat.Of(SensorPreset.FullFrame).HeightMm
        );
        float apsc = PhysicalCameraResolver.VerticalFov(
            focalLengthMm: 50f,
            sensorHeightMm: SensorFormat.Of(SensorPreset.ApsC).HeightMm
        );
        Assert.True(
            condition: apsc < ff,
            userMessage: "A smaller sensor crops to a narrower FOV at the same focal length."
        );
        Assert.True(SensorFormat.Of(SensorPreset.ApsC).CropFactor > 1.4f);
    }

    [Fact]
    public void CircleOfConfusion_ZeroAtFocusPlane()
    {
        Assert.Equal(
            expected: 0f,
            actual: PhysicalCameraResolver.CircleOfConfusionMm(
                focalLengthMm: 50f,
                fStop: 2.8f,
                subjectM: 8f,
                focusM: 8f
            ),
            precision: 5
        );
    }

    [Fact]
    public void CircleOfConfusion_DoublesWhenApertureOpensOneStopPair()
    {
        float c28 = PhysicalCameraResolver.CircleOfConfusionMm(
            focalLengthMm: 50f,
            fStop: 2.8f,
            subjectM: 4f,
            focusM: 8f
        );
        float c14 = PhysicalCameraResolver.CircleOfConfusionMm(
            focalLengthMm: 50f,
            fStop: 1.4f,
            subjectM: 4f,
            focusM: 8f
        );
        Assert.Equal(
            expected: 2f,
            actual: c14 / c28,
            precision: 3
        ); // halving the f-number doubles the CoC
    }

    [Fact]
    public void CircleOfConfusion_MonotonicInDepthError()
    {
        float near = PhysicalCameraResolver.CircleOfConfusionMm(
            focalLengthMm: 50f,
            fStop: 2.8f,
            subjectM: 10f,
            focusM: 8f
        );
        float mid = PhysicalCameraResolver.CircleOfConfusionMm(
            focalLengthMm: 50f,
            fStop: 2.8f,
            subjectM: 20f,
            focusM: 8f
        );
        float far = PhysicalCameraResolver.CircleOfConfusionMm(
            focalLengthMm: 50f,
            fStop: 2.8f,
            subjectM: 100f,
            focusM: 8f
        );
        Assert.True(
            condition: near < mid && mid < far,
            userMessage: "Background blur grows with distance behind the focus plane."
        );
    }

    [Fact]
    public void Ev100_MatchesApex()
    {
        // f/2.8, 1/50 s, ISO 100 ≈ EV 8.61.
        Assert.Equal(
            expected: 8.61f,
            actual: PhysicalCameraResolver.Ev100(fStop: 2.8f, shutterSeconds: 1f / 50f, iso: 100f),
            precision: 2
        );
    }

    [Fact]
    public void Ev100_OneStopAperture_AddsOneEv()
    {
        float baseline = PhysicalCameraResolver.Ev100(
            fStop: 2f,
            shutterSeconds: 1f / 50f,
            iso: 100f
        );
        float stopped = PhysicalCameraResolver.Ev100(
            fStop: 2f * MathF.Sqrt(2f),
            shutterSeconds: 1f / 50f,
            iso: 100f
        ); // exactly one stop
        Assert.Equal(expected: 1f, actual: stopped - baseline, precision: 3);
    }

    [Fact]
    public void Ev100_DoublingIso_DropsOneEv()
    {
        float iso100 = PhysicalCameraResolver.Ev100(
            fStop: 2.8f,
            shutterSeconds: 1f / 50f,
            iso: 100f
        );
        float iso200 = PhysicalCameraResolver.Ev100(
            fStop: 2.8f,
            shutterSeconds: 1f / 50f,
            iso: 200f
        );
        Assert.Equal(expected: -1f, actual: iso200 - iso100, precision: 4);
    }

    [Fact]
    public void ExposureMultiplier_BrighterEvMeansLessGain_AndClamps()
    {
        Assert.True(
            PhysicalCameraResolver.ExposureMultiplier(12f) <
            PhysicalCameraResolver.ExposureMultiplier(9f)
        );
        Assert.True(
            PhysicalCameraResolver.ExposureMultiplier(9f) <
            PhysicalCameraResolver.ExposureMultiplier(6f)
        );
        Assert.Equal(
            expected: 4f,
            actual: PhysicalCameraResolver.ExposureMultiplier(6f),
            precision: 3
        ); // clamps high
        Assert.Equal(
            expected: 0.1f,
            actual: PhysicalCameraResolver.ExposureMultiplier(20f),
            precision: 3
        ); // clamps low
    }

    [Fact]
    public void FilmStock_StrengthZeroEqualsNeutral()
    {
        var n = FilmStock.Neutral;
        var s = FilmStock.Of(kind: FilmStockKind.Kodak2383, strength: 0f);
        Assert.Equal(expected: n.Look, actual: s.Look);
        Assert.Equal(expected: n.Contrast, actual: s.Contrast, precision: 4);
        Assert.Equal(expected: n.Saturation, actual: s.Saturation, precision: 4);
        Assert.Equal(expected: n.WbTemperature, actual: s.WbTemperature, precision: 4);
        Assert.Equal(expected: n.Grain, actual: s.Grain, precision: 4);
        Assert.Equal(expected: n.Vignette, actual: s.Vignette, precision: 4);
    }

    [Fact]
    public void FilmStock_BlackAndWhite_DesaturatesFully() => Assert.Equal(
        expected: 0f,
        actual: FilmStock.Of(kind: FilmStockKind.Bw, strength: 1f).Saturation,
        precision: 5
    );

    [Fact]
    public void FilmStock_GoldenPrint_UsesGoldenLookAtFullStrength() => Assert.Equal(
        expected: 2,
        actual: FilmStock.Of(kind: FilmStockKind.Kodak2383, strength: 1f).Look
    );

    [Fact]
    public void Focus_ManualIsConstant()
    {
        var cam = new PhysicalCamera {
            Focus = new FocusSettings {
                Kind = FocusModeKind.Manual,
                ManualDistanceM = 5f,
                SpeedPerSec = 10f,
            },
        };
        Assert.Equal(
            expected: 5f,
            actual: PhysicalCameraResolver.ResolveFocusDistance(
                cam: cam,
                subjectDistanceM: 100f,
                dtSeconds: 1f / 60f
            ),
            precision: 4
        );
    }

    [Fact]
    public void Focus_ZeroSpeedIsInstant()
    {
        var cam = new PhysicalCamera {
            Focus = new FocusSettings {
                Kind = FocusModeKind.Center,
                SpeedPerSec = 0f,
            },
        };
        Assert.Equal(
            expected: 10f,
            actual: PhysicalCameraResolver.ResolveFocusDistance(
                cam: cam,
                subjectDistanceM: 10f,
                dtSeconds: 1f / 60f
            ),
            precision: 4
        );
    }

    [Fact]
    public void Focus_ExponentialApproachReachesTarget()
    {
        var cam = new PhysicalCamera {
            CurrentFocusDistanceM = 2f,
            Focus = new FocusSettings {
                Kind = FocusModeKind.Center,
                SpeedPerSec = 4f,
            },
        };
        for (int i = 0; i < 600; i++)
        {
            cam.CurrentFocusDistanceM =
                PhysicalCameraResolver.ResolveFocusDistance(
                    cam: cam,
                    subjectDistanceM: 10f,
                    dtSeconds: 1f / 60f
                );
        }

        Assert.Equal(expected: 10f, actual: cam.CurrentFocusDistanceM, precision: 2);
    }

    [Fact]
    public void Resolve_FillsProjectionAndDof()
    {
        var cam = new PhysicalCamera {
            Enabled = true,
            Sensor = SensorFormat.Of(SensorPreset.FullFrame),
            Lens = new Lens {
                FocalLengthMm = 35f,
                FStop = 1.4f,
                Anamorphic = 1f,
            },
            Focus = new FocusSettings {
                Kind = FocusModeKind.Manual,
                ManualDistanceM = 3f,
            },
        };
        var grade = PhysicalCameraResolver.Resolve(
            cam: cam,
            subjectDistanceM: 3f,
            viewportHeightPx: 1080f,
            dtSeconds: 1f / 60f
        );

        Assert.Equal(
            expected: PhysicalCameraResolver.VerticalFov(focalLengthMm: 35f, sensorHeightMm: 24f),
            actual: grade.FovYRadians,
            precision: 4
        );
        Assert.True(grade.DofEnabled);
        Assert.Equal(expected: 3f, actual: grade.DofFocusDistance, precision: 3);
        Assert.Equal(expected: 1.4f, actual: grade.DofFStop, precision: 4);
        Assert.True(grade.DofMaxCoc > 0f);
    }

    [Fact]
    public void ApplyTo_OverridesOnlyOwnedKnobs()
    {
        var baseSettings = new ZgRenderSettings3D {
            AmbientIntensity = 0.6f,
            BloomIntensity = 0.45f,
            FogDensity = 0.3f,
            SsaoStrength = 0.5f,
            Exposure = 1.10f,
            Contrast = 0.34f,
            Saturation = 1.20f,
            DofEnabled = 0f,
        };

        var cam = new PhysicalCamera {
            Enabled = true,
            Lens = new Lens {
                FocalLengthMm = 50f,
                FStop = 1.8f,
                Anamorphic = 1f,
            },
            Body = new CameraBody {
                Iso = 100f,
                ShutterSpeed = 1f / 200f,
            },
            Focus = new FocusSettings {
                Kind = FocusModeKind.Manual,
                ManualDistanceM = 6f,
            },
            Film = FilmStock.Of(kind: FilmStockKind.Bw, strength: 1f),
        };
        var grade = PhysicalCameraResolver.Resolve(
            cam: cam,
            subjectDistanceM: 6f,
            viewportHeightPx: 1080f,
            dtSeconds: 1f / 60f
        );

        var merged = baseSettings;
        grade.ApplyTo(ref merged);

        // Owned knobs change.
        Assert.Equal(expected: 1f, actual: merged.DofEnabled);
        Assert.Equal(expected: 6f, actual: merged.DofFocusDistance, precision: 3);
        Assert.Equal(expected: 1.8f, actual: merged.DofFStop, precision: 4);
        Assert.Equal(expected: 0f, actual: merged.Saturation, precision: 5); // B&W
        Assert.NotEqual(expected: baseSettings.Exposure, actual: merged.Exposure);
        // Film-look knobs (exposed over the ABI in Phase 4) now apply too.
        Assert.Equal(
            expected: FilmStock.Of(kind: FilmStockKind.Bw, strength: 1f).Look,
            actual: merged.AgxLook,
            precision: 5
        );
        Assert.Equal(
            expected: FilmStock.Of(kind: FilmStockKind.Bw, strength: 1f).Vignette,
            actual: merged.VignetteStrength,
            precision: 5
        );
        Assert.Equal(
            expected: FilmStock.Of(kind: FilmStockKind.Bw, strength: 1f).Grain,
            actual: merged.GrainAmount,
            precision: 5
        );

        // Un-owned knobs pass through untouched.
        Assert.Equal(expected: baseSettings.AmbientIntensity, actual: merged.AmbientIntensity);
        Assert.Equal(expected: baseSettings.BloomIntensity, actual: merged.BloomIntensity);
        Assert.Equal(expected: baseSettings.FogDensity, actual: merged.FogDensity);
        Assert.Equal(expected: baseSettings.SsaoStrength, actual: merged.SsaoStrength);
    }

    [Fact]
    public void ApplyTo_RespectsAffectFlags()
    {
        var baseSettings = new ZgRenderSettings3D {
            DofEnabled = 0f,
            Exposure = 1.1f,
            Saturation = 1.2f,
        };
        var cam = new PhysicalCamera {
            Enabled = true,
            AffectDof = false,
            AffectExposure = false,
            AffectGrade = false,
            Lens = Lens.Default,
            Body = CameraBody.Default,
            Focus = new FocusSettings {
                Kind = FocusModeKind.Manual,
                ManualDistanceM = 6f,
            },
            Film = FilmStock.Of(kind: FilmStockKind.Bw, strength: 1f),
        };
        var grade = PhysicalCameraResolver.Resolve(
            cam: cam,
            subjectDistanceM: 6f,
            viewportHeightPx: 1080f,
            dtSeconds: 1f / 60f
        );

        var merged = baseSettings;
        grade.ApplyTo(ref merged);

        Assert.Equal(expected: 0f, actual: merged.DofEnabled);
        Assert.Equal(expected: 1.1f, actual: merged.Exposure, precision: 5);
        Assert.Equal(expected: 1.2f, actual: merged.Saturation, precision: 5);
    }
}
