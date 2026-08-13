using Zigote.Core.Animation;
using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Widgets.Transitions;

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
///     Push slides the incoming page in from the right (~250 ms ease-out); pop slides the departing
///     page back out, revealing the page beneath. Popped-from pages still lose transient state once
///     the transition settles (the retained instances survive, rebuilt subtrees do not).
/// </summary>
public sealed class AdwNavigationView : ComposedWidget
{
    private readonly List<AdwNavigationPage> _pages = [];
    private readonly AnimationController _slide;
    private readonly Signal<int> _version = new(0);
    private bool _autoHeaderBar = true;
    private AdwNavigationPage? _moving; // slides above: incoming on push, departing on pop
    private SlideTransition? _movingSlide;
    private bool _showEndWindowControls = true;
    private bool _showStartWindowControls = true;
    private AdwNavigationPage? _under; // painted beneath during a transition

    public AdwNavigationView(params AdwNavigationPage[] pages)
    {
        _pages.AddRange(pages);
        _slide = new AnimationController(durationSeconds: 0.25f, vsync: this) {
            Curve = Curves.EaseOut,
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

    private void OnSlideTick()
    {
        // The slide distance is this view's live width (known after any layout; a push before the
        // first layout degrades to a near-instant swap).
        if (_movingSlide is not null)
            _movingSlide.BeginOffset = new Offset(x: MathF.Max(x: Bounds.Width, y: 1f), y: 0f);
        MarkNeedsLayout();
    }

    private void EndTransition()
    {
        if (_moving is null) return;
        _under = null;
        _moving = null;
        _movingSlide = null;
        _version.Value++; // collapse the transition Stack back to the plain top page
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
        return new Watch(() =>
            {
                _ = _version.Value; // track push/pop and transition end
                if (_pages.Count == 0) return new SizedBox();

                if (_moving is not null && _under is not null)
                {
                    _movingSlide = new SlideTransition(
                        controller: _slide,
                        child: BuildPage(_moving)
                    ) {
                        BeginOffset = new Offset(x: MathF.Max(x: Bounds.Width, y: 1f), y: 0f),
                    };
                    // Clip: mid-slide the moving page pokes out to the right of this view.
                    return new ClipRect(
                        new Stack {
                            Children = {
                                BuildPage(_under),
                                _movingSlide,
                            },
                        }
                    );
                }

                return BuildPage(_pages[^1]);
            }
        );
    }

    private Widget BuildPage(AdwNavigationPage page)
    {
        if (!AutoHeaderBar) return page;

        // Depth of this page in the stack; a popped (departing) page sat above the whole stack.
        int i = _pages.IndexOf(page);
        int depth = i < 0 ? _pages.Count + 1 : i + 1;

        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
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
}
