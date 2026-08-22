# Camera

Cross-platform camera capture for [Zigote](https://github.com/zigote): desktop
(Windows/macOS/Linux via ffmpeg), Android (camera2), iOS (AVFoundation).

- **Preview into a GPU texture** — capture threads hand over only the newest frame
  (latest-wins mailbox, recycled buffers), the app thread uploads it once per frame, and
  `CameraView` paints it. No frame queues, no per-frame allocation in steady state.
- **Frames stay raw; color is a GPU concern.** A `.cube` 3D LUT loads into a strip texture
  and grades at draw time through a registered shader effect (`LutEffect`) — over the live
  preview, and through the offscreen photo pipeline, identically.
- **Photos ride the same pipeline.** `TakePhotoAsync(lut: …)` renders frame texture → LUT
  pass → render texture, reads the processed pixels back, and encodes a JPEG. Without a LUT
  it encodes the raw frame. What you see is exactly what you save.
- **Manual controls, where the device has them** — ISO, shutter, EV, white balance
  (temperature + tint), manual focus, AE/AF regions and locks, driven as signals and
  applied as one coalesced snapshot. `Capabilities` says what the *current lens* can do, so
  a UI draws a dial only when there is something behind it; `Metadata` reports what the
  sensor actually did, frame by frame.
- **DSLR-style minimal processing** — `StartAsync(minimalProcessing: true)` turns off the
  vendor look where the platform allows it (Android: noise reduction, edge enhancement,
  stabilization off; desktop capture is already the driver's untouched output).

## Try it

```
cd example && dotnet run --project CameraExample
```

Desktop capture needs `ffmpeg` (and `ffprobe`) on `PATH` — the same binaries
`Zigote.Videoplayer` uses, same `ZIGOTE_FFMPEG`/`ZIGOTE_FFPROBE` overrides.

## Use it in an app

```csharp
PluginHost.Register(new CameraPlugin());   // before the App: wires lifecycle + permissions
```

```csharp
var camera = new CameraController();
if (await CameraPlugin.RequestPermissionAsync())
    await camera.StartAsync(minimalProcessing: true);   // default device, 720p preview

var lut = CameraLut.Load("looks/kodak-2383.cube");
var view = new CameraView(camera) { Lut = lut, LutStrength = 0.8f };

byte[] jpeg = await camera.TakePhotoAsync(lut: lut);    // GPU-graded, same look as the view
```

```csharp
var caps = camera.Capabilities.Value;              // per lens, probed at open
if (caps.Iso.Supported) camera.Controls.Iso.Value = 400;
if (caps.Shutter.Supported) camera.Controls.ShutterNs.Value = 4_000_000;   // 1/250
camera.Controls.WhiteBalanceKelvin.Value = 5200;   // 0 = auto
camera.Controls.FocusDiopters.Value = float.NaN;   // NaN = autofocus
camera.Controls.ResetToAuto();                     // hand it all back

var shot = camera.Metadata.Value;                  // what the sensor actually did
string readout = $"ISO {shot?.Iso} · {shot?.ShutterLabel}";
```

Which dials are held decides the mode — hold ISO for ISO priority, both for Manual — so
`Controls.Mode` is derived and can never disagree with the dials. Setting a control the lens
cannot do is not an error; it is clamped, or dropped back to auto, when the session opens.

`CameraPlugin.GetDevicesAsync()` enumerates cameras; pass an id to `StartAsync` to switch.
`camera.State` / `camera.Error` are signals — bind a status line to them and it stays right.

The head must declare the platform permission: `android.permission.CAMERA` in the Android
manifest, `NSCameraUsageDescription` in the iOS Info.plist.

## How the pieces fit

| Path | What it is |
|---|---|
| `Camera/CameraController.cs` | Session lifecycle, frame mailbox → texture upload, the GPU photo pipeline. |
| `Camera/CameraControls.cs` | Controls, capabilities, per-frame metadata, and the Kelvin→gains table. |
| `Camera/CameraLut.cs` | `.cube` parser, LUT strip texture, and the `LutEffect` grading shader. |
| `Camera/CameraView.cs` | The preview widget: ticks the controller, paints the texture, applies the LUT. |
| `Camera/Platforms/` | One `CameraDriver` per target framework; the csproj compiles exactly one. |
| `Camera.Tests/` | Mailbox contract, `.cube` parsing, ffmpeg args, device-list parsers, fit math. |

The GPU pipeline uses two engine seams that are general, not camera-specific:
`PaintList.AddShaderEffect(..., imageKey:)` binds any app-owned texture to a custom WGSL
shader at `@group(1)`, and `ZigoteEngine.ReadRenderTexturePixels` pulls processed pixels
back out of a render texture — compose them for any image-processing pass, not just LUTs.

Manual-control ceilings: an auto-ISO ceiling has no camera2 key and is only honoured on the
manual path; a shutter floor for device-chosen exposures goes through the AE fps range, which is
the only lever camera2 offers. Desktop capture reports `CameraCapabilities.None` — v4l2 /
avfoundation / dshow hand over whatever the driver decided, and there is nothing to turn.

Known ceilings (deliberate): photos are preview-stream resolution (`maxHeight: 0` for the
device's native size; a sensor-resolution still path is the upgrade); the front camera is
not mirrored; macOS capture is pinned to 720p30; GPU-LUT photos need a mounted `CameraView`
(or call `controller.PaintPhotoPass` from your own painter).

## Ship it

```
dotnet pack Camera        # multi-targets; pack on macOS to include the iOS build
```
