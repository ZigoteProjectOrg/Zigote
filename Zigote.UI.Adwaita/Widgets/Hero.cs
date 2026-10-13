using Zigote.Core.Animation;
using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>
///     Draws a custom hero flight. <paramref name="from" /> and <paramref name="to" /> are the two
///     heroes' frames and <paramref name="t" /> runs 0 at the first to 1 at the second.
/// </summary>
public delegate void AdwHeroPainter(PaintList paint, Rect from, Rect to, float t);

/// <summary>
///     A shared-element transition: wrap the same thing in two places with the same
///     <see cref="Tag" />, and when the second appears the first flies to it — and back when it
///     goes — so what was pressed is visibly what opened.
///     <code>
///         new AdwHero(tag: photo, child: thumbnail)   // in the grid
///         new AdwHero(tag: photo, child: fullPicture) // in the dialog it opens
///     </code>
///     <para>
///         Nothing else is needed. The hero already on screen is the source; one mounted later
///         with its tag is the destination, and stays hidden until the flight lands on it. The
///         return flight starts when the destination's <see cref="AdwDialog" /> begins to close;
///         anywhere else, call <see cref="Return" /> as the destination starts to leave.
///     </para>
///     <para>
///         What flies is the source's own child, laid out afresh at every size on the way, clipped
///         to a corner easing between the two <see cref="Radius" /> values. When the two ends are
///         not the same thing at two sizes — a cropped thumbnail opening into the whole picture —
///         set <see cref="Flight" /> on the destination and draw the journey yourself.
///     </para>
/// </summary>
public sealed class AdwHero : Widget
{
    // Tag → the hero on screen that a later one with the same tag flies from.
    private static readonly Dictionary<object, AdwHero> Sources = [];

    // True while this hero's child is in the air rather than in its slot.
    private bool _away;
    private Shuttle? _shuttle;
    private Size _size;

    public AdwHero(object tag, Widget? child = null)
    {
        Tag = tag;
        Child = child;
    }

    /// <summary>What pairs two heroes. Compared with <see cref="object.Equals(object)" />.</summary>
    public object Tag { get; }

    public Widget? Child { get; set; }

    /// <summary>Corner radius of this end of the flight; the clip eases between the two.</summary>
    public float Radius { get; set; }

    /// <summary>How the flight travels. Read from the destination.</summary>
    public MotionSpec Motion { get; set; } = AdwMotion.Reveal;

    /// <summary>Replaces the default flight with your own drawing. Read from the destination.</summary>
    public AdwHeroPainter? Flight { get; set; }

    /// <summary>Fires on the destination the frame the flight lands and its child takes over.</summary>
    public Action? OnLanded { get; set; }

    /// <summary>
    ///     Send the child back to where it came from. For a destination that leaves by some route
    ///     other than an <see cref="AdwDialog" /> closing, which does this itself.
    /// </summary>
    public void Return()
    {
        if (_shuttle is not { } shuttle || shuttle.To != this) return;
        _away = true;
        MarkNeedsPaint();
        shuttle.Return();
    }

    /// <summary>Returns every destination hero under <paramref name="root" />.</summary>
    public static void ReturnAll(Widget root)
    {
        if (root is AdwHero hero) hero.Return();
        foreach (var child in root.GetChildren()) ReturnAll(child);
    }

    protected override void OnMount()
    {
        if (Sources.TryGetValue(key: Tag, value: out var source) && !ReferenceEquals(objA: source, objB: this))
        {
            _away = true;
            _shuttle = source._shuttle = new Shuttle(from: source, to: this);
            _shuttle.Fly();
        }
        else
            Sources[Tag] = this;
    }

    protected override void OnUnmount()
    {
        if (Sources.TryGetValue(key: Tag, value: out var source) && ReferenceEquals(objA: source, objB: this))
            Sources.Remove(Tag);
        if (_shuttle is not { } shuttle) return;
        _shuttle = null;
        // A destination that goes while its child is flying home is the normal case — the dialog
        // finishes closing first — and the flight only needs where it stood. Anything else ends it.
        if (shuttle.To != this || !shuttle.Returning) shuttle.Leave();
    }

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? Size.Zero;
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        if (!_away) Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point) =>
        !_away && Bounds.Contains(px: point.X, py: point.Y) ? Child?.HitTest(point) : null;

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);

    /// <summary>The overlay that carries the source's child between the two frames.</summary>
    private sealed class Shuttle : Widget
    {
        private readonly AnimationController _anim;
        private readonly AdwHero _from;
        private App? _host;
        private Size _size;

        public Shuttle(AdwHero from, AdwHero to)
        {
            _from = from;
            To = to;
            _anim = new AnimationController(durationSeconds: 0.35f, vsync: this) { Spec = to.Motion };
            // Layout, not just paint: the default flight re-lays the child out at each size.
            _anim.OnTick += MarkNeedsLayout;
            _anim.OnCompleted += () =>
            {
                To._away = false;
                To.MarkNeedsPaint();
                To.OnLanded?.Invoke();
                Leave();
            };
            _anim.OnDismissed += Leave;
        }

        public AdwHero To { get; }
        public bool Returning => _anim.Status == AnimationStatus.Reverse;

        protected override void OnMount() => _anim.AttachTicker(this);

        public void Fly()
        {
            Enter();
            _anim.Forward();
        }

        /// <summary>From wherever the flight is: landed, or still on its way out.</summary>
        public void Return()
        {
            if (_host is null)
            {
                Enter();
                _anim.Seek(1f);
            }

            _anim.Reverse();
        }

        private void Enter()
        {
            if (_host is not null) return;
            _host = To.Owner ?? App.Active;
            _host?.PushOverlay(this);
            _from._away = true;
            _from.MarkNeedsPaint();
        }

        public void Leave()
        {
            _host?.PopOverlay(this);
            _host = null;
            _from._away = false;
            // The default flight laid the child out all over the window; put it back in its slot.
            _from.MarkNeedsLayout();
        }

        private Rect Frame()
        {
            Rect a = _from.Bounds, b = To.Bounds;
            float t = _anim.Value;
            return new Rect(
                x: a.X + ((b.X - a.X) * t),
                y: a.Y + ((b.Y - a.Y) * t),
                width: a.Width + ((b.Width - a.Width) * t),
                height: a.Height + ((b.Height - a.Height) * t)
            );
        }

        public override Size Measure(Constraints c)
        {
            _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);
            if (To.Flight is not null || _from.Child is not { } child || To.Bounds.Width <= 0f) return;
            var frame = Frame();
            child.Measure(Constraints.Tight(width: frame.Width, height: frame.Height));
            child.Layout(new Offset(x: frame.X, y: frame.Y));
        }

        public override void Paint(PaintList paint)
        {
            // Not laid out yet: the destination was mounted this frame and has no frame to fly to.
            if (To.Bounds.Width <= 0f || _from.Bounds.Width <= 0f) return;
            float t = _anim.Value;
            if (To.Flight is { } custom)
            {
                custom(paint: paint, from: _from.Bounds, to: To.Bounds, t: t);
                return;
            }

            paint.AddClipStart(bounds: Frame(), radius: _from.Radius + ((To.Radius - _from.Radius) * t));
            _from.Child?.Paint(paint);
            paint.AddClipEnd();
        }

        // Never in the way: whatever is underneath keeps every click.
        public override Widget? HitTest(Offset point) => null;
    }
}
