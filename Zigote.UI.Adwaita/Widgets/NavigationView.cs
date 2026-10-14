using Zigote.Core.Animation;
using Zigote.Core.State;
using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>One page in an <see cref="AdwNavigationView" /> stack.</summary>
public sealed class AdwNavigationPage : ComposedWidget
{
    private Widget _page;

    public AdwNavigationPage(Widget child, string title = "")
    {
        _page = child;
        Title = title;
    }

    /// <summary>Shown in the automatic header bar. Read at push/pop time, not live.</summary>
    public string Title { get; set; }

    public Widget Child
    {
        get => _page;
        set => this.Set(field: ref _page, value: value);
    }

    /// <summary>
    ///     When false the automatic back button is suppressed and
    ///     <see cref="AdwNavigationView.Pop" /> refuses.
    /// </summary>
    public bool CanPop { get; set; } = true;

    protected override Widget Build(BuildContext context) => Child;
}

/// <summary>
///     AdwNavigationView — a page stack. Shows the top page under an automatic
///     <see cref="AdwHeaderBar" /> whose back button pops when there is somewhere to go back to.
///     Push slides the incoming page in from the right over the one beneath, which drifts left at a
///     slower pace and dims under the moving page's edge shadow; pop plays the same motion back.
///     Both ride <see cref="AdwMotion.Reveal" />, so a pop mid-push turns around without a jolt.
///     Each page keeps one header-bar wrapper for as long as it is on the stack, so starting or
///     settling a transition re-parents it rather than rebuilding it.
/// </summary>
public sealed class AdwNavigationView : ComposedWidget
{
    private readonly Dictionary<AdwNavigationPage, Widget> _built = [];
    private readonly List<AdwNavigationPage> _pages = [];
    private readonly AnimationController _slide;
    private readonly Signal<int> _version = new(0);
    private bool _autoHeaderBar = true;
    private AdwNavigationPage? _moving; // slides above: incoming on push, departing on pop
    private bool _showEndWindowControls = true;
    private bool _showStartWindowControls = true;
    private AdwNavigationPage? _under; // painted beneath during a transition

    public AdwNavigationView(params AdwNavigationPage[] pages)
    {
        _pages.AddRange(pages);
        _slide = new AnimationController(durationSeconds: 0.25f, vsync: this) {
            Curve = Curves.EaseOut,
            Spec = AdwMotion.Reveal, // a back gesture that turns into a push mid-slide
        };
        _slide.OnTick += OnSlideTick;
        _slide.OnCompleted += EndTransition;
        _slide.OnDismissed += EndTransition;
    }

    /// <summary>Set false to render the top page bare (host provides its own chrome).</summary>
    public bool AutoHeaderBar
    {
        get => _autoHeaderBar;
        set => this.Set(field: ref _autoHeaderBar, value: value);
    }

    /// <summary>
    ///     Passed through to <see cref="AdwHeaderBar.ShowStartWindowControls" /> on the automatic
    ///     header bars. Default true (window-root view); set false when the view is embedded.
    /// </summary>
    public bool ShowStartWindowControls
    {
        get => _showStartWindowControls;
        set => this.Set(field: ref _showStartWindowControls, value: value);
    }

    /// <inheritdoc cref="ShowStartWindowControls" />
    public bool ShowEndWindowControls
    {
        get => _showEndWindowControls;
        set => this.Set(field: ref _showEndWindowControls, value: value);
    }

    public int Depth => _pages.Count;

    public void Push(AdwNavigationPage page)
    {
        var from = _pages.Count > 0 ? _pages[^1] : null;
        _pages.Add(page);
        if (from is not null && !ReferenceEquals(objA: from, objB: page))
        {
            _under = from;
            _moving = page;
            _slide.Dismiss();
            _slide.Forward();
        }

        _version.Value++;
    }

    public void Pop()
    {
        if (_pages.Count <= 1 || !_pages[^1].CanPop) return;
        var top = _pages[^1];
        _pages.RemoveAt(_pages.Count - 1);
        if (!ReferenceEquals(objA: _pages[^1], objB: top))
        {
            _under = _pages[^1];
            _moving = top;
            // Restart a pop-during-pop from fully-out; but not while a push is still running —
            // Complete() there would jump the incoming page to its resting place (and, being a
            // snap, skip EndTransition) one frame before it slides back out.
            if (_slide.Status is not AnimationStatus.Forward) _slide.Complete();
            _slide.Reverse();
        }

        _version.Value++;
    }

    private void OnSlideTick() => MarkNeedsLayout();

    private void EndTransition()
    {
        if (_moving is null) return;
        _under = null;
        _moving = null;
        // A popped page is gone for good: drop its wrapper so it can be collected.
        foreach (var page in _built.Keys)
        {
            if (!_pages.Contains(page)) _built.Remove(page);
        }

        _version.Value++; // collapse the transition stage back to the plain top page
    }

    // ── Ticker plumbing (same pattern as AdwToastOverlay) ──────────────────────


    // OnMount, not Attach: Attach re-runs on every rebuild cascade, so registering here would
    // both leak a ticker per pass and stack duplicate back handlers. A mount is once per tree entry,
    // paired with exactly one OnUnmount — which is the guard the old Remove-before-Add hack faked.
    protected override void OnMount()
    {
        _slide.AttachTicker(this);
        Owner?.AddBackHandler(TryPop);
    }

    protected override void OnUnmount() =>
        Owner?.RemoveBackHandler(TryPop); // still set: Widget.Detach unmounts before dropping Owner

    /// <summary>
    ///     The system back action (Android's back button/gesture, an iOS edge swipe) pops the stack.
    ///     Returning false at the root — or on a page that refuses to pop — lets the action fall
    ///     through to whatever registered before this view, and finally to closing the app, which is
    ///     what a back press at the root is supposed to do. Registering also arms the iOS edge swipe
    ///     (<see cref="App.CanHandleSystemBack" />). No ordering machinery here: App dismisses open
    ///     overlays before it runs any back handler, and handlers run innermost (last-registered)
    ///     first, so a nested view wins over the one it sits in.
    /// </summary>
    private bool TryPop()
    {
        if (_pages.Count <= 1 || !_pages[^1].CanPop) return false;
        Pop();
        return true;
    }

    // ── Tree ───────────────────────────────────────────────────────────────────

    protected override Widget Build(BuildContext context)
    {
        _built.Clear(); // header settings or the theme changed: wrappers are rebuilt once
        // Resolved here, not in the Watch builder: there the ambient context is not this view's.
        var theme = ThemeProvider.Of(context);
        var surface = AdwDialogSurface.Of(ctx: context, theme: theme);
        return new Watch(() =>
            {
                _ = _version.Value; // track push/pop and transition end
                if (_pages.Count == 0) return new SizedBox();

                if (_moving is not null && _under is not null)
                {
                    return new TransitionStage(
                        anim: _slide,
                        under: BuildPage(_under),
                        moving: BuildPage(_moving),
                        dark: theme.IsDark,
                        surface: surface
                    );
                }

                return BuildPage(_pages[^1]);
            }
        );
    }

    private Widget BuildPage(AdwNavigationPage page)
    {
        if (!AutoHeaderBar) return page;
        if (_built.TryGetValue(key: page, value: out var built)) return built;

        // Depth of this page in the stack; a popped (departing) page sat above the whole stack.
        int i = _pages.IndexOf(page);
        int depth = i < 0 ? _pages.Count + 1 : i + 1;

        return _built[page] = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
            Children = {
                new AdwHeaderBar {
                    Title = page.Title,
                    ShowBackButton = depth > 1 && page.CanPop,
                    OnBack = Pop,
                    ShowStartWindowControls = ShowStartWindowControls,
                    ShowEndWindowControls = ShowEndWindowControls,
                },
                new Expanded(page),
            },
        };
    }

    /// <summary>
    ///     The two pages of a push or pop. The moving page is opaque and casts a shadow off its
    ///     leading edge; the page beneath trails it at <see cref="Parallax" /> of the distance and
    ///     dims as it is covered. Both stay measured at full size — a frame only re-positions them.
    ///     The page beneath takes no input while it is covered.
    /// </summary>
    private sealed class TransitionStage(
        AnimationController anim,
        Widget under,
        Widget moving,
        bool dark,
        Color surface) : Widget
    {
        /// <summary>How far the page beneath travels, as a fraction of the moving page's.</summary>
        private const float Parallax = 0.3f;

        private const float ShadowBlur = 18f;

        private readonly Color _dim = Color.Black.WithAlpha(dark ? 0.3f : 0.12f);
        private readonly Color _shadow = Color.Black.WithAlpha(dark ? 0.4f : 0.14f);
        private Size _size;

        // 0 = only the page beneath shows, 1 = the moving page covers it. A spring may overshoot.
        private float T => Math.Clamp(value: anim.Value, min: 0f, max: 1f);

        public override Size Measure(Constraints c)
        {
            // Fill the view (a navigation view is always given a bounded box; fall back to its minimum).
            _size = new Size(
                width: float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth,
                height: float.IsFinite(c.MaxHeight) ? c.MaxHeight : c.MinHeight
            );
            var tight = Constraints.Tight(width: _size.Width, height: _size.Height);
            under.Measure(tight);
            moving.Measure(tight);
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);
            float t = T;
            // Whole pixels: a fractional page offset re-rasterises every glyph on it each frame.
            under.Layout(new Offset(x: MathF.Round(origin.X - (t * Parallax * _size.Width)), y: origin.Y));
            moving.Layout(new Offset(x: MathF.Round(origin.X + ((1f - t) * _size.Width)), y: origin.Y));
        }

        public override void Paint(PaintList paint)
        {
            float t = T;
            paint.AddClipStart(Bounds);
            under.Paint(paint);
            if (t > 0.001f) paint.AddRect(bounds: Bounds, color: _dim.WithAlpha(_dim.A * t));

            var page = moving.Bounds;
            if (page.X < Bounds.Right)
            {
                paint.AddShadow(
                    bounds: page,
                    color: _shadow.WithAlpha(_shadow.A * MathF.Min(x: 1f, y: t * 4f)),
                    borderRadius: 0f,
                    blurRadius: ShadowBlur
                );
                paint.AddRect(bounds: page, color: surface);
                moving.Paint(paint);
            }

            paint.AddClipEnd();
        }

        public override Widget? HitTest(Offset point) =>
            moving.Bounds.Contains(px: point.X, py: point.Y) ? moving.HitTest(point) : null;

        public override IEnumerable<Widget> GetChildren()
        {
            yield return under;
            yield return moving;
        }
    }
}
