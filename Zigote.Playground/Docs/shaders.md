# Custom Shaders

When `PaintList`'s built-in commands aren't enough, write the pixels yourself in **WGSL**, the shading language of WebGPU, which Zigote's wgpu renderer runs on Metal, Vulkan, Direct3D 12 and GL. A custom shader is a **shader effect**: a quad the renderer draws with your fragment shader, with read access to everything painted beneath it.

1. Register WGSL once: `ZigoteEngine.RegisterShader(id, wgsl)`.
2. Draw it from a widget's `Paint`: `paint.AddShaderEffect(bounds, id, p0 … p7)`.

## The contract

Your WGSL provides `vs_main` and `fs_main` with exactly this interface:

```wgsl
@group(0) @binding(0) var backdrop: texture_2d<f32>;     // everything painted before this quad
@group(0) @binding(1) var backdrop_sampler: sampler;

struct VertexOut {
  @builtin(position) position: vec4<f32>,                // in fs_main: the pixel, in DEVICE pixels
  @location(0) uv: vec2<f32>,                            // where this pixel lies in the backdrop
  @location(1) params_a: vec4<f32>,                      // p0..p3 from AddShaderEffect
  @location(2) params_b: vec4<f32>,                      // p4..p7
};

@vertex
fn vs_main(@location(0) position: vec2<f32>, @location(1) uv: vec2<f32>,
           @location(2) params_a: vec4<f32>, @location(3) params_b: vec4<f32>) -> VertexOut {
  var out: VertexOut;
  out.position = vec4<f32>(position, 0.0, 1.0);          // already in clip space
  out.uv = uv; out.params_a = params_a; out.params_b = params_b;
  return out;
}
```

Facts to design around:

- **Eight float parameters** reach the shader, `p0..p7`, as `params_a.xyzw` and `params_b.xyzw`. Use them for time, sizes, colours and strengths.
- **Coordinates are device pixels.** `in.position.xy` is the physical pixel. To know where the quad is, pass its rectangle as parameters, multiplied by the display's pixel ratio and including any translation (`paint.CurrentTranslation`) from scrolling.
- **The backdrop is a snapshot** of the frame painted so far. Sampling it at `in.uv` returns what's under the pixel, which is how blurs, distortions and colour filters work.
- **No clip stack.** The effect isn't clipped by the frame's rounded clips; round your own corners with an SDF (see `BackdropBlur.cs` for the exact form).
- **Ids are global.** Pick a distinctive 32-bit id; re-registering an id replaces the shader. Built-ins use ids like `0x7AA27B10`, so don't reuse those.
- **Register on the UI thread, after the engine is up**: the first paint, not a constructor.
- `RegisterShader` returns `false` for invalid WGSL, and `ZigoteEngine.LastShaderError` holds the compiler's message.

## A generative shader

This shader ignores the backdrop and generates an animated plasma from time and position. Edit the WGSL (try changing `0.8` to `3.0`, or swap the colour channels) and press Run. A typo shows the compiler's error in place of the effect.

```csharp run height=320
sealed class ShaderView(float pixelRatio) : LeafWidget
{
    private const uint Id = 0x504C_0001;               // "PL" 1 — unique to this example
    private bool? _ready;
    private string _error = "";
    private float _time;
    private Size _size;

    private const string Wgsl = """
        @group(0) @binding(0) var backdrop: texture_2d<f32>;
        @group(0) @binding(1) var backdrop_sampler: sampler;

        struct VertexOut {
          @builtin(position) position: vec4<f32>,
          @location(0) uv: vec2<f32>,
          @location(1) params_a: vec4<f32>,
          @location(2) params_b: vec4<f32>,
        };

        @vertex
        fn vs_main(@location(0) position: vec2<f32>, @location(1) uv: vec2<f32>,
                   @location(2) params_a: vec4<f32>, @location(3) params_b: vec4<f32>) -> VertexOut {
          var out: VertexOut;
          out.position = vec4<f32>(position, 0.0, 1.0);
          out.uv = uv; out.params_a = params_a; out.params_b = params_b;
          return out;
        }

        @fragment
        fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
          let t = in.params_a.x;                                   // p0: seconds
          let rect = vec4<f32>(in.params_a.yzw, in.params_b.x);    // p1..p4: x, y, w, h in device px
          let p = (in.position.xy - rect.xy) / rect.w * 6.0;       // 0..6 across the quad
          let v = sin(p.x + t) + sin(p.y * 0.8 + t * 1.3) + sin((p.x + p.y) * 0.5 + t * 0.7)
                + sin(length(p - vec2<f32>(3.0 + sin(t), 2.0)) * 1.5);
          let c = vec3<f32>(0.5 + 0.5 * sin(v), 0.5 + 0.5 * sin(v + 2.1), 0.5 + 0.5 * sin(v + 4.2));
          return vec4<f32>(c, 1.0);
        }
        """;

    protected override void OnMount() => CreateTicker(dt => { _time += dt; MarkNeedsPaint(); }).Start();

    public override Size Measure(Constraints c) => _size = new Size(c.MaxWidth, c.MaxHeight);
    public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    public override void Paint(PaintList paint)
    {
        if (_ready is null)                             // first paint: the engine exists now
        {
            _ready = ZigoteEngine.RegisterShader(Id, Wgsl);
            _error = ZigoteEngine.LastShaderError ?? "";
        }
        if (_ready != true)
        {
            paint.AddText("WGSL error:", Bounds.X + 12, Bounds.Y + 24, new Color(0xFFE01B24), 14);
            // The message is multi-line, with a caret under the problem. Show the lines that matter.
            var lines = _error.Split('\n').Where(l => l.Contains("error") || l.Contains('│')).Take(6);
            float y = Bounds.Y + 48;
            foreach (string line in lines)
            {
                paint.AddText(line.TrimEnd(), Bounds.X + 12, y, new Color(0xFF888888), 12, fontFamily: AdwTypography.Monospace.FontFamily);
                y += 18;
            }
            return;
        }
        var (ox, oy) = paint.CurrentTranslation;        // scrolling shifts the quad; params must follow
        float d = pixelRatio;
        paint.AddShaderEffect(Bounds, Id,
            p0: _time,
            p1: (Bounds.X + ox) * d, p2: (Bounds.Y + oy) * d, p3: Bounds.Width * d, p4: Bounds.Height * d);
    }
}

class Demo : ComposedWidget
{
    // The pixel ratio comes from the media query, read in Build.
    protected override Widget Build(BuildContext context) =>
        new ShaderView(MediaQuery.Of(context).DevicePixelRatio);
}
```

## A backdrop filter

A shader effect drawn *after* other content can read and transform it. This one paints ordinary widgets, then runs a ripple and colour filter over them, with a slider controlling the strength through `p0`. The rule is: **paint the content first, then the effect over it.**

```csharp run height=360
sealed class Ripple(float pixelRatio) : Widget
{
    private const uint Id = 0x504C_0002;
    private bool? _ready;
    private float _time;
    public float Strength { get; set; } = 0.5f;
    public required Widget Child { get; init; }

    private const string Wgsl = """
        @group(0) @binding(0) var backdrop: texture_2d<f32>;
        @group(0) @binding(1) var backdrop_sampler: sampler;
        struct VertexOut {
          @builtin(position) position: vec4<f32>,
          @location(0) uv: vec2<f32>,
          @location(1) params_a: vec4<f32>,
          @location(2) params_b: vec4<f32>,
        };
        @vertex
        fn vs_main(@location(0) position: vec2<f32>, @location(1) uv: vec2<f32>,
                   @location(2) params_a: vec4<f32>, @location(3) params_b: vec4<f32>) -> VertexOut {
          var out: VertexOut;
          out.position = vec4<f32>(position, 0.0, 1.0);
          out.uv = uv; out.params_a = params_a; out.params_b = params_b;
          return out;
        }
        @fragment
        fn fs_main(in: VertexOut) -> @location(0) vec4<f32> {
          let strength = in.params_a.x;                          // p0: 0..1 from the slider
          let t = in.params_a.y;                                 // p1: seconds
          let center = vec2<f32>(in.params_a.z, in.params_a.w);  // p2,p3: centre in device px
          let d = distance(in.position.xy, center);
          let wave = sin(d * 0.06 - t * 4.0) * strength * 10.0;  // ripple displacement in px
          let dir = normalize(in.position.xy - center + vec2<f32>(0.001));
          let px = 1.0 / vec2<f32>(textureDimensions(backdrop));
          let c = textureSampleLevel(backdrop, backdrop_sampler, in.uv + dir * wave * px, 0.0);
          let grey = dot(c.rgb, vec3<f32>(0.299, 0.587, 0.114));
          let tinted = mix(c.rgb, vec3<f32>(grey * 0.6, grey * 0.9, grey * 1.2), strength);
          return vec4<f32>(tinted, 1.0);
        }
        """;

    protected override void OnMount() => CreateTicker(dt => { _time += dt; MarkNeedsPaint(); }).Start();
    public override IEnumerable<Widget> GetChildren() => [Child];
    public override Size Measure(Constraints c) => Child.Measure(c);
    public override void Layout(Offset origin)
    {
        Child.Layout(origin);
        Bounds = Child.Bounds;
    }
    public override Widget? HitTest(Offset p) => Child.HitTest(p);

    public override void Paint(PaintList paint)
    {
        Child.Paint(paint);                             // 1. the content
        _ready ??= ZigoteEngine.RegisterShader(Id, Wgsl);
        if (_ready != true || Strength <= 0.001f) return;
        var (ox, oy) = paint.CurrentTranslation;
        float d = pixelRatio;
        paint.AddShaderEffect(Bounds, Id,               // 2. the effect over it
            p0: Strength, p1: _time,
            p2: (Bounds.X + ox + Bounds.Width / 2) * d, p3: (Bounds.Y + oy + Bounds.Height / 2) * d);
    }
}

class Demo : ComposedWidget
{
    private Ripple? _ripple;

    protected override Widget Build(BuildContext context)
    {
        var p = AdwPalette.For(ThemeProvider.Of(context));
        _ripple ??= new Ripple(MediaQuery.Of(context).DevicePixelRatio) {
            Child = new DecoratedBox {
                Fill = p.ViewBg,
                Child = new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
                    new Label(text: "Shaders see what you painted", style: AdwTypography.Title2),
                    new Wrap(spacing: 8, runSpacing: 8, children: [
                        .. new uint[] { 0xFF3584E4, 0xFF2EC27E, 0xFFE66100, 0xFF9141AC, 0xFFC01C28 }.Select(c =>
                            new DecoratedBox { Fill = new Color(c), Radius = 10, Child = new SizedBox(width: 56, height: 56) }),
                    ]),
                    new AdwButton(label: "Still clickable underneath", onPressed: () => { }) { Pill = true },
                ])),
            },
        };
        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch, children: [
            new Padding(EdgeInsets.All(12), new Row(spacing: 12, children: [
                new Label("Strength", fontSize: 13),
                new Expanded(new AdwSlider(value: 0.5f, onChanged: v => { _ripple.Strength = v; _ripple.MarkNeedsPaint(); })),
            ])),
            new Expanded(_ripple),
        ]);
    }
}
```

The effect is paint-only: layout and hit testing are untouched, so the button underneath still works.

## A second input

`AddShaderEffect(…, imageKey: handle)` binds an app-owned texture (from `ZigoteEngine.LoadTextureFromRgba`, or a render texture's key) at `@group(1)`. Declare it in your WGSL and you have a second input: a colour LUT, a mask, a noise texture, a previous pass:

```wgsl
@group(1) @binding(0) var lut: texture_2d<f32>;
@group(1) @binding(1) var lut_sampler: sampler;
```

The Camera plugin's colour-grading LUT (`plugins/Camera/Camera/CameraLut.cs`) is a complete example. For multi-pass pipelines, set `chainsBackdrop: true` so each pass sees the previous pass's output instead of the original backdrop.

## Other places shaders live

| Where | How |
|---|---|
| 2D sprites | `ZigoteEngine.SpritesShaderCreate(wgsl)`, a custom material for the batched sprite pass |
| 3D materials | The editor's **Shading graph**: nodes compile to WGSL (graph → SSA → WGSL), with a CPU evaluator for previews |
| Particles | VFX graphs compile live in the editor and are baked on export |
| Built-in UI effects | Liquid Glass, backdrop blur, shadows: native WGSL in `Zigote.Engine/src/renderer/shaders/` |

## Tips

- **Read the errors.** `ZigoteEngine.LastShaderError` carries the WGSL parser's message with line, column and a caret under the problem. For CI, validate shader files offline with `naga shader.wgsl`.
- **Keep fragment work proportional to area.** A shader runs once per covered device pixel, so a full-screen 24-tap blur on a 5K display is 350 million texture reads per frame.
- **Animate with a ticker** and pass time as a parameter. The shader itself has no clock.
- **Mind the pixel ratio** everywhere you pass geometry. Forgetting it gives effects that are half-size and offset on Retina displays.
- **Respect reduce-motion** for animated shaders.
