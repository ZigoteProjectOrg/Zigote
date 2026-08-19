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

`CameraPlugin.GetDevicesAsync()` enumerates cameras; pass an id to `StartAsync` to switch.
`camera.State` / `camera.Error` are signals — bind a status line to them and it stays right.

The head must declare the platform permission: `android.permission.CAMERA` in the Android
manifest, `NSCameraUsageDescription` in the iOS Info.plist.

## How the pieces fit

| Path | What it is |
|---|---|
| `Camera/CameraController.cs` | Session lifecycle, frame mailbox → texture upload, the GPU photo pipeline. |
| `Camera/CameraLut.cs` | `.cube` parser, LUT strip texture, and the `LutEffect` grading shader. |
| `Camera/CameraView.cs` | The preview widget: ticks the controller, paints the texture, applies the LUT. |
| `Camera/Platforms/` | One `CameraDriver` per target framework; the csproj compiles exactly one. |
| `Camera.Tests/` | Mailbox contract, `.cube` parsing, ffmpeg args, device-list parsers, fit math. |

The GPU pipeline uses two engine seams that are general, not camera-specific:
`PaintList.AddShaderEffect(..., imageKey:)` binds any app-owned texture to a custom WGSL
shader at `@group(1)`, and `ZigoteEngine.ReadRenderTexturePixels` pulls processed pixels
back out of a render texture — compose them for any image-processing pass, not just LUTs.

Known ceilings (deliberate): photos are preview-stream resolution (`maxHeight: 0` for the
device's native size; a sensor-resolution still path is the upgrade); the front camera is
not mirrored; macOS capture is pinned to 720p30; GPU-LUT photos need a mounted `CameraView`
(or call `controller.PaintPhotoPass` from your own painter).

## Ship it

```
dotnet pack Camera        # multi-targets; pack on macOS to include the iOS build
```
