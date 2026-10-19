# Games & 3D

The 3D renderer, gameplay runtime and visual editor are a **separate stack** next to the UI framework. They are built *with* `Zigote.UI` (the editor is an ordinary Zigote app), but nothing in `Zigote.UI` or `Zigote.Core` depends on them. An app that only draws widgets links none of it, and the native 3D stack is never even allocated.

```
Zigote.Editor          authoring: hierarchy, inspector, viewport, graphs, export
Zigote.Scripting       your gameplay code: Component subclasses, hot-reloaded
Zigote.World / .Save   spawning, tags, spatial queries, save & load
Zigote.ECS / Physics2D flecs entities; 2D physics (3D physics is Jolt, native)
Zigote.Vfx / Graphs.*  particles; node graphs that compile to WGSL
Zigote.Runtime         scenes, prefabs, animation, the frame loop a shipped game runs on
Zigote.Core → libzigote
```

## Components

Gameplay code is a `Component` attached to a scene node:

```csharp
using Zigote.Core.Math3D;
using Zigote.Scripting;

public sealed class Spinner : Component
{
    private float _yaw;

    protected override void OnUpdate(float dt)
    {
        _yaw += dt * 1.5f;
        Rotation = Quat.FromEuler(0f, _yaw, 0f);   // pitch, yaw, roll
    }
}
```

- `OnUpdate` runs on a **fixed 120 Hz tick**, not once per rendered frame. Smooth visuals with `Time.InterpolationAlpha`.
- `Position`, `Rotation` and `Scale` sync from the node before `OnUpdate` and are written back after it.
- An exception disables only the component that threw; the editor keeps running.
- Static facades give scripts the engine without wiring: `Physics`, `World`, `Input`, `Gamepad`, `Camera`, `Audio`, `Vfx`, `Save`, `Ecs`, `Time`, and `Hud`.

## The HUD is the UI framework

`Hud.Root` is a widget tree, so everything in this playground (layout, signals, Adwaita controls, animation) is also your in-game UI. The 3D scene renders first and the widget layer composites over it in the same frame.

## The 2D layer, live

2D games don't need the 3D stack at all. A `LeafWidget` with a ticker is already a game loop. This one bounces balls with simple physics; click to add more.

```csharp run height=320
sealed class Balls : LeafWidget
{
    private record struct Ball(float X, float Y, float Vx, float Vy, float R, Color C);
    private readonly List<Ball> _balls = [];
    private Size _size;

    protected override void OnMount() => CreateTicker(Step).Start();

    public override Size Measure(Constraints c) => _size = new Size(c.MaxWidth, c.MaxHeight);
    public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);

    public override void OnPointerDown(Offset p)
    {
        for (int i = 0; i < 6; i++)
            _balls.Add(new Ball(p.X - Bounds.X, p.Y - Bounds.Y,
                Random.Shared.NextSingle() * 400 - 200, -Random.Shared.NextSingle() * 400,
                6 + Random.Shared.NextSingle() * 10,
                new Color(Random.Shared.NextSingle(), 0.5f + Random.Shared.NextSingle() * 0.5f, 1f)));
    }

    private void Step(float dt)
    {
        float w = Bounds.Width, h = Bounds.Height;
        for (int i = 0; i < _balls.Count; i++)
        {
            var b = _balls[i];
            b.Vy += 900 * dt;                                     // gravity
            b.X += b.Vx * dt; b.Y += b.Vy * dt;
            if (b.Y > h - b.R) { b.Y = h - b.R; b.Vy *= -0.82f; } // floor, losing energy
            if (b.X < b.R || b.X > w - b.R) { b.X = Math.Clamp(b.X, b.R, w - b.R); b.Vx *= -1; }
            _balls[i] = b;
        }
        MarkNeedsPaint();
    }

    public override void Paint(PaintList paint)
    {
        paint.AddText(_balls.Count == 0 ? "Click anywhere" : $"{_balls.Count} balls", Bounds.X + 12, Bounds.Y + 22, new Color(0xFF888888), 13);
        foreach (var b in _balls)
            paint.AddRect(new Rect(Bounds.X + b.X - b.R, Bounds.Y + b.Y - b.R, b.R * 2, b.R * 2), b.C, radius: b.R);
    }
}

class Demo : ComposedWidget
{
    protected override Widget Build(BuildContext context) => new Balls();
}
```

For real 2D games, `Zigote.Render2D` adds a native batched sprite pass (atlases, materials, tilemaps) and `Zigote.Physics2D` adds rigid bodies.

## Rendering 3D

A forward+, EEVEE-style pipeline: cascaded shadows, horizon-based AO with optional SSGI, screen-space reflections, bloom, depth of field, AgX tonemapping, auto exposure and TAA. It supports the full glTF PBR material set, including transmission, clearcoat and real-IOR glass. GPU instancing, frustum culling, LOD and streaming live in `Zigote.Runtime`.

## Shipping

The editor exports a project per platform (`--rids osx-arm64,win-x64,linux-x64`, plus iOS and Android), as JIT or Native AOT, and the result runs on `Zigote.Player`:

```sh
dotnet run --project Zigote.Editor -- --export MyGame.zigoteproj --rids osx-arm64,win-x64 --mode aot --out ./dist
```
