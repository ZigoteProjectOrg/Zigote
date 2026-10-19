# Native Interop

The native half of Zigote is `libzigote`, a Zig library exposing a flat C ABI: a few hundred `zigote_*` functions. It owns everything that touches the OS or GPU: windows and input (SDL3), rendering (wgpu), text (HarfBuzz and FreeType), physics (Jolt), ECS (flecs), audio (miniaudio), dialogs, clipboard and window chrome.

C# never declares P/Invokes by hand. A Roslyn **source generator** (`Zigote.Generators/ZigoteBindingGenerator`) reads the `export fn` declarations in `Zigote.Engine/src/ffi/*.zig`, along with the enums in `src/abi.zig`, and emits `NativeEngine` at compile time. Change a Zig signature and the C# binding changes with the next build.

## One call, end to end

The Zig export:

```zig
export fn zigote_get_clipboard(buf: [*]u8, capacity: u32) u32 {
    const text = sdl3.clipboard.getText() catch return 0;
    defer sdl3.free(text);
    // copy up to capacity-1 bytes, NUL-terminate, return the FULL length
    ...
}
```

The generated binding, which you never edit:

```csharp
[DllImport("zigote")] internal static extern uint GetClipboard(byte* buf, uint capacity);
```

The public wrapper in `ZigoteEngine` turns it into idiomatic C#. It uses a stack buffer for the common case and a second, heap-sized call when the text is larger:

```csharp
public string GetClipboard()
{
    byte* stackBuf = stackalloc byte[8192];
    uint len = NativeEngine.GetClipboard(stackBuf, 8192);
    ...
}
```

A widget reaches the engine through its `Owner`, the `App` it is mounted in:

```csharp run height=260
class Demo : ComposedWidget
{
    private readonly Signal<string> _clip = new("(press Read)");
    private readonly Signal<string> _info = new("");

    protected override void OnMount()
    {
        if (Owner is not { } app) return;
        _info.Value = $"System theme: {app.Engine.GetSystemTheme()}   ·   OS: {Environment.OSVersion.Platform}";
    }

    protected override Widget Build(BuildContext context) =>
        new Padding(EdgeInsets.All(20), new Column(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Start, children: [
            new Watch(() => new Label(_info.Value, fontSize: 13)),
            new Row(spacing: 8, children: [
                new AdwButton(label: "Copy a greeting", onPressed: () => Owner?.Engine.SetClipboard($"Hello from Zigote at {DateTime.Now:T}")),
                new AdwButton(label: "Read clipboard", onPressed: () => _clip.Value = Owner?.Engine.GetClipboard() ?? "") { Style = AdwButtonStyle.Suggested },
            ]),
            new Watch(() => new Label($"Clipboard: {_clip.Value}", fontSize: 15) { MaxLines = 3 }),
        ]));
}
```

## The contract

- **One engine per process**; no handle is threaded through every call.
- Fallible exports return a `ZgStatus` (0 = ok) and pass results through out-parameters. The C# wrapper turns failures into exceptions.
- Resources are **generational handles** (`index + generation`), so a stale handle fails cleanly instead of pointing at reused memory.
- Strings go in as UTF-8 spans. Strings and blobs come out through caller buffers, `(buf, capacity) → length`, as the clipboard call above shows.
- Most calls run on the UI thread. Audio runs miniaudio's own callback thread; nothing else in the engine spawns threads behind your back.

## Adding a native function

1. Add `export fn zigote_thing(...) callconv(.c) ...` to `src/ffi/root.zig` (or the subsystem's own file under `src/ffi/`).
2. Build. The generator emits `NativeEngine.Thing(...)`.
3. Wrap it in `ZigoteEngine` with a C#-shaped API. Application code never calls `NativeEngine` directly.
4. If you touched a struct in `src/abi.zig`, keep its `comptime` offset asserts in step and bump the ABI version.

Native changes need a process restart: hot reload covers C#, not a rebuilt dylib.

## Native views

For things only the OS can draw (a web view, a map, a video surface), `NativeViewHost` reserves space in the layout and keeps a platform child view (`NSView`, `HWND`, X11 window, Android `View`, `UIView`) positioned over it. Native views composite **on top** of everything Zigote draws, so keep popups beside them. The WebView plugin in `plugins/WebView` is the reference implementation. On Wayland it switches to offscreen rendering into an engine texture instead.

## Platform services

| Need | API |
|---|---|
| Open/save dialogs | `FileDialog.OpenFileAsync(...)`: native panels on macOS, SDL3 elsewhere, an in-app browser as fallback |
| Clipboard | `App.Engine.GetClipboard()` / `SetClipboard(text)`, plus typed data |
| Drag and drop | Drops arrive as events; `CanAcceptDrop`/`OnDrop` on widgets; drag-out on macOS |
| Appearance | `AdwaitaApp` follows light/dark and accent; `App.SystemThemeChanged` |
| Menus | One `AppMenu` model: the native macOS menu bar (`NativeMenuBar`), an in-window `MenuBar` elsewhere; `AdwMenuButton` popovers in-app |
| Mobile | Touch, lifecycle (`AppLifecycleState`), safe-area insets, on-screen keyboard (see `docs/mobile.md`) |
