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
        Assert.Equal(26.99f, PhysicalCameraResolver.VerticalFov(50f, 24f) * Deg, 2);
        // Wider lens → wider FOV.
        Assert.Equal(53.13f, PhysicalCameraResolver.VerticalFov(24f, 24f) * Deg, 2);
        // Telephoto → narrow FOV.
        Assert.Equal(6.87f, PhysicalCameraResolver.VerticalFov(200f, 24f) * Deg, 2);
    }

    [Fact]
    public void CropFactor_NarrowsFovForSameFocal()
    {
        var ff = PhysicalCameraResolver.VerticalFov(
            50f,
            SensorFormat.Of(SensorPreset.FullFrame).HeightMm
        );
        var apsc = PhysicalCameraResolver.VerticalFov(
            50f,
            SensorFormat.Of(SensorPreset.ApsC).HeightMm
        );
        Assert.True(
            apsc < ff,
            "A smaller sensor crops to a narrower FOV at the same focal length."
        );
        Assert.True(SensorFormat.Of(SensorPreset.ApsC).CropFactor > 1.4f);
    }

    [Fact]
    public void CircleOfConfusion_ZeroAtFocusPlane()
    {
        Assert.Equal(
            0f,
            PhysicalCameraResolver.CircleOfConfusionMm(
                50f,
                2.8f,
                8f,
                8f
            ),
            5
        );
    }

    [Fact]
    public void CircleOfConfusion_DoublesWhenApertureOpensOneStopPair()
    {
        var c28 = PhysicalCameraResolver.CircleOfConfusionMm(
            50f,
            2.8f,
            4f,
            8f
        );
        var c14 = PhysicalCameraResolver.CircleOfConfusionMm(
            50f,
            1.4f,
            4f,
            8f
        );
        Assert.Equal(2f, c14 / c28, 3); // halving the f-number doubles the CoC
    }

    [Fact]
    public void CircleOfConfusion_MonotonicInDepthError()
    {
        var near = PhysicalCameraResolver.CircleOfConfusionMm(
            50f,
            2.8f,
            10f,
            8f
        );
        var mid = PhysicalCameraResolver.CircleOfConfusionMm(
            50f,
            2.8f,
            20f,
            8f
        );
        var far = PhysicalCameraResolver.CircleOfConfusionMm(
            50f,
            2.8f,
            100f,
            8f
        );
        Assert.True(
            near < mid && mid < far,
            "Background blur grows with distance behind the focus plane."
        );
    }

    [Fact]
    public void Ev100_MatchesApex()
    {
        // f/2.8, 1/50 s, ISO 100 ≈ EV 8.61.
        Assert.Equal(8.61f, PhysicalCameraResolver.Ev100(2.8f, 1f / 50f, 100f), 2);
    }

    [Fact]
    public void Ev100_OneStopAperture_AddsOneEv()
    {
        var baseline = PhysicalCameraResolver.Ev100(2f, 1f / 50f, 100f);
        var stopped = PhysicalCameraResolver.Ev100(
            2f * MathF.Sqrt(2f),
            1f / 50f,
            100f
        ); // exactly one stop
        Assert.Equal(1f, stopped - baseline, 3);
    }

    [Fact]
    public void Ev100_DoublingIso_DropsOneEv()
    {
        var iso100 = PhysicalCameraResolver.Ev100(2.8f, 1f / 50f, 100f);
        var iso200 = PhysicalCameraResolver.Ev100(2.8f, 1f / 50f, 200f);
        Assert.Equal(-1f, iso200 - iso100, 4);
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
        Assert.Equal(4f, PhysicalCameraResolver.ExposureMultiplier(6f), 3); // clamps high
        Assert.Equal(0.1f, PhysicalCameraResolver.ExposureMultiplier(20f), 3); // clamps low
    }

    [Fact]
    public void FilmStock_StrengthZeroEqualsNeutral()
    {
        var n = FilmStock.Neutral;
        var s = FilmStock.Of(FilmStockKind.Kodak2383, 0f);
        Assert.Equal(n.Look, s.Look);
        Assert.Equal(n.Contrast, s.Contrast, 4);
        Assert.Equal(n.Saturation, s.Saturation, 4);
        Assert.Equal(n.WbTemperature, s.WbTemperature, 4);
        Assert.Equal(n.Grain, s.Grain, 4);
        Assert.Equal(n.Vignette, s.Vignette, 4);
    }

    [Fact]
    public void FilmStock_BlackAndWhite_DesaturatesFully()
    {
        Assert.Equal(0f, FilmStock.Of(FilmStockKind.Bw, 1f).Saturation, 5);
    }

    [Fact]
    public void FilmStock_GoldenPrint_UsesGoldenLookAtFullStrength()
    {
        Assert.Equal(2, FilmStock.Of(FilmStockKind.Kodak2383, 1f).Look);
    }

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
        Assert.Equal(5f, PhysicalCameraResolver.ResolveFocusDistance(cam, 100f, 1f / 60f), 4);
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
        Assert.Equal(10f, PhysicalCameraResolver.ResolveFocusDistance(cam, 10f, 1f / 60f), 4);
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
        for (var i = 0; i < 600; i++)
            cam.CurrentFocusDistanceM =
                PhysicalCameraResolver.ResolveFocusDistance(cam, 10f, 1f / 60f);
        Assert.Equal(10f, cam.CurrentFocusDistanceM, 2);
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
            cam,
            3f,
            1080f,
            1f / 60f
        );

        Assert.Equal(PhysicalCameraResolver.VerticalFov(35f, 24f), grade.FovYRadians, 4);
        Assert.True(grade.DofEnabled);
        Assert.Equal(3f, grade.DofFocusDistance, 3);
        Assert.Equal(1.4f, grade.DofFStop, 4);
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
            Film = FilmStock.Of(FilmStockKind.Bw, 1f),
        };
        var grade = PhysicalCameraResolver.Resolve(
            cam,
            6f,
            1080f,
            1f / 60f
        );

        var merged = baseSettings;
        grade.ApplyTo(ref merged);

        // Owned knobs change.
        Assert.Equal(1f, merged.DofEnabled);
        Assert.Equal(6f, merged.DofFocusDistance, 3);
        Assert.Equal(1.8f, merged.DofFStop, 4);
        Assert.Equal(0f, merged.Saturation, 5); // B&W
        Assert.NotEqual(baseSettings.Exposure, merged.Exposure);
        // Film-look knobs (exposed over the ABI in Phase 4) now apply too.
        Assert.Equal(FilmStock.Of(FilmStockKind.Bw, 1f).Look, merged.AgxLook, 5);
        Assert.Equal(FilmStock.Of(FilmStockKind.Bw, 1f).Vignette, merged.VignetteStrength, 5);
        Assert.Equal(FilmStock.Of(FilmStockKind.Bw, 1f).Grain, merged.GrainAmount, 5);

        // Un-owned knobs pass through untouched.
        Assert.Equal(baseSettings.AmbientIntensity, merged.AmbientIntensity);
        Assert.Equal(baseSettings.BloomIntensity, merged.BloomIntensity);
        Assert.Equal(baseSettings.FogDensity, merged.FogDensity);
        Assert.Equal(baseSettings.SsaoStrength, merged.SsaoStrength);
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
            Film = FilmStock.Of(FilmStockKind.Bw, 1f),
        };
        var grade = PhysicalCameraResolver.Resolve(
            cam,
            6f,
            1080f,
            1f / 60f
        );

        var merged = baseSettings;
        grade.ApplyTo(ref merged);

        Assert.Equal(0f, merged.DofEnabled);
        Assert.Equal(1.1f, merged.Exposure, 5);
        Assert.Equal(1.2f, merged.Saturation, 5);
    }
}