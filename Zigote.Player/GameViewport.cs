using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Runtime;
using Zigote.Runtime.Scene;
using Zigote.Scripting;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.Vfx;

namespace Zigote.Player;

/// <summary>
///     The standalone player's single full-window widget: renders the 3D scene each frame, hosts the
///     game's <see cref="Hud.Root" /> widget tree over it, and routes play input into the running
///     <see cref="GameSession" /> — the player-side counterpart of the editor viewport's play-mode
///     paths, minus gizmos, editor cameras, and debug overlays.
/// </summary>
public sealed class GameViewport : Widget
{
    private readonly GameHost _host;
    private readonly ThemeData _theme;
    private MediaQuery? _hudMedia;

    // Retained HUD host (same wiring as the editor viewport): ThemeProvider → viewport-sized
    // MediaQuery → the game's Hud.Root. Input is routed in HitTest; focus traversal + hot reload
    // reach it through GetChildren.
    private Widget? _hudSource;
    private Widget? _hudWrapper;

    private Offset _lastMousePos;

    // Reused scratch for flattening particles into the native upload buffer (9 floats/particle).
    private float[] _particleScratch = [];
    private bool _rightDragging;

    private Size _size;

    public GameViewport(GameHost host, ThemeData theme)
    {
        _host = host;
        _theme = theme;
    }

    public override bool Focusable => true;

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
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
        paint.AddRect(bounds: Bounds, color: Color.Black);

        uint renderW = (uint)MathF.Max(x: 1f, y: MathF.Floor(Bounds.Width));
        uint renderH = (uint)MathF.Max(x: 1f, y: MathF.Floor(Bounds.Height));
        RenderView.SetViewport(width: renderW, height: renderH);

        var cam = RenderView.IsAvailable ? RenderView.CameraPosition : Vec3.Zero;
        LodSystem.Apply(root: _host.Scene.Root, cameraPos: cam);

        UploadVfxParticlesNative();
        UploadSprites2D(renderW: renderW, renderH: renderH);

        ulong texHandle = ZigoteEngine.Instance!.Render3D(width: renderW, height: renderH);
        if (texHandle != 0)
        {
            paint.AddImage(
                bounds: Bounds,
                pixelWidth: (int)renderW,
                pixelHeight: (int)renderH,
                pixels: null,
                cacheKey: texHandle
            );
        }

        DrawGameHud(paint);

        // Scene-transition fade (Scenes.Load with a fade) — covers the frame, HUD included.
        if (_host.Session is { ScreenFadeAlpha: > 0f } fadingSession)
        {
            paint.AddRect(
                bounds: Bounds,
                color: Color.Black.WithAlpha(fadingSession.ScreenFadeAlpha)
            );
        }
    }

    // ── Game HUD hosting (ported from the editor viewport) ────────────────────

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        // Interactive/opaque HUD widgets capture input; transparent regions fall through to the
        // viewport (camera control). Hit-test the game tree directly, not the theme/media wrapper.
        if (_hudSource is not null && _hudSource.HitTest(point) is { } hit) return hit;
        return this;
    }

    public override IEnumerable<Widget> GetChildren() =>
        _hudWrapper is not null ? new[] { _hudWrapper } : [];

    private void DrawGameHud(PaintList paint)
    {
        SyncHudWidget();
        if (_hudWrapper is null) return;

        float w = MathF.Max(x: 1f, y: Bounds.Width);
        float h = MathF.Max(x: 1f, y: Bounds.Height);
        _hudWrapper.Measure(Constraints.Tight(width: w, height: h));
        _hudWrapper.Layout(new Offset(x: Bounds.X, y: Bounds.Y));
        _hudWrapper.Paint(paint);
    }

    private void SyncHudWidget()
    {
        var src = Hud.Root;
        if (!ReferenceEquals(objA: src, objB: _hudSource))
        {
            _hudWrapper?.Detach();
            _hudSource = src;
            if (src is null)
            {
                _hudWrapper = null;
                _hudMedia = null;
            }
            else
            {
                _hudMedia = new MediaQuery(data: ViewportMedia(), child: src);
                _hudWrapper = new ThemeProvider(data: _theme, child: _hudMedia);
                if (Owner is not null) _hudWrapper.Attach(owner: Owner, parent: this);
            }
        }

        if (_hudMedia is not null) _hudMedia.Data = ViewportMedia();
    }

    private MediaQueryData ViewportMedia()
    {
        float scale = Owner?.Engine.Scale ?? 1f;
        return new MediaQueryData(
            width: MathF.Max(x: 1f, y: Bounds.Width),
            height: MathF.Max(x: 1f, y: Bounds.Height),
            devicePixelRatio: scale
        );
    }

    // ── Play input (ported from the editor viewport's play-mode paths) ────────

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (down && scancode == (uint)KeyCode.Escape)
        {
            // Esc releases a captured pointer first and only quits once the cursor is back — quitting
            // out from under a hidden, pinned cursor is not a choice the player meant to make.
            if (Owner is { Engine.RelativeMouseMode: true })
                Owner.Engine.SetRelativeMouseMode(false);
            else App.Active?.RequestQuit();
            return;
        }

        if (_host.Session is not { } play) return;

        // Publish the raw key to the session's general held-key set, so a game script can read ANY
        // key (menus, a second couch player, custom bindings) — not just the built-in drive keys.
        if (Enum.GetName((KeyCode)scancode) is { } keyName) play.SetKey(name: keyName, down: down);

        switch (char.ToLower(keyChar))
        {
            case 'w': play.MoveForward = down; break;
            case 's': play.MoveBack = down; break;
            case 'a': play.MoveLeft = down; break;
            case 'd': play.MoveRight = down; break;
            case 'q': play.MoveDown = down; break;
            case 'e': play.MoveUp = down; break;
            case ' ': play.Handbrake = down; break;
            case 'r': play.ResetCar = down; break;
        }
    }

    /// <summary>Motion while the game has captured the pointer — see the editor viewport's copy.</summary>
    public override void OnPointerRelative(float deltaX, float deltaY)
    {
        if (_host.Session is not { } play) return;
        play.LookDx += deltaX;
        play.LookDy += deltaY;
    }

    protected override void OnFocusChanged(bool focused)
    {
        // A held key's release is lost when focus goes elsewhere — drop all latched input.
        if (!focused) _host.Session?.ResetInput();
    }

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);
        _lastMousePos = point;
    }

    public override void OnRightClick(Offset point)
    {
        App.Active?.RequestFocus(this);
        _lastMousePos = point;
        _rightDragging = true;
    }

    public override void OnRightPointerUp(Offset point) => _rightDragging = false;

    public override void OnPointerMove(Offset point)
    {
        float dx = point.X - _lastMousePos.X;
        float dy = point.Y - _lastMousePos.Y;
        _lastMousePos = point;

        if (_rightDragging && _host.Session is { } play)
        {
            play.LookDx += dx;
            play.LookDy += dy;
        }
    }

    // ── 2D sprites (native sprite pass; mirrors the editor viewport) ──────────

    private void UploadSprites2D(uint renderW, uint renderH)
    {
        var vp = _host.Sprites2D.ResolvePlayCamera(
            root: _host.Scene.Root,
            viewportW: renderW,
            viewportH: renderH
        );
        _host.Sprites2D.Render(
            root: _host.Scene.Root,
            sceneViewProjection: vp,
            viewportW: renderW,
            viewportH: renderH,
            includeScriptQueue: true
        );
    }

    // ── VFX (native billboard pass; ported from the editor viewport) ──────────

    private void UploadVfxParticlesNative()
    {
        if (ZigoteEngine.Instance is not { } engine || _host.Session is not { } play) return;

        foreach ((ulong key, var sim) in play.AllVfxSimulators)
        {
            if (key == 0) continue;
            var live = sim.Pool.Live;
            int count = live.Length;
            if (count == 0)
            {
                engine.ParticlesClear(key);
                continue;
            }

            int need = count * 9;
            if (_particleScratch.Length < need)
                _particleScratch = new float[Math.Max(val1: need, val2: 256 * 9)];
            for (int i = 0; i < count; i++)
            {
                ref readonly var p = ref live[i];
                int o = i * 9;
                _particleScratch[o] = p.Position.X;
                _particleScratch[o + 1] = p.Position.Y;
                _particleScratch[o + 2] = p.Position.Z;
                _particleScratch[o + 3] = p.Size;
                _particleScratch[o + 4] = p.Rotation;
                _particleScratch[o + 5] = p.Color.R;
                _particleScratch[o + 6] = p.Color.G;
                _particleScratch[o + 7] = p.Color.B;
                _particleScratch[o + 8] = p.Color.A;
            }

            uint blend = sim.Asset.Blend == VfxBlendMode.Additive ? 0u : 1u;
            engine.ParticlesUpload(
                nodeHandle: key,
                data: _particleScratch.AsSpan(start: 0, length: need),
                count: (uint)count,
                blend: blend
            );
        }
    }
}
