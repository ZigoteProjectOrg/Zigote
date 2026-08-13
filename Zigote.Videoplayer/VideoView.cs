using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Semantics;
using Zigote.UI.Widgets;

namespace Zigote.Videoplayer;

/// <summary>How the frame is fitted into the space the view was given.</summary>
public enum VideoFit
{
    /// <summary>Fit inside, preserving aspect. Letterbox bars show <see cref="VideoView.Background" />.</summary>
    Contain,

    /// <summary>Fill the box, preserving aspect, cropping the overflow.</summary>
    Cover,

    /// <summary>Fill the box exactly, stretching the aspect. Rarely what you want.</summary>
    Fill,
}

/// <summary>
///     Shows a <see cref="VideoPlayer" />'s current frame, and drives the player's per-frame
///     <see cref="VideoPlayer.Tick" /> while it is on screen — so a UI never has to find a heartbeat
///     to lend it.
///     <para>
///         It does not own the player: two views can show the same one, and disposing the view leaves
///         the player alive for whoever created it.
///     </para>
///     <para>
///         Sizing follows the video's own aspect. Given a bounded box it takes it and letterboxes
///         inside; given an unbounded axis it derives that axis from the other, so a view in a
///         scrolling column ends up the right shape instead of collapsing to nothing.
///     </para>
/// </summary>
public sealed class VideoView : ComposedWidget
{
    private Color _background;
    private VideoFit _fit;
    private VideoSurface? _surface;

    public VideoView(VideoPlayer player, VideoFit fit = VideoFit.Contain, Color? background = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        Player = player;
        _fit = fit;
        _background = background ?? Colors.Black;
    }

    public VideoPlayer Player { get; }

    /// <summary>
    ///     How the frame sits in the box. Pushed straight into the live surface, so switching it
    ///     repaints one widget instead of rebuilding the screen that owns it.
    /// </summary>
    public VideoFit Fit
    {
        get => _fit;
        set
        {
            if (_fit == value) return;
            _fit = value;
            SyncSurface();
        }
    }

    /// <summary>Painted behind the frame, and in the letterbox bars. Black by default, as it should be.</summary>
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

    /// <summary>Size to occupy before the first frame arrives, when nothing else constrains the view.</summary>
    public Size FallbackSize { get; set; } = new(width: 640, height: 360);

    /// <summary>
    ///     What a screen reader announces. Null marks the view decorative — right for a background
    ///     loop, wrong for anything the user is here to watch.
    /// </summary>
    public string? AltText { get; set; }

    // ponytail: ticks for as long as the view is mounted, including while paused, which keeps the
    // frame loop awake. Tick() returns immediately with no pipeline, so the cost is a call per
    // frame; gate it on a player-side "is decoding" signal if idle power ever matters.
    protected override void OnMount() => CreateTicker(_ => Pump()).Start();

    protected override Widget Build(BuildContext context)
    {
        // Retained across rebuilds so the ticker's MarkNeedsPaint always lands on the live surface.
        _surface ??= new VideoSurface(Player);
        SyncSurface();
        return _surface;
    }

    /// <summary>Push the view's presentation properties into the retained surface.</summary>
    private void SyncSurface()
    {
        if (_surface is null) return;

        bool repaint = _surface.Fit != Fit || !_surface.Background.Equals(Background);
        _surface.Fit = Fit;
        _surface.Background = Background;
        _surface.FallbackSize = FallbackSize;
        _surface.AltText = AltText;
        if (repaint) _surface.MarkNeedsPaint();
    }

    /// <summary>Advance the player, and repaint only when that produced a new frame.</summary>
    private void Pump()
    {
        ulong before = Player.TextureHandle;
        Player.Tick();
        if (Player.TextureHandle != before) _surface?.MarkNeedsPaint();
    }

    /// <summary>
    ///     Place an <paramref name="aspect" />-shaped frame inside <paramref name="box" />. Pure, so
    ///     the letterbox arithmetic is assertable without a GPU.
    /// </summary>
    internal static Rect FitRect(Rect box, float aspect, VideoFit fit)
    {
        if (fit == VideoFit.Fill || aspect <= 0 || box.Width <= 0 || box.Height <= 0)
            return box;

        float boxAspect = box.Width / box.Height;

        // Contain: the axis that runs out first wins. Cover: the other one does.
        bool matchHeight = fit == VideoFit.Contain ? boxAspect > aspect : boxAspect < aspect;
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

/// <summary>The painting half of <see cref="VideoView" />: a texture, a fit rule and a background.</summary>
internal sealed class VideoSurface(VideoPlayer player) : Widget
{
    private Size _size;

    public VideoFit Fit { get; set; }
    public Color Background { get; set; }
    public Size FallbackSize { get; set; } = new(width: 640, height: 360);
    public string? AltText { get; set; }

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
        if (Background.A > 0) paint.AddRect(bounds: Bounds, color: Background);

        ulong texture = player.TextureHandle;
        (uint pw, uint ph) = player.FrameSize;
        if (texture == 0 || pw == 0 || ph == 0) return;

        var dest = VideoView.FitRect(box: Bounds, aspect: (float)pw / ph, fit: Fit);

        // Cover deliberately overflows; the other fits are already inside Bounds, so only pay for
        // the clip when it can actually cut something.
        bool clip = Fit == VideoFit.Cover;
        if (clip) paint.AddClipStart(Bounds);
        paint.AddImage(
            bounds: dest,
            pixelWidth: (int)pw,
            pixelHeight: (int)ph,
            pixels: null,
            cacheKey: texture
        );
        if (clip) paint.AddClipEnd();
    }

    private float Aspect()
    {
        (uint w, uint h) = player.FrameSize;
        if (w > 0 && h > 0) return (float)w / h;
        return (float)(player.Media.Value?.AspectRatio ?? 16.0 / 9.0);
    }
}
