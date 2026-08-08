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
        _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, Color.Black);

        var renderW = (uint)MathF.Max(1f, MathF.Floor(Bounds.Width));
        var renderH = (uint)MathF.Max(1f, MathF.Floor(Bounds.Height));
        RenderView.SetViewport(renderW, renderH);

        var cam = RenderView.IsAvailable ? RenderView.CameraPosition : Vec3.Zero;
        LodSystem.Apply(_host.Scene.Root, cam);

        UploadVfxParticlesNative();
        UploadSprites2D(renderW, renderH);

        var texHandle = ZigoteEngine.Instance!.Render3D(renderW, renderH);
        if (texHandle != 0)
            paint.AddImage(
                Bounds,
                (int)renderW,
                (int)renderH,
                null,
                texHandle
            );

        DrawGameHud(paint);

        // Scene-transition fade (Scenes.Load with a fade) — covers the frame, HUD included.
        if (_host.Session is { ScreenFadeAlpha: > 0f } fadingSession)
            paint.AddRect(Bounds, Color.Black.WithAlpha(fadingSession.ScreenFadeAlpha));
    }

    // ── Game HUD hosting (ported from the editor viewport) ────────────────────

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        // Interactive/opaque HUD widgets capture input; transparent regions fall through to the
        // viewport (camera control). Hit-test the game tree directly, not the theme/media wrapper.
        if (_hudSource is not null && _hudSource.HitTest(point) is { } hit) return hit;
        return this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return _hudWrapper is not null ? new[] { _hudWrapper } : [];
    }

    private void DrawGameHud(PaintList paint)
    {
        SyncHudWidget();
        if (_hudWrapper is null) return;

        var w = MathF.Max(1f, Bounds.Width);
        var h = MathF.Max(1f, Bounds.Height);
        _hudWrapper.Measure(Constraints.Tight(w, h));
        _hudWrapper.Layout(new Offset(Bounds.X, Bounds.Y));
        _hudWrapper.Paint(paint);
    }

    private void SyncHudWidget()
    {
        var src = Hud.Root;
        if (!ReferenceEquals(src, _hudSource))
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
                _hudMedia = new MediaQuery(ViewportMedia(), src);
                _hudWrapper = new ThemeProvider(_theme, _hudMedia);
                if (Owner is not null) _hudWrapper.Attach(Owner, this);
            }
        }

        if (_hudMedia is not null) _hudMedia.Data = ViewportMedia();
    }

    private MediaQueryData ViewportMedia()
    {
        var scale = Owner?.Engine.Scale ?? 1f;
        return new MediaQueryData(MathF.Max(1f, Bounds.Width), MathF.Max(1f, Bounds.Height), scale);
    }

    // ── Play input (ported from the editor viewport's play-mode paths) ────────

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (down && scancode == (uint)KeyCode.Escape)
        {
            App.Active?.RequestQuit();
            return;
        }

        if (_host.Session is not { } play) return;

        // Publish the raw key to the session's general held-key set, so a game script can read ANY
        // key (menus, a second couch player, custom bindings) — not just the built-in drive keys.
        if (Enum.GetName((KeyCode)scancode) is { } keyName) play.SetKey(keyName, down);

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

    public override void OnRightPointerUp(Offset point)
    {
        _rightDragging = false;
    }

    public override void OnPointerMove(Offset point)
    {
        var dx = point.X - _lastMousePos.X;
        var dy = point.Y - _lastMousePos.Y;
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
        var vp = _host.Sprites2D.ResolvePlayCamera(_host.Scene.Root, renderW, renderH);
        _host.Sprites2D.Render(
            _host.Scene.Root,
            vp,
            renderW,
            renderH,
            true
        );
    }

    // ── VFX (native billboard pass; ported from the editor viewport) ──────────

    private void UploadVfxParticlesNative()
    {
        if (ZigoteEngine.Instance is not { } engine || _host.Session is not { } play) return;

        foreach (var (key, sim) in play.AllVfxSimulators)
        {
            if (key == 0) continue;
            var live = sim.Pool.Live;
            var count = live.Length;
            if (count == 0)
            {
                engine.ParticlesClear(key);
                continue;
            }

            var need = count * 9;
            if (_particleScratch.Length < need)
                _particleScratch = new float[Math.Max(need, 256 * 9)];
            for (var i = 0; i < count; i++)
            {
                ref readonly var p = ref live[i];
                var o = i * 9;
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

            var blend = sim.Asset.Blend == VfxBlendMode.Additive ? 0u : 1u;
            engine.ParticlesUpload(
                key,
                _particleScratch.AsSpan(0, need),
                (uint)count,
                blend
            );
        }
    }
}