using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.Core.State;
using Zigote.UI.Semantics;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets;

/// <summary>
///     Base class for all retained UI widgets.
///     Widgets are C# objects that persist across frames — their fields ARE their state.
///     Each frame the framework calls Measure → Layout → Paint in sequence.
///     Input is routed via HitTest + OnPointer* callbacks.
/// </summary>
public abstract class Widget : ITickerProvider
{
    [ThreadStatic] public static Widget? CurrentScrollParent;

    private bool _focused;
    private List<IDisposable>? _owned;

    protected Constraints LastConstraints;

    public Widget? Parent { get; set; }
    public App? Owner { get; set; }
    public Key? Key { get; set; }

    // Invalidation/Dirty flags
    public bool NeedsBuild { get; set; } = true;
    public bool NeedsLayout { get; set; } = true;
    public bool NeedsPaint { get; set; } = true;
    public Constraints DebugLastConstraints => LastConstraints;
    public Size MeasuredSize { get; protected set; }

    // Debug counters
    public int MeasureCount { get; set; }
    public int LayoutCount { get; set; }
    public int PaintCount { get; set; }
    public int RebuildCount { get; set; }

    /// <summary>
    ///     Absolute screen-space bounding rect, set by the parent during Layout.
    ///     Valid after <see cref="Layout" /> has been called.
    /// </summary>
    public Rect Bounds { get; set; }

    /// <summary>
    ///     The region this widget can actually paint into — used by the sub-rectangle partial-repaint
    ///     path (see <c>RepaintTracker</c>) to decide which pixels to redraw when this widget requests a
    ///     precise repaint. Defaults to <see cref="Bounds" />. Override on widgets that paint
    ///     <em>outside</em>
    ///     their bounds (drop shadows, glow) so the damaged region covers that overflow — otherwise a
    ///     partial repaint driven by this widget could leave stale pixels around it. The framework
    ///     additionally
    ///     inflates this by a small safety margin (focus rings, anti-aliasing), so only larger overflow
    ///     (e.g. an elevation shadow) needs an override.
    /// </summary>
    public virtual Rect DamageBounds => Bounds;

    /// <summary>Optional tooltip text shown after hovering for ~0.7 s.</summary>
    public virtual string? TooltipText => null;

    public virtual bool Focusable => false;

    public bool Focused
    {
        get => _focused;
        set
        {
            if (_focused == value) return;
            _focused = value;
            OnFocusChanged(value);
        }
    }

    public Widget? ScrollParent { get; set; }

    /// <summary>
    ///     Whether this widget consumes the arrow keys itself (caret movement, value step, list
    ///     navigation). When <c>false</c>, the app repurposes an arrow press on the focused widget for
    ///     <em>directional focus traversal</em> between sibling focusables. Text editors, sliders, and
    ///     steppers override this to <c>true</c> so their own arrow handling is never stolen.
    /// </summary>
    public virtual bool HandlesDirectionalKeys => false;

    /// <summary>
    ///     Stable accessibility identity, assigned lazily by <see cref="SemanticsBuilder" /> the first
    ///     time this widget contributes a node. 0 = not yet assigned. Lets a platform bridge diff the
    ///     semantics tree across frames by node identity rather than position.
    /// </summary>
    public int SemanticsId { get; set; }

    /// <summary>
    ///     When true this widget and its whole subtree are omitted from the accessibility tree (purely
    ///     decorative / off-screen content). Distinct from contributing an empty configuration, which
    ///     keeps the children visible to assistive tech.
    /// </summary>
    public virtual bool ExcludeSemantics => false;

    /// <summary>
    ///     Called when keyboard focus is gained/lost. Override for focus-aware widgets (e.g. close a
    ///     popup on blur).
    /// </summary>
    protected virtual void OnFocusChanged(bool focused)
    {
    }

    /// <summary>
    ///     Describe this widget to the accessibility layer by filling <paramref name="config" /> (role,
    ///     accessible name/value, state flags, supported actions). The default contributes nothing, which
    ///     makes the widget semantically transparent — its children are hoisted into the parent's node.
    ///     Override on every interactive control and on text/image leaves. See
    ///     <see cref="SemanticsBuilder" />.
    /// </summary>
    public virtual void DescribeSemantics(SemanticsConfiguration config)
    {
    }

    public virtual void Attach(App owner, Widget? parent)
    {
        Owner = owner;
        Parent = parent;
        EnsureMounted();
        // Snapshot before cascading: attaching a child can run app build code (Watch.EnsureStarted,
        // a lazy ComposedWidget build) that reconciles THIS widget's live child list mid-iteration —
        // GetChildren() returns the actual List for multi-child containers. Children added by such
        // a reconcile are attached by the reconcile itself, so iterating the snapshot loses nothing.
        foreach (var child in GetChildren().ToArray()) child.Attach(owner, this);
    }

    public virtual void Detach()
    {
        // Skip children another parent has since adopted: during an overlap transition (e.g.
        // AdaptiveBuilder cross-fading two subtrees that share a retained instance — the documented
        // "return the same grid, reconcile its children" pattern), the incoming subtree attaches
        // the shared child (re-parenting it) while the outgoing one is still fading. Cascading the
        // outgoing tree's detach into it would tear it out of the live tree. Sequential
        // detach-then-attach swaps are unaffected: at detach time the child still points here.
        foreach (var child in GetChildren().ToArray())
            if (ReferenceEquals(child.Parent, this))
                child.Detach();
        // Children first, then self: a teardown body must never observe a half-dead parent.
        // Runs while Owner is still set so OnUnmount can still reach the app (unregister a back
        // handler, release a text-input session).
        Unmount();
        // Drop app-level references (focus/hover/capture) to this widget before the Owner link goes
        // away — otherwise removing a focused/hovered widget via SetChildren, a Root swap, or a route
        // pop leaves App pointing at an off-tree widget (misrouted keys, stranded StartTextInput, a
        // focus ring painted on a widget that is no longer laid out). Cheap: a few reference compares.
        Owner?.NotifyDetached(this);
        Owner = null;
        Parent = null;
        // A detached widget may be re-attached later (a wrapper swapped around a retained subtree, a
        // route re-entered, a tab re-shown). Nothing in the re-attach path invalidates measure caches,
        // so a ComposedWidget ancestor whose NeedsLayout is still false, at unchanged
        // constraints and generation, early-returns its cached size and never re-measures the subtree
        // below it — which is the only thing that would re-Attach the descendants an unmounted subtree
        // stopped exposing. The result is a subtree with a null Owner: no Watch ever starts and it
        // renders blank. Flag it
        // here (no upward propagation — the parent link is already gone) so a re-attach always
        // re-measures. Costs nothing when the subtree really is being thrown away.
        NeedsLayout = true;
        NeedsPaint = true;
    }

    // ── Mount lifecycle ───────────────────────────────────────────────────────

    /// <summary>
    ///     True between <see cref="OnMount" /> and <see cref="OnUnmount" /> — i.e. while this widget is
    ///     live in a tree. A detached widget instance is not destroyed (its fields <em>are</em> its
    ///     state, and they survive), so re-attaching it mounts it again.
    /// </summary>
    public bool Mounted { get; private set; }

    /// <summary>
    ///     Start anything that must stop when this widget leaves the tree: signal subscriptions,
    ///     tickers, async work. Runs on attach and again on every re-attach, so it is paired 1:1 with
    ///     <see cref="OnUnmount" />. Register what you start via <see cref="Own{T}" /> /
    ///     <see cref="OwnEffect(Action)" /> and the teardown is automatic.
    ///     <para>
    ///         <b>Do not compose the child tree here</b> — this is a retained framework, so the child
    ///         widgets you keep in fields belong in the constructor or a field initializer, where they
    ///         are built once per instance instead of once per mount. Per-frame/per-theme wiring belongs
    ///         in <c>Build</c>.
    ///     </para>
    /// </summary>
    protected virtual void OnMount()
    {
    }

    /// <summary>
    ///     Counterpart to <see cref="OnMount" />, run when the widget leaves the tree. Everything
    ///     registered with <see cref="Own{T}" />/<see cref="OwnEffect(Action)" /> is disposed right
    ///     after this returns — override only for teardown that those cannot express.
    /// </summary>
    protected virtual void OnUnmount()
    {
    }

    /// <summary>
    ///     Fire <see cref="OnMount" /> if it has not run for the current mount period. Called from
    ///     <see cref="Attach" />, and again from the build owner before its first build — a widget can
    ///     legitimately be measured before anything attaches it (tests, off-tree measurement passes),
    ///     and mounting must not depend on which of the two happens first.
    /// </summary>
    protected void EnsureMounted()
    {
        if (Mounted) return;
        Mounted = true;
        OnMount();
    }

    private void Unmount()
    {
        if (!Mounted) return;
        Mounted = false;
        OnUnmount();
        if (_owned == null) return;
        // Reverse order: later registrations may depend on earlier ones (a controller owning a ticker).
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
    }

    /// <summary>
    ///     Tie <paramref name="disposable" />'s lifetime to this widget's mount period and return it
    ///     unchanged, so it reads as a wrapper: <c>Own(signal.Observe(Sync))</c>. Disposed on unmount,
    ///     in reverse registration order.
    /// </summary>
    protected T Own<T>(T disposable) where T : IDisposable
    {
        (_owned ??= []).Add(disposable);
        return disposable;
    }

    /// <summary>
    ///     Create an <see cref="Effect" /> owned by this widget: it runs now, re-runs whenever a signal
    ///     it read changes, and is disposed on unmount. Always use this rather than a bare
    ///     <c>new Effect(...)</c> — signals hold their observers strongly, so an unowned effect outlives
    ///     the widget and keeps re-running against a detached subtree.
    ///     <para>
    ///         This is also the framework's <b>finest-grained update</b>: have the body write straight
    ///         into the retained child widgets instead of returning a new subtree.
    ///         <code>
    ///   OwnEffect(() => _label.Text = $"Count: {count.Value}");
    /// </code>
    ///         Use it wherever a <see cref="Zigote.UI.Widgets.Watch" /> would only be swapping in a tree
    ///         of the same shape. A <c>Watch</c> allocates a fresh subtree per change, re-measures it and
    ///         throws the old one away (losing focus and press state inside it); this allocates nothing
    ///         and touches exactly the properties that changed — and the property setters raise whatever
    ///         invalidation they actually need, so a colour change repaints and never relayouts. Reach
    ///         for <c>Watch</c> when the tree's <em>shape</em> depends on the signal.
    ///     </para>
    ///     <para>
    ///         Thread rule: the body runs on whichever thread wrote the signal, so an effect fed from a
    ///         background thread must marshal to the UI thread itself (see <c>VideoControls</c>).
    ///     </para>
    /// </summary>
    protected Effect OwnEffect(Action body)
    {
        return Own(new Effect(body));
    }

    /// <summary><see cref="OwnEffect(Action)" /> for a body returning a cleanup thunk (run before each re-run and on dispose).</summary>
    protected Effect OwnEffect(Func<Action> bodyWithCleanup)
    {
        return Own(new Effect(bodyWithCleanup));
    }

    /// <summary>
    ///     Put <paramref name="next" /> in this widget's single-child slot and tear down
    ///     <paramref name="previous" /> — unless the new tree re-adopted it. Every widget that rebuilds
    ///     a cached child goes through here, because the ORDER is load-bearing in two directions:
    ///     <list type="number">
    ///         <item>
    ///             <b>Attach first, always</b> — even when <paramref name="next" /> is the same instance.
    ///             A retained root whose contents changed (a Container given a fresh child, an overlay
    ///             re-pointed at a new page) has newly-inserted descendants that have never been mounted,
    ///             and this cascade is what gives them an Owner. Skip it and they keep a null Owner, so
    ///             every Watch inside them never starts and the subtree renders blank.
    ///         </item>
    ///         <item>
    ///             <b>Detach second, and only what was really dropped.</b> The common
    ///             "wrap/unwrap a retained subtree" build — a sheet or scrim returning <c>content</c> when
    ///             closed and <c>new Stack { content, … }</c> when open — would otherwise tear
    ///             <c>content</c> down (unmounting every widget, scroll offset and focus inside it) only
    ///             to re-attach it one line later. Attaching first re-parents the shared subtree, so the
    ///             re-adoption check below sees it and leaves it alone; only the genuinely-dropped wrapper
    ///             is detached, and <see cref="Detach" />'s own re-adoption check keeps that cascade off
    ///             the shared child.
    ///         </item>
    ///     </list>
    /// </summary>
    protected void SwapChild(Widget? previous, Widget? next)
    {
        if (next != null && Owner != null) next.Attach(Owner, this);
        if (ReferenceEquals(previous, next) || previous is null) return;
        if (previous.Parent is null || ReferenceEquals(previous.Parent, this)) previous.Detach();
    }

    /// <summary>
    ///     Create a <see cref="Ticker" /> owned by this widget's mount period — the
    ///     <see cref="ITickerProvider" /> every <c>AnimationController(…, vsync: this)</c> wants. Muted
    ///     while unmounted, disposed on unmount.
    /// </summary>
    public Ticker CreateTicker(Action<float> onTick)
    {
        return Own(new Ticker(onTick) { Muted = !Mounted });
    }

    // ── Invalidating property setters ─────────────────────────────────────────
    //
    // The body of nearly every public widget property: store it if it changed, then raise the
    // CHEAPEST invalidation that actually covers the change. Picking the right one of these three is
    // most of this framework's per-frame performance, so they are named rather than folded into one
    // call with a flag — `set => SetPaint(ref _color, value);` says at a glance that a colour change
    // can never move anything.
    //
    // Change detection is EqualityComparer<T>.Default, which for float/double differs from `==` on
    // exactly two inputs, both in the safe direction: NaN equals NaN (so a NaN size settles instead
    // of re-invalidating every frame forever), and -0.0 does not equal 0.0 (one redundant repaint).
    // Color/EdgeInsets and friends define `operator ==` as Equals, so they are unaffected.

    /// <summary>Store <paramref name="value" /> and rebuild if it differs. Use when the child <em>structure</em> depends on it.</summary>
    protected bool SetBuild<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        MarkNeedsBuild();
        return true;
    }

    /// <summary>Store <paramref name="value" /> and relayout if it differs. Use when the measured size may change.</summary>
    protected bool SetLayout<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        MarkNeedsLayout();
        return true;
    }

    /// <summary>Store <paramref name="value" /> and repaint if it differs. Use when the size provably cannot change.</summary>
    protected bool SetPaint<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        MarkNeedsPaint();
        return true;
    }

    public virtual void MarkNeedsBuild()
    {
        NeedsBuild = true;
        MarkNeedsLayout();
    }

    public virtual void MarkNeedsLayout()
    {
        // No early-out on NeedsLayout: the per-widget flag is only reset by the ComposedWidget/
        // ComposedWidget wrappers in Measure — raw control widgets never reset it, so guarding on it
        // would make repeated calls (e.g. a drag emitting MarkNeedsLayout every move) no-op after the
        // first. The propagation is cheap (interaction-rate, tree-depth bounded), so always run it.
        NeedsLayout = true;
        NeedsPaint = true;
        Parent?.MarkNeedsLayout();
        RequestLayout();
    }

    public virtual void MarkNeedsPaint()
    {
        // Always request a repaint — see MarkNeedsLayout: the NeedsPaint flag isn't reliably reset per
        // frame, so a guard here would make continuous repaint requests (pointer-tracking glow, slider
        // thumb drag) fire only once and then freeze until the next discrete event.
        NeedsPaint = true;
        RequestFrame();
    }

    protected void RequestLayout()
    {
        Owner?.RequestLayout();
        if (Owner == null)
            App.Active?.RequestLayout();
    }

    protected void RequestFrame()
    {
        // Scope the repaint to this widget so a self-repaint (hover glow, slider thumb, caret) damages
        // only its own region + layer instead of full-clearing the frame. The App degrades to a full
        // repaint when the region is unknown or partial repaint is disabled. UI-thread only, like
        // MarkNeedsLayout above (both walk the tree to resolve the widget's layer).
        (Owner ?? App.Active)?.RequestPaintFor(this);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Bottom-up pass. Compute and return the size this widget wants given
    ///     <paramref name="constraints" />. Stored internally; parent reads it back
    ///     through the return value.
    /// </summary>
    public abstract Size Measure(Constraints constraints);

    /// <summary>
    ///     Top-down pass. Set <see cref="Bounds" /> using <paramref name="origin" />
    ///     (absolute screen position) plus the size computed in
    ///     <see cref="Measure" />. Container widgets must also call Layout on every
    ///     child here.
    /// </summary>
    public abstract void Layout(Offset origin);

    // ── Paint ─────────────────────────────────────────────────────────────────

    /// <summary>Emit paint commands into <paramref name="paint" />.</summary>
    public abstract void Paint(PaintList paint);

    // ── Input ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Return the deepest widget under <paramref name="point" /> (screen coords),
    ///     or null if this widget does not cover the point.
    /// </summary>
    public virtual Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    public virtual void OnPointerDown(Offset point)
    {
    }

    public virtual void OnPointerUp(Offset point)
    {
    }

    public virtual void OnPointerMove(Offset point)
    {
    }

    /// <summary>
    ///     Raw pointer motion, in logical pixels, delivered while the pointer is captured
    ///     (<see cref="Core.Engine.ZigoteEngine.SetRelativeMouseMode" />).
    ///     <para>
    ///         Capture hides the cursor and pins it in place, so there is no position to report and
    ///         <see cref="OnPointerMove" /> stops arriving; this replaces it. The widget holding focus
    ///         receives the deltas — that is the one driving the camera. Ignore it unless you asked for
    ///         capture.
    ///     </para>
    /// </summary>
    public virtual void OnPointerRelative(float deltaX, float deltaY)
    {
    }

    public virtual void OnPointerEnter()
    {
    }

    public virtual void OnPointerExit()
    {
    }

    /// <summary>
    ///     The mouse cursor this widget wants shown when the pointer is at <paramref name="point" />
    ///     (screen coords), or null to defer to an ancestor / the default arrow. The app queries the
    ///     widget under the pointer (or the one capturing a drag) each move and walks up the parent
    ///     chain until a non-null value is found. Override to show resize / hand / text cursors, e.g. a
    ///     split divider returns <see cref="MouseCursor.ResizeEW" /> over its hit region.
    /// </summary>
    public virtual MouseCursor? GetCursor(Offset point)
    {
        return null;
    }

    public virtual void OnRightClick(Offset point)
    {
    }

    public virtual void OnRightPointerUp(Offset point)
    {
    }

    /// <summary>
    ///     The pointer sequence this widget was tracking ended without a logical up: a touch was
    ///     cancelled (OS gesture takeover, app backgrounded) or an app-level gesture — touch
    ///     drag-to-scroll, long-press — claimed the pointer after this widget already saw
    ///     <see cref="OnPointerDown" />. Abandon the interaction: clear pressed visuals and drag
    ///     state, commit nothing (no tap, no click). Widgets that track state across down→up
    ///     must override this alongside <see cref="OnPointerUp" />.
    /// </summary>
    public virtual void OnPointerCancel()
    {
    }

    /// <summary>
    ///     A touch was held in place past the long-press threshold. The default maps it to
    ///     <see cref="OnRightClick" /> — on touch screens a long-press is the context-menu
    ///     gesture, so right-click-driven menus work unchanged. Override to attach a distinct
    ///     long-press behavior (see <c>GestureDetector.onLongPress</c>).
    /// </summary>
    public virtual void OnLongPress(Offset point)
    {
        OnRightClick(point);
    }

    public virtual void OnScroll(float dx, float dy)
    {
        ScrollParent?.OnScroll(dx, dy);
    }

    // ── Touch scrolling ─────────────────────────────────────────────────────────
    //
    // A touch drag that exceeds the slop distance becomes a scroll gesture when a widget in the
    // hit chain can consume its dominant axis (the App asks via CanTouchScroll, walking hit →
    // ScrollParent…). Unlike wheel OnScroll — whose deltas are in wheel ticks and get a speed
    // multiplier — these deltas are raw finger pixels and must track 1:1.

    /// <summary>
    ///     Can this widget consume a touch drag along the given axis right now? True only when
    ///     genuinely scrollable there (content overflows) — a false lets the drag fall through to
    ///     the pressed widget (e.g. a horizontal slider inside a vertical list keeps horizontal
    ///     drags). Default: no.
    /// </summary>
    public virtual bool CanTouchScroll(bool vertical)
    {
        return false;
    }

    /// <summary>
    ///     Is the press this widget is already handling a drag of its own — a slider being scrubbed,
    ///     a split divider being moved, a chart being panned? Answering true keeps the surrounding
    ///     scrollable from taking the gesture away: the App asks the pressed widget BEFORE it offers
    ///     the drag to <see cref="CanTouchScroll" />, so the control the finger is on wins.
    ///     <para>
    ///         Both platforms do exactly this — iOS exempts <c>UIControl</c>s from a scroll view's
    ///         touch cancellation, and Android's SeekBar calls
    ///         <c>requestDisallowInterceptTouchEvent</c> the moment a finger takes the thumb.
    ///         Without it a fader inside a scrolling page could never be moved (every drag on it is
    ///         vertical, so the page always claimed it), and any slider lost the whole gesture when
    ///         the finger settled downward before setting off sideways.
    ///     </para>
    ///     <para>
    ///         A control that has committed to a scrub claims BOTH axes: the press already means "I
    ///         am adjusting this", and letting the page steal the perpendicular direction is exactly
    ///         what makes touch sliders feel broken. A large surface that merely pans (a chart)
    ///         claims only the axes it can actually pan, so the page still scrolls where the surface
    ///         has nothing to do with the finger. Answer false when the press started no drag at
    ///         all — pressing a control's row and then scrolling the page must keep working.
    ///         Gestures that begin with a long-press (drag-to-reorder, <c>Draggable</c>) need
    ///         nothing here: the App already stops arbitrating once a long-press fires.
    ///     </para>
    /// </summary>
    public virtual bool CanTouchDrag(bool vertical)
    {
        return false;
    }

    /// <summary>
    ///     Scroll by a finger-drag delta in logical pixels (positive = the finger moved
    ///     right/down; content follows the finger). Unconsumable remainder bubbles to
    ///     <see cref="ScrollParent" /> like wheel scrolling.
    /// </summary>
    public virtual void OnTouchScroll(float dx, float dy)
    {
        ScrollParent?.OnTouchScroll(dx, dy);
    }

    /// <summary>
    ///     The scrolling finger lifted with residual velocity (logical px/sec, finger-direction
    ///     signs like <see cref="OnTouchScroll" />). Start inertial scrolling from it.
    /// </summary>
    public virtual void OnTouchFling(float velocityX, float velocityY)
    {
        ScrollParent?.OnTouchFling(velocityX, velocityY);
    }

    // ── Pinch-to-zoom ───────────────────────────────────────────────────────────
    //
    // A second finger down turns the gesture into a pinch. The App walks hit → Parent… for the
    // first widget that answers CanTouchScale, cancels whatever the first finger had pressed, and
    // from then until a finger lifts drives OnTouchScale (spread/squeeze) and OnTouchScroll
    // (the two-finger centroid moving — panning the zoomed content) on that widget.

    /// <summary>
    ///     Can this widget consume a two-finger pinch right now? Default: no, which lets the
    ///     gesture fall through to an ancestor (a zoomable page inside a scrolling list).
    /// </summary>
    public virtual bool CanTouchScale()
    {
        return false;
    }

    /// <summary>
    ///     Scale by <paramref name="scale" /> (a per-event multiplier: &gt;1 the fingers spread,
    ///     &lt;1 they closed) about <paramref name="focus" /> in window coordinates — the point
    ///     under the fingers' centroid, which must stay put as the content scales, or the zoom
    ///     drifts away from what the user is holding.
    /// </summary>
    public virtual void OnTouchScale(float scale, Offset focus)
    {
    }

    // ── Drag-and-drop targets ───────────────────────────────────────────────────
    //
    // The same four hooks serve BOTH external OS drops (files/text dragged onto the window) and in-app
    // drags (a Draggable's payload). The App finds a drop target by hit-testing under the pointer and
    // walking up the parent chain to the first widget whose CanAcceptDrop returns true, then drives its
    // enter/leave/drop. Widgets that don't accept drops (the default) are simply skipped.

    /// <summary>
    ///     Pure predicate: can this widget accept <paramref name="data" />? Called during hit-testing to
    ///     locate a drop target — must have no side effects (highlighting belongs in
    ///     <see cref="OnDragEnter" />). Default: no.
    /// </summary>
    public virtual bool CanAcceptDrop(DragData data)
    {
        return false;
    }

    /// <summary>The pointer carrying an acceptable payload entered this target — turn on hover feedback.</summary>
    public virtual void OnDragEnter(DragData data)
    {
    }

    /// <summary>The drag left this target (moved off it or was released/cancelled) — turn off feedback.</summary>
    public virtual void OnDragLeave()
    {
    }

    /// <summary>The payload was released over this target. Perform the drop.</summary>
    public virtual void OnDrop(DragData data, Offset point)
    {
    }

    /// <summary>
    ///     Returns a hash that represents this widget's current visual state.
    ///     The devtools repaint-rainbow layer calls this every frame; when the value changes the widget
    ///     is considered "redrawn" and gets a rainbow border highlight.
    ///     Override in stateful widgets to include mutable fields that affect Paint output.
    ///     Default includes Bounds so any layout change is also detected.
    /// </summary>
    public virtual int DebugStateHash()
    {
        return HashCode.Combine(
            Bounds.X,
            Bounds.Y,
            Bounds.Width,
            Bounds.Height
        );
    }

    /// <summary>Called when this widget has keyboard focus and a key is pressed/released.</summary>
    public virtual void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
    }

    /// <summary>Called when this widget has keyboard focus and text input is received.</summary>
    public virtual void OnTextInput(string text)
    {
    }

    /// <summary>
    ///     Called for transient IME pre-edit updates. An empty string cancels the active composition.
    ///     The selection offsets describe the IME's selected subrange in its native UTF-8 units.
    /// </summary>
    public virtual void OnTextComposition(string text, int selectionStart, int selectionLength)
    {
    }

    /// <summary>
    ///     Returns the direct children of this widget.
    ///     Used by the framework for focus traversal and tree walks.
    ///     Override in every container widget; the default returns an empty sequence.
    /// </summary>
    public virtual IEnumerable<Widget> GetChildren()
    {
        return [];
    }

    /// <summary>
    ///     The children that are actually SHOWN right now — what focus traversal and the semantics
    ///     tree walk, so Tab (and a screen reader) can never land on an invisible control.
    ///     Defaults to <see cref="GetChildren" />; containers that keep hidden children attached
    ///     (<c>TabView</c> pages, covered navigator routes) override this to expose only the
    ///     visible subset. Lifecycle walks (attach/detach, hot reload) stay on
    ///     <see cref="GetChildren" /> — hidden children remain part of the tree.
    /// </summary>
    public virtual IEnumerable<Widget> GetVisibleChildren()
    {
        return GetChildren();
    }

    // Cached single-child snapshot for GetChildren: a collection-expression `[Child]` allocates a
    // wrapper per call, and per-frame tree walks (devtools layers, semantics, hot reload) call
    // GetChildren on every widget. Re-allocated only when the child reference changes; a returned
    // array is never mutated afterwards, so stale holders keep a stable snapshot.
    private Widget[]? _singleChildCache;

    /// <summary>Allocation-free `[Child]` for single-child <see cref="GetChildren" /> overrides.</summary>
    protected IEnumerable<Widget> ChildOrEmpty(Widget? child)
    {
        if (child is null) return Array.Empty<Widget>();
        var cached = _singleChildCache;
        if (cached is null || !ReferenceEquals(cached[0], child))
            _singleChildCache = cached = [child];
        return cached;
    }

    /// <summary>
    ///     Copy configuration from <paramref name="newWidget" /> — a freshly-built widget of the same
    ///     runtime type — onto this retained instance. Called by <see cref="ChildReconciler" /> when a
    ///     keyed child is reused across a rebuild, so the instance (and its transient state: hover,
    ///     scroll position, in-flight animation) survives while its configuration updates.
    ///     Default: no-op — the instance keeps its current configuration.
    /// </summary>
    public virtual void UpdateFrom(Widget newWidget)
    {
    }
}