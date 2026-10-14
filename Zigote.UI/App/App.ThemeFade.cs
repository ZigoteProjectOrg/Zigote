using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;

namespace Zigote.UI.Host;

public partial class App
{
    private bool _themeFading;

    /// <summary>
    ///     Seconds the window takes to dissolve from one theme into the next when
    ///     <see cref="Theme" /> is reassigned — light to dark, a new accent. 0 swaps outright.
    /// </summary>
    public static float ThemeCrossfade { get; set; } = 0.2f;

    /// <summary>
    ///     Lays the frame that is on screen now over the window and fades it away, so whatever the
    ///     next frame paints — the new theme — arrives as a dissolve instead of a flash. The
    ///     widgets underneath restyle at once, exactly as before; only the eye is eased.
    /// </summary>
    private void FadeFromLastFrame()
    {
        // One at a time: a dragged accent picker reassigns the theme every frame, and the first
        // frame's picture fading out over the live ones is all the smoothing that needs.
        if (_themeFading || ThemeCrossfade <= 0f || AnimationController.ReduceMotion ||
            InTreeWalk ||
            Engine is null || _paint is not { Count: > 0 })
            return;

        // ponytail: main window only — the capture lives in the main window's image cache, so a
        // secondary window swaps its theme outright. Capture per window if that ever shows.
        if (ParentApp is not null) return;
        ulong texture = CaptureUiTexture(width: out uint w, height: out uint h);
        if (texture == 0) return;
        _themeFading = true;
        PushOverlay(
            new ThemeFade(
                app: this,
                texture: texture,
                width: w,
                height: h
            )
        );
    }

    private sealed class ThemeFade : Widget
    {
        private readonly AnimationController _anim;
        private readonly App _app;
        private readonly uint _height;
        private readonly ulong _texture;
        private readonly uint _width;
        private Size _size;

        public ThemeFade(App app, ulong texture, uint width, uint height)
        {
            _app = app;
            _texture = texture;
            _width = width;
            _height = height;
            _anim = new AnimationController(durationSeconds: ThemeCrossfade, vsync: this) {
                Curve = Curves.EaseOut,
            };
            _anim.OnTick += MarkNeedsPaint;
            _anim.OnCompleted += () =>
            {
                _app._themeFading = false;
                _app.PopOverlay(this);
                ZigoteEngine.ReleaseTexture(_texture);
            };
        }

        protected override void OnMount()
        {
            _anim.AttachTicker(this);
            if (_anim.Status == AnimationStatus.Idle) _anim.Forward();
        }

        public override Size Measure(Constraints c)
        {
            _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
            return _size;
        }

        public override void Layout(Offset origin) =>
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _size.Width,
                height: _size.Height
            );

        public override void Paint(PaintList paint)
        {
            float alpha = 1f - _anim.Value;
            if (alpha <= 0.001f) return;
            paint.PushAlpha(alpha);
            paint.AddImage(
                bounds: Bounds,
                pixelWidth: (int)_width,
                pixelHeight: (int)_height,
                pixels: null,
                cacheKey: _texture
            );
            paint.PopAlpha();
        }

        // A picture of the window, not a part of it: every click goes to what is underneath.
        public override Widget? HitTest(Offset point) => null;
    }
}
