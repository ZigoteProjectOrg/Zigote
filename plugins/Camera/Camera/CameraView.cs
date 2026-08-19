using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Semantics;
using Zigote.UI.Widgets;

namespace Camera;

/// <summary>How the preview frame is fitted into the space the view was given.</summary>
public enum CameraFit
{
    /// <summary>Fit inside, preserving aspect. Letterbox bars show <see cref="CameraView.Background" />.</summary>
    Contain,

    /// <summary>Fill the box, preserving aspect, cropping the overflow. The camera-preview default.</summary>
    Cover,

    /// <summary>Fill the box exactly, stretching the aspect.</summary>
    Fill,
}

/// <summary>
///     Shows a <see cref="CameraController" />'s preview, and drives its per-frame
///     <see cref="CameraController.Tick" /> while on screen. It does not own the controller: two
///     views can show the same one, and disposing the view leaves the camera running for whoever
///     started it. The camera sibling of <c>Zigote.Videoplayer</c>'s <c>VideoView</c>.
/// </summary>
public sealed class CameraView : ComposedWidget
{
    private Color _background;
    private CameraFit _fit;
    private CameraSurface? _surface;

    public CameraView(CameraController camera, CameraFit fit = CameraFit.Cover, Color? background = null)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Camera = camera;
        _fit = fit;
        _background = background ?? Colors.Black;
    }

    public CameraController Camera { get; }

    /// <summary>How the frame sits in the box. Pushed straight into the live surface.</summary>
    public CameraFit Fit
    {
        get => _fit;
        set
        {
            if (_fit == value) return;
            _fit = value;
            SyncSurface();
        }
    }

    /// <summary>Painted behind the frame, and in any letterbox bars.</summary>
    public Color Background
    {
        get => _background;
        set
        {
            if (_background.Equals(value)) return;
            _background = value;
            SyncSurface();
        }
    }

    /// <summary>Size to occupy before the first frame, when nothing else constrains the view.</summary>
    public Size FallbackSize { get; set; } = new(width: 480, height: 640);

    /// <summary>What a screen reader announces. Null marks the preview decorative.</summary>
    public string? AltText { get; set; }

    /// <summary>
    ///     Grade the preview through this LUT, on the GPU, at draw time — the frames themselves
    ///     stay raw. Swapping it (or nulling it) takes effect on the next paint.
    /// </summary>
    public CameraLut? Lut
    {
        get => _lut;
        set
        {
            if (ReferenceEquals(objA: _lut, objB: value)) return;
            _lut = value;
            SyncSurface();
        }
    }

    /// <summary>How much of the LUT's grade to apply: 0 = raw, 1 = full.</summary>
    public float LutStrength
    {
        get => _lutStrength;
        set
        {
            if (Math.Abs(_lutStrength - value) < 1e-6f) return;
            _lutStrength = value;
            SyncSurface();
        }
    }

    private CameraLut? _lut;
    private float _lutStrength = 1f;

    protected override void OnMount() => CreateTicker(_ => Pump()).Start();

    protected override Widget Build(BuildContext context)
    {
        _surface ??= new CameraSurface(Camera);
        SyncSurface();
        return _surface;
    }

    private void SyncSurface()
    {
        if (_surface is null) return;

        bool repaint = _surface.Fit != Fit
                       || !_surface.Background.Equals(Background)
                       || !ReferenceEquals(objA: _surface.Lut, objB: Lut)
                       || Math.Abs(_surface.LutStrength - LutStrength) > 1e-6f;
        _surface.Fit = Fit;
        _surface.Background = Background;
        _surface.FallbackSize = FallbackSize;
        _surface.AltText = AltText;
        _surface.Lut = Lut;
        _surface.LutStrength = LutStrength;
        if (repaint) _surface.MarkNeedsPaint();
    }

    /// <summary>Advance the controller, repainting only when something new must reach the GPU.</summary>
    private void Pump()
    {
        // The frame counter, not the texture handle: the handle changes once per session (later
        // frames are texel overwrites into the same texture), and a repaint gated on it leaves
        // the damage-tracked scene frozen on the first frame forever.
        long before = Camera.FramesPresented;
        Camera.Tick();
        // A pending GPU photo also needs a paint to carry its render-texture pass.
        if (Camera.FramesPresented != before || Camera.PhotoPassPending) _surface?.MarkNeedsPaint();
    }

    /// <summary>Place an <paramref name="aspect" />-shaped frame inside <paramref name="box" />.</summary>
    internal static Rect FitRect(Rect box, float aspect, CameraFit fit)
    {
        if (fit == CameraFit.Fill || aspect <= 0 || box.Width <= 0 || box.Height <= 0)
            return box;

        float boxAspect = box.Width / box.Height;
        bool matchHeight = fit == CameraFit.Contain ? boxAspect > aspect : boxAspect < aspect;
        float height = matchHeight ? box.Height : box.Width / aspect;
        float width = matchHeight ? box.Height * aspect : box.Width;

        return new Rect(
            x: box.X + ((box.Width - width) / 2f),
            y: box.Y + ((box.Height - height) / 2f),
            width: width,
            height: height
        );
    }
}

/// <summary>The painting half of <see cref="CameraView" />: a texture, a fit rule, a background.</summary>
internal sealed class CameraSurface(CameraController camera) : Widget
{
    private Size _size;

    public CameraFit Fit { get; set; }
    public Color Background { get; set; }
    public Size FallbackSize { get; set; } = new(width: 480, height: 640);
    public string? AltText { get; set; }
    public CameraLut? Lut { get; set; }
    public float LutStrength { get; set; } = 1f;

    public override bool ExcludeSemantics => AltText is null;

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Image;
        config.Label = AltText;
    }

    public override Size Measure(Constraints c)
    {
        float aspect = Aspect();
        bool boundedW = !float.IsInfinity(c.MaxWidth);
        bool boundedH = !float.IsInfinity(c.MaxHeight);

        Size wanted;
        if (boundedW && boundedH) wanted = new Size(width: c.MaxWidth, height: c.MaxHeight);
        else if (boundedW) wanted = new Size(width: c.MaxWidth, height: c.MaxWidth / aspect);
        else if (boundedH) wanted = new Size(width: c.MaxHeight * aspect, height: c.MaxHeight);
        else wanted = FallbackSize;

        _size = c.Constrain(wanted);
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
        // A pending GPU photo rides this paint: its commands go into a render texture, not the
        // screen, so it costs this surface nothing visible.
        camera.PaintPhotoPass(paint);

        if (Background.A > 0) paint.AddRect(bounds: Bounds, color: Background);

        ulong texture = camera.TextureHandle;
        (uint pw, uint ph) = camera.FrameSize;
        if (texture == 0 || pw == 0 || ph == 0) return;

        var dest = CameraView.FitRect(box: Bounds, aspect: (float)pw / ph, fit: Fit);

        bool clip = Fit == CameraFit.Cover;
        if (clip) paint.AddClipStart(Bounds);
        paint.AddImage(
            bounds: dest,
            pixelWidth: (int)pw,
            pixelHeight: (int)ph,
            pixels: null,
            cacheKey: texture
        );
        if (clip) paint.AddClipEnd();

        // The grade goes over the frame's visible pixels only — letterbox bars keep the plain
        // background rather than a LUT-lifted version of it.
        if (Lut is not null && LutStrength > 0f)
            LutEffect.Paint(
                paint: paint,
                bounds: Intersect(a: dest, b: Bounds),
                lut: Lut,
                strength: LutStrength
            );
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        float x = MathF.Max(x: a.X, y: b.X);
        float y = MathF.Max(x: a.Y, y: b.Y);
        float right = MathF.Min(x: a.X + a.Width, y: b.X + b.Width);
        float bottom = MathF.Min(x: a.Y + a.Height, y: b.Y + b.Height);
        return new Rect(x: x, y: y, width: MathF.Max(x: 0, y: right - x), height: MathF.Max(x: 0, y: bottom - y));
    }

    private float Aspect()
    {
        (uint w, uint h) = camera.FrameSize;
        return w > 0 && h > 0 ? (float)w / h : 3f / 4f;
    }
}
