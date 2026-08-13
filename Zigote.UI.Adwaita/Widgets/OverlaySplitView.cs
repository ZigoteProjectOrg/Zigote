using Zigote.Core.Animation;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwOverlaySplitView — sidebar + content side by side; collapsed, the sidebar floats over the
///     content behind a scrim (clicking the scrim closes it). Toggling <see cref="ShowSidebar" />
///     slides the sidebar in/out (~250 ms ease-out); collapsed, the scrim fades with it.
///     ponytail: consumers that rebuild the whole view per state change (instead of mutating
///     ShowSidebar on a retained instance) only get the attach-time slide-in, not the slide-out.
/// </summary>
public sealed class AdwOverlaySplitView : ComposedWidget
{
    private readonly AnimationController _anim;
    private float _autoCollapseBelow;
    private bool _autoCollapsed;
    private bool _collapsed;
    private Widget? _content;
    private bool _entrancePlayed;
    private SidebarReveal? _reveal;
    private Container? _scrim;
    private Color _scrimColor;
    private bool _showSidebar = true;

    private Widget? _sidebar;

    // Retained width-bearing nodes: a sidebar resize adjusts these and re-lays out, instead of
    // rebuilding — a rebuild detaches and re-attaches the whole sidebar and content subtree, which
    // during a resize drag means doing that every frame.
    private Container? _sidebarBox;
    private Positioned? _sidebarSlot;
    private float _sidebarWidth = 260f;

    public AdwOverlaySplitView()
    {
        _anim = new AnimationController(durationSeconds: 0.25f, vsync: this) {
            Curve = Curves.EaseOut,
        };
        _anim.OnTick += OnAnimTick;
        _anim.OnDismissed += OnHidden;
        _anim.Complete(); // sidebar starts shown
    }

    public Widget? Sidebar
    {
        get => _sidebar;
        set => this.Set(field: ref _sidebar, value: value);
    }

    public Widget? Content
    {
        get => _content;
        set => this.Set(field: ref _content, value: value);
    }

    /// <summary>Whether the sidebar is visible (side-by-side uncollapsed, overlaid collapsed).</summary>
    public bool ShowSidebar
    {
        get => _showSidebar;
        set
        {
            if (_showSidebar == value) return;
            _showSidebar = value;
            if (Owner is null)
            {
                // Unattached (construction-time config): snap, don't animate.
                if (value) _anim.Complete();
                else _anim.Dismiss();
                Invalidate();
                return;
            }

            if (value)
            {
                // The hidden collapsed tree has no overlay layers — build them, then slide in.
                if (IsCollapsed) Invalidate();
                _anim.Forward();
            }
            else
                _anim.Reverse(); // overlay stays in the tree until OnHidden rebuilds without it
        }
    }

    /// <summary>Fired when the view itself changes the visibility (scrim click closes the overlay).</summary>
    public Action<bool>? OnShowSidebarChanged { get; set; }

    public bool Collapsed
    {
        get => _collapsed;
        set => this.Set(field: ref _collapsed, value: value);
    }

    /// <summary>
    ///     Auto-collapse when the available width drops below this (e.g. 720). 0 disables — the
    ///     default, so an overlay split view in a narrow demo frame stays side-by-side unless opted
    ///     in. Mirrors <see cref="AdwNavigationSplitView.AutoCollapseBelow" />.
    /// </summary>
    public float AutoCollapseBelow
    {
        get => _autoCollapseBelow;
        set => this.Set(field: ref _autoCollapseBelow, value: value);
    }

    /// <summary>
    ///     Collapsed for real: the host's flag or the breakpoint. Everything that branches on the
    ///     fold reads this, not <see cref="Collapsed" /> — otherwise a breakpoint-folded view builds
    ///     the overlay layers but never drops them again when the sidebar hides, and the scrim goes
    ///     on eating input over the content.
    /// </summary>
    private bool IsCollapsed => _collapsed || _autoCollapsed;

    /// <summary>
    ///     Width of the sidebar pane (and of the sheet when collapsed). Assigning re-lays the view
    ///     out — a host that narrows the sheet for a phone-width window would otherwise keep
    ///     painting the old width until something else happened to invalidate.
    /// </summary>
    public float SidebarWidth
    {
        get => _sidebarWidth;
        set
        {
            if (Math.Abs(_sidebarWidth - value) < 0.01f) return;
            _sidebarWidth = value;

            // Adjust the built tree in place where possible; only a tree that does not exist yet
            // needs building.
            bool adjusted = false;
            if (_sidebarBox is not null)
            {
                _sidebarBox.Width = value;
                adjusted = true;
            }

            if (_reveal is not null)
            {
                _reveal.FullWidth = value + 1f;
                adjusted = true;
            }

            if (_sidebarSlot is not null)
            {
                _sidebarSlot.Width = value;
                adjusted = true;
            }

            if (adjusted) MarkNeedsLayout();
            else Invalidate();
        }
    }

    private void OnAnimTick()
    {
        if (_scrim is not null)
            _scrim.Background = _scrimColor.WithAlpha(_scrimColor.A * _anim.Value);
        MarkNeedsLayout();
    }

    private void OnHidden()
    {
        // Fully hidden: drop the overlay layers so the scrim stops eating input.
        if (IsCollapsed) Invalidate();
    }

    // ── Ticker plumbing (same pattern as AdwToastOverlay) ──────────────────────


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        _anim.AttachTicker(this);
        // A collapsed overlay that mounts already-open plays its entrance — this is also what lets
        // rebuild-per-state consumers (a Watch around the whole view) animate the open, since they
        // hand over a fresh instance. Once only: a re-attach of the SAME instance (a theme change
        // rebuilds the tree above this one) must not replay a reveal the user already watched, and
        // AttachTicker on its own already resumes a transition that was in flight.
        if (IsCollapsed && _showSidebar && !_entrancePlayed)
        {
            _entrancePlayed = true;
            _anim.Dismiss();
            _anim.Forward();
        }
    }


    // ── Tree ───────────────────────────────────────────────────────────────────

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        _scrimColor = p.Scrim;

        if (AutoCollapseBelow <= 0f)
        {
            _autoCollapsed = false;
            return BuildPanes(theme: theme, p: p);
        }

        // One retained tree for the branch currently in force: the builder runs on every constraint
        // change, i.e. every frame of a window-resize drag, and handing back a fresh tree there
        // detaches and re-attaches the whole sidebar + content on each of those frames. Only
        // crossing the breakpoint rebuilds — which also keeps _reveal/_scrim/_sidebarSlot pointing
        // at the live tree, so a SidebarWidth change still lands.
        Widget? branch = null;
        bool branchCollapsed = false;
        return new LayoutBuilder((_, c) =>
            {
                bool auto = c.MaxWidth < AutoCollapseBelow;
                if (branch is null || auto != branchCollapsed)
                {
                    _autoCollapsed = auto;
                    branchCollapsed = auto;
                    branch = BuildPanes(theme: theme, p: p);
                }

                return branch;
            }
        );
    }

    private Widget BuildPanes(ThemeData theme, AdwColors p)
    {
        var content = new Container {
            Background = theme.Window,
            Child = Content,
        };

        if (!IsCollapsed)
        {
            _scrim = null;
            _sidebarSlot = null;
            _sidebarBox = new Container {
                Width = SidebarWidth,
                Background = p.SidebarBg,
                Child = Sidebar,
            };
            _reveal = new SidebarReveal(
                anim: _anim,
                fullWidth: SidebarWidth + 1f,
                child: new Row(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                    Children = {
                        _sidebarBox,
                        // `.sidebar-pane { box-shadow: inset -1px 0 var(--sidebar-border-color) }`.
                        new Container {
                            Width = 1f,
                            Background = p.SidebarBorder,
                        },
                    },
                }
            );

            // The reveal clips the full-width sidebar (+1 px separator) to an animated fraction,
            // sliding it out to the left; at 0 it takes no space and no input.
            return new Row(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                Children = {
                    _reveal,
                    new Expanded(content),
                },
            };
        }

        _reveal = null;
        _sidebarBox = null;
        if (!ShowSidebar &&
            _anim.Status is not (AnimationStatus.Forward or AnimationStatus.Reverse))
        {
            _scrim = null;
            _sidebarSlot = null;
            return content;
        }

        // Collapsed + shown (or animating): sidebar slides over the content behind a fading,
        // click-to-close scrim. Clipped: mid-slide the sidebar pokes out to the left of this view.
        _scrim = new Container {
            Background = _scrimColor.WithAlpha(_scrimColor.A * _anim.Value),
        };
        _sidebarSlot = new Positioned(
            child: new SlideTransition(
                controller: _anim,
                child: new DecoratedBox {
                    Fill = p.SidebarBg,
                    Elevation = Elevation.Z3,
                    Child = Sidebar,
                }
            ) { BeginOffset = new Offset(x: -SidebarWidth, y: 0f) },
            left: 0,
            top: 0,
            bottom: 0,
            width: SidebarWidth
        );
        return new ClipRect(
            new Stack {
                Children = {
                    content,
                    Positioned.Fill(new GestureDetector(child: _scrim, onTap: CloseSidebar)),
                    _sidebarSlot,
                },
            }
        );
    }

    private void CloseSidebar()
    {
        ShowSidebar = false;
        OnShowSidebarChanged?.Invoke(false);
    }

    /// <summary>
    ///     Reports an animated fraction of <paramref name="fullWidth" />, keeps the child measured at
    ///     the full width (no per-frame reflow of sidebar content), anchors it right and clips —
    ///     the sidebar slides out to the left while the content reflows into the freed space.
    /// </summary>
    private sealed class SidebarReveal(AnimationController anim, float fullWidth, Widget child)
        : Widget
    {
        private Size _size;

        /// <summary>Settable so a resize re-lays out rather than rebuilding the subtree.</summary>
        public float FullWidth { get; set; } = fullWidth;

        public override Size Measure(Constraints c)
        {
            var childSize = child.Measure(
                new Constraints(
                    minWidth: FullWidth,
                    maxWidth: FullWidth,
                    minHeight: c.MinHeight,
                    maxHeight: c.MaxHeight
                )
            );
            _size = new Size(width: MathF.Round(FullWidth * anim.Value), height: childSize.Height);
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
            // Right-anchored: the hidden part hangs off to the left, clipped in Paint.
            child.Layout(new Offset(x: origin.X + _size.Width - FullWidth, y: origin.Y));
        }

        public override void Paint(PaintList paint)
        {
            if (_size.Width < 0.5f) return;
            paint.AddClipStart(Bounds);
            child.Paint(paint);
            paint.AddClipEnd();
        }

        public override Widget? HitTest(Offset point) => Bounds.Contains(px: point.X, py: point.Y)
            ? child.HitTest(point)
            : null;

        public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(child);
    }
}
