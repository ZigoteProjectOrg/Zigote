using System.Globalization;
using Camera;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace CameraExample;

/// <summary>
///     The plugin end to end: permission, a full-bleed DSLR-neutral preview, a GPU LUT toggle,
///     camera switching, and a shutter whose JPEG goes through the same GPU grade as the preview.
/// </summary>
public sealed class CameraExampleApp : MaterialApp
{
    public CameraExampleApp() : base(
        home: new CameraPage(),
        title: "Camera example",
        theme: ThemeData.Dark
    )
    {
        Width = 420;
        Height = 720;
    }
}

internal sealed class CameraPage : ComposedWidget
{
    private readonly CameraController _camera = new();
    private readonly CameraLut _teal = CameraLut.Parse(TealAndOrange());
    private readonly Text _status = new("starting…");
    private readonly Text _exposure = new("");
    private CameraView? _view;
    private CameraDeviceInfo[] _devices = [];
    private int _deviceIndex;

    protected override void OnMount()
    {
        // Errors can land on a capture thread; widget mutation belongs on the UI thread.
        Own(_camera.State.Observe(() => OnUi(() => _status.Text = Describe())));
        Own(_camera.Error.Observe(() => OnUi(() => _status.Text = Describe())));
        // What the sensor actually did, frame by frame — the only way to read the exposure while
        // the device is metering it, and how you see a manual value being clamped.
        Own(_camera.Metadata.Observe(() => OnUi(() => _exposure.Text = DescribeExposure())));
        Own(_camera.Capabilities.Observe(() => OnUi(() => _exposure.Text = DescribeExposure())));
        _ = StartAsync();
    }

    private void OnUi(Action action)
    {
        if (Zigote.UI.Host.App.Active is { } app) app.Post(action);
        else action();
    }

    protected override void OnUnmount() => _camera.Dispose();

    private async Task StartAsync()
    {
        if (!await CameraPlugin.RequestPermissionAsync())
        {
            _status.Text = "camera permission denied";
            return;
        }

        _devices = await CameraPlugin.GetDevicesAsync();
        // DSLR-flat frames: the LUT is the look, not the vendor pipeline.
        await _camera.StartAsync(
            deviceId: _devices.FirstOrDefault()?.Id,
            minimalProcessing: true
        );
    }

    protected override Widget Build(BuildContext context)
    {
        _view ??= new CameraView(_camera);

        return new SafeArea(new Scaffold(
            new AppBar(new Text("Camera"), centerTitle: true),
            new Column(children:
                [
                    new Expanded(_view),
                    new Padding(
                        padding: EdgeInsets.All(12),
                        child: new Column(children:
                            [
                                _status,
                                _exposure,
                                new SizedBox(height: 8),
                                new Row(
                                    mainAxisAlignment: MainAxisAlignment.Center,
                                    children:
                                    [
                                        new OutlinedButton(new Text("LUT"), ToggleLut),
                                        new SizedBox(width: 8),
                                        new OutlinedButton(new Text("Manual"), ToggleManual),
                                        new SizedBox(width: 8),
                                        new FilledButton(new Text("Shoot"), () => _ = ShootAsync()),
                                        new SizedBox(width: 8),
                                        new OutlinedButton(new Text("Switch"), () => _ = SwitchAsync()),
                                    ]
                                ),
                            ]
                        )
                    ),
                ]
            )
        ));
    }

    /// <summary>
    ///     Hand the exposure back and forth. Manual picks the middle of what this lens reports —
    ///     the point is the round trip, not the values — and Auto is a single reset, because
    ///     "give it back to the camera" is a thing photographers do constantly.
    /// </summary>
    private void ToggleManual()
    {
        var caps = _camera.Capabilities.Value;
        if (!caps.Iso.Supported || !caps.Shutter.Supported)
        {
            _status.Text = "this camera has no manual exposure";
            return;
        }

        if (_camera.Controls.Mode is not ExposureMode.Auto)
        {
            _camera.Controls.ResetToAuto();
            return;
        }

        _camera.Controls.Iso.Value = (caps.Iso.Min + caps.Iso.Max) / 2;
        _camera.Controls.ShutterNs.Value = caps.Shutter.Clamp(4_000_000); // 1/250
    }

    /// <summary>The viewfinder readout: what was asked for, and what the sensor actually gave.</summary>
    private string DescribeExposure()
    {
        var caps = _camera.Capabilities.Value;
        if (!caps.AnyManual) return "no manual controls on this camera";

        var meta = _camera.Metadata.Value;
        string actual = meta is null
            ? "…"
            : $"ISO {meta.Iso} · {meta.ShutterLabel}{(meta.AeConverged ? "" : " (metering)")}";
        return $"{_camera.Controls.Mode} · {actual}";
    }

    private void ToggleLut()
    {
        _view!.Lut = _view.Lut is null ? _teal : null;
        _status.Text = _view.Lut is null ? "LUT off — raw preview" : "LUT on — GPU graded";
    }

    private async Task ShootAsync()
    {
        try
        {
            // With a LUT active the photo rides the GPU pipeline: frame texture → LUT pass →
            // offscreen target → readback → JPEG. Without one it is the raw frame, encoded.
            byte[] jpeg = await _camera.TakePhotoAsync(lut: _view!.Lut);
            string path = Path.Combine(
                path1: Path.GetTempPath(),
                path2: $"zigote-photo-{DateTime.Now:HHmmss}.jpg"
            );
            await File.WriteAllBytesAsync(path: path, bytes: jpeg);
            _status.Text = $"saved {path} ({jpeg.Length / 1024} KB)";
        }
        catch (Exception ex)
        {
            _status.Text = $"photo failed: {ex.Message}";
        }
    }

    private async Task SwitchAsync()
    {
        if (_devices.Length < 2) return;
        _deviceIndex = (_deviceIndex + 1) % _devices.Length;
        await _camera.StartAsync(deviceId: _devices[_deviceIndex].Id, minimalProcessing: true);
        _status.Text = $"camera: {_devices[_deviceIndex].Name}";
    }

    private string Describe() => _camera.State.Value switch {
        CameraState.Failed => $"failed: {_camera.Error.Value}",
        CameraState.Streaming => $"{_camera.FrameSize.Width}×{_camera.FrameSize.Height} streaming",
        var s => s.ToString().ToLowerInvariant(),
    };

    /// <summary>A generated teal-and-orange grade, so the demo needs no .cube file on disk.</summary>
    private static IEnumerable<string> TealAndOrange()
    {
        const int n = 17;
        yield return "LUT_3D_SIZE " + n;
        for (int b = 0; b < n; b++)
        for (int g = 0; g < n; g++)
        for (int r = 0; r < n; r++)
        {
            float rf = r / (n - 1f), gf = g / (n - 1f), bf = b / (n - 1f);
            float luma = (0.299f * rf) + (0.587f * gf) + (0.114f * bf);
            // Shadows toward teal, highlights toward orange, with a gentle s-curve on contrast.
            float warm = (luma - 0.5f) * 0.35f;
            float outR = Curve(rf + warm);
            float outG = Curve(gf + (warm * 0.25f));
            float outB = Curve(bf - warm);
            yield return string.Create(
                provider: CultureInfo.InvariantCulture,
                $"{outR:0.####} {outG:0.####} {outB:0.####}"
            );
        }

        static float Curve(float v)
        {
            v = Math.Clamp(value: v, min: 0f, max: 1f);
            return (v * v * (3f - (2f * v)) * 0.6f) + (v * 0.4f); // 60% smoothstep, 40% linear
        }
    }
}
