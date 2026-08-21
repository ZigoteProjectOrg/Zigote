using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Navigation;

/// <summary>
///     <see cref="InheritedWidget" /> that exposes the <see cref="Navigator" /> to descendants so
///     <c>Navigator.Of(context)</c> and the <c>context.Push/Pop</c> extensions can find it.
/// </summary>
public sealed class NavigatorScope : InheritedWidget
{
    public NavigatorScope(Navigator state, Widget child)
    {
        State = state;
        Child = child;
    }

    public Navigator State { get; }

    public override bool UpdateShouldNotify(InheritedWidget oldWidget) =>
        oldWidget is not NavigatorScope s || !ReferenceEquals(objA: s.State, objB: State);
}

/// <summary>
///     The render host for a navigator's route stack. Lays out every visible route at full size,
///     applies each route's slide/fade transition, paints bottom-to-top (skipping routes fully hidden
///     behind an opaque, settled route) and routes input to the current (topmost non-popping) route.
/// </summary>
internal sealed class NavigatorBody : Widget
{
    private readonly Navigator _state;
    private Size _size;

    public NavigatorBody(Navigator state) => _state = state;

    private IReadOnlyList<Route> Routes => _state.History;

    public override Size Measure(Constraints c)
    {
        if (Debug.WidgetDebug.CountersEnabled) MeasureCount++;
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        var tight = Constraints.Tight(width: _size.Width, height: _size.Height);

        var routes = Routes;
        int first = FirstLayoutIndex(routes);
        for (int i = 0; i < routes.Count; i++)
        {
            // Every route keeps its content built and attached (state preservation) — only the
            // measure itself is skipped for covered routes.
            var content = routes[i].EnsureContent(BuildContext.Current);
            if (content.Owner is null && Owner is not null)
                content.Attach(owner: Owner, parent: this);
            if (i >= first) content.Measure(tight);
        }

        return _size;
    }

    public override void Layout(Offset origin)
    {
        if (Debug.WidgetDebug.CountersEnabled) LayoutCount++;
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );

        var routes = Routes;
        for (int i = FirstLayoutIndex(routes); i < routes.Count; i++)
        {
            var r = routes[i];
            var content = r.ContentOrNull;
            if (content is null) continue;
            var off = r.TransitionOffset(size: _size, t: r.Transition.Value);
            content.Layout(new Offset(x: origin.X + off.X, y: origin.Y + off.Y));
        }
    }

    public override void Paint(PaintList paint)
    {
        if (Debug.WidgetDebug.CountersEnabled) PaintCount++;
        var routes = Routes;
        if (routes.Count == 0) return;

        for (int i = FirstVisibleIndex(routes); i < routes.Count; i++)
        {
            var r = routes[i];
            var content = r.ContentOrNull;
            if (content is null) continue;

            float op = r.TransitionOpacity(r.Transition.Value);
            if (op <= 0.001f) continue;

            if (op < 0.999f)
            {
                paint.PushAlpha(op);
                content.Paint(paint);
                paint.PopAlpha();
            }
            else
                content.Paint(paint);
        }
    }

    // Measure/Layout window: while every route is settled, routes under the topmost opaque one can
    // neither move nor become visible, so they keep last layout's geometry (Paint already skips them
    // via FirstVisibleIndex). Any running transition lays out the whole stack — a route being
    // revealed by a pop must have fresh geometry from its first visible frame.
    private static int FirstLayoutIndex(IReadOnlyList<Route> routes)
    {
        for (int i = 0; i < routes.Count; i++)
        {
            if (routes[i].Status != RouteStatus.Idle)
                return 0;
        }

        return FirstVisibleIndex(routes);
    }

    // Start painting at the topmost opaque, fully-settled route; everything below it is obscured.
    private static int FirstVisibleIndex(IReadOnlyList<Route> routes)
    {
        for (int i = routes.Count - 1; i >= 0; i--)
        {
            var r = routes[i];
            if (r.Opaque && r.Status == RouteStatus.Idle && r.Transition.Value >= 0.999f)
                return i;
        }

        return 0;
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        // Input is modal — only the current (topmost non-popping) route is interactive. Returning
        // this body for misses makes it a barrier so clicks never fall through to routes beneath.
        var hit = _state.CurrentRoute?.ContentOrNull?.HitTest(point);
        return hit ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        foreach (var r in Routes)
        {
            if (r.ContentOrNull is { } content)
                yield return content;
        }
    }

    /// <summary>
    ///     Focus/semantics parity with input modality: only the current route is interactive
    ///     (<see cref="HitTest" /> is a barrier), so only its content is focus-reachable — Tab must
    ///     not cycle into controls on routes covered by the one on top.
    /// </summary>
    public override IEnumerable<Widget> GetVisibleChildren()
    {
        if (_state.CurrentRoute?.ContentOrNull is { } current)
            yield return current;
    }
}

/// <summary>
///     A stack of routes, supporting both the imperative API (<c>Push</c>/<c>Pop</c>, named routes)
///     and the declarative <b>Navigator 2.0</b> page API (<see cref="Pages" /> +
///     <see cref="OnPopPage" />).
///     The stack is kept as two ordered sections — declarative <c>Pages</c> at the bottom and
///     imperatively-pushed routes on top — plus a transient set of routes still animating out.
///     <para>
///         <see cref="ZigoteApp" /> wraps <c>Home</c> in a root Navigator automatically, so any widget
///         can navigate with <c>context.Push(...)</c> / <c>context.Pop()</c>. Insert nested Navigators
///         for sub-flows (e.g. a wizard inside a panel).
///     </para>
///     <para>Look up the nearest navigator with <see cref="Of" /> / <see cref="MaybeOf" />.</para>
/// </summary>
public sealed class Navigator : ComposedWidget
{
    private readonly NavigatorBody _body;

    // Routes removed from the stack but still animating out (painted on top until gone).
    private readonly List<Route> _exiting = [];

    // Combined bottom→top render order, rebuilt on every structural change.
    private readonly List<Route> _history = [];

    // Declarative (Navigator 2.0) routes, bottom→top, matching Navigator.Pages.
    private readonly List<Route> _pages = [];

    // Imperatively-pushed routes, stacked on top of the pages.
    private readonly List<Route> _pushed = [];
    private readonly NavigatorScope _scope;
    private bool _backRegistered;

    public Navigator()
    {
        _body = new NavigatorBody(this);
        _scope = new NavigatorScope(state: this, child: _body);
    }

    /// <summary>Declarative page stack (Navigator 2.0). When set, describes the initial routes.</summary>
    public List<Page>? Pages { get; set; }

    /// <summary>
    ///     Called when a page-based route asks to pop (system back, <c>BackButton</c>, <c>Pop()</c>).
    ///     Return true to allow the pop (the navigator animates the page out and removes it); return
    ///     false to veto it. If null, page pops are always allowed.
    /// </summary>
    public Func<Route, object?, bool>? OnPopPage { get; set; }

    /// <summary>The base content when no <see cref="Pages" /> / named initial route is supplied.</summary>
    public Widget? Home { get; set; }

    /// <summary>Name resolved at startup (via <see cref="Routes" /> / <see cref="OnGenerateRoute" />).</summary>
    public string InitialRoute { get; set; } = "/";

    /// <summary>Named route table: name → content builder.</summary>
    public Dictionary<string, WidgetBuilder>? Routes { get; set; }

    /// <summary>Fallback factory for names not found in <see cref="Routes" />.</summary>
    public RouteFactory? OnGenerateRoute { get; set; }

    /// <summary>Last-resort factory when a name resolves to nothing.</summary>
    public RouteFactory? OnUnknownRoute { get; set; }

    /// <summary>Combined route stack in render order (bottom → top).</summary>
    public IReadOnlyList<Route> History => _history;

    /// <summary>True if there is a route that <see cref="Pop" /> can remove.</summary>
    public bool CanPop => _pushed.Count > 0 || _pages.Count > 1;

    /// <summary>The topmost route currently receiving input (skips routes animating out).</summary>
    public Route? CurrentRoute
    {
        get
        {
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].Status != RouteStatus.Popping)
                    return _history[i];
            }

            return _history.Count > 0 ? _history[^1] : null;
        }
    }

    /// <summary>A predicate matching a route by its settings name — for <c>PopUntil</c>.</summary>
    public static Predicate<Route> WithName(string name) => r => r.Settings.Name == name;

    /// <summary>The nearest enclosing navigator. Throws if none is found.</summary>
    public static Navigator Of(BuildContext context)
    {
        return context.FindAncestor<NavigatorScope>()?.State
               ?? throw new InvalidOperationException(
                   "No Navigator found in the widget tree. Wrap your UI in a ZigoteApp (which "
                   + "installs a root Navigator) or add a Navigator widget above this point."
               );
    }

    /// <summary>The nearest enclosing navigator, or null if none is found.</summary>
    public static Navigator? MaybeOf(BuildContext context) =>
        context.FindAncestor<NavigatorScope>()?.State;

    // The route stack is per-mount: it is torn down in OnUnmount, so a re-attached navigator starts
    // from its declared Pages/Home again — the same lifetime the state object used to give it.
    // Not the constructor: Pages/Home/InitialRoute arrive via the object initialiser, after it runs.
    protected override void OnMount() => BuildInitialRoutes();

    /// <summary>Pop for the system back action; false when this navigator is at its root.</summary>
    private bool HandleSystemBack() => MaybePop();

    protected override Widget Build(BuildContext context)
    {
        // The system back action (Android's back gesture/button, an iOS edge swipe) pops this
        // navigator. Registered here rather than in OnMount because the owning App is only
        // known once the widget is attached; registration order also gets the nesting right —
        // the innermost navigator registers last and so receives the gesture first.
        if (!_backRegistered && Owner is { } app)
        {
            app.AddBackHandler(HandleSystemBack);
            _backRegistered = true;
        }

        return _scope;
    }

    protected override void OnUnmount()
    {
        if (_backRegistered)
        {
            Owner?.RemoveBackHandler(HandleSystemBack);
            _backRegistered = false;
        }

        // Complete any pending Popped task (an awaited context.Push) and detach content — otherwise a
        // navigator torn down mid-flow leaves awaiters hung forever and route subtrees attached.
        // CompleteWith is TrySetResult-based, so it is safe to call unconditionally.
        foreach (var r in _history)
        {
            r.CompleteWith(r.PendingResult);
            r.ContentOrNull?.Detach();
            r.DisposeRoute();
        }

        _pages.Clear();
        _pushed.Clear();
        _exiting.Clear();
        _history.Clear();
    }

    // ── Imperative API ────────────────────────────────────────────────────────

    /// <summary>Push a route onto the stack; the returned task completes when it is popped.</summary>
    public Task<T?> Push<T>(Route<T> route)
    {
        AddPushed(route);
        return route.Popped;
    }

    /// <summary>Push a page built from <paramref name="builder" />.</summary>
    public Task<object?> Push(WidgetBuilder builder, RouteSettings? settings = null) => Push(
        new MaterialPageRoute<object?>(builder: builder, settings: settings)
    );

    /// <summary>Push a page showing the given widget.</summary>
    public Task<object?> Push(Widget page) => Push(_ => page);

    /// <summary>Resolve and push a named route (via <c>Routes</c> / <c>OnGenerateRoute</c>).</summary>
    public Task<object?> PushNamed(string name, object? arguments = null)
    {
        var route = GenerateRoute(new RouteSettings(Name: name, Arguments: arguments))
                    ?? throw new InvalidOperationException(
                        $"Navigator.PushNamed: no route registered for '{name}'."
                    );
        AddPushed(route);
        return (route as Route<object?>)?.Popped ?? Task.FromResult<object?>(null);
    }

    /// <summary>Push <paramref name="route" />, removing the route it replaces once it has entered.</summary>
    public Task<T?> PushReplacement<T>(Route<T> route, object? result = null)
    {
        // Wire the removal before pushing: a zero-duration route enters (and fires OnEntered)
        // synchronously inside Push, so assigning it afterwards would miss the callback.
        var replaced = CurrentRoute;
        if (replaced is not null)
            route.OnEntered = () => RemoveInstant(r: replaced, result: result);
        return Push(route);
    }

    /// <summary>Replace the current route with a named one.</summary>
    public Task<object?> PushReplacementNamed(string name, object? arguments = null,
        object? result = null)
    {
        var route = GenerateRoute(new RouteSettings(Name: name, Arguments: arguments))
                    ?? throw new InvalidOperationException(
                        $"Navigator.PushReplacementNamed: no route registered for '{name}'."
                    );
        var replaced = CurrentRoute;
        if (replaced is not null)
            route.OnEntered = () => RemoveInstant(r: replaced, result: result);
        AddPushed(route);
        return (route as Route<object?>)?.Popped ?? Task.FromResult<object?>(null);
    }

    /// <summary>Pop the topmost route, completing it with <paramref name="result" />.</summary>
    public void Pop(object? result = null)
    {
        if (_pushed.Count > 0)
        {
            var r = _pushed[^1];
            if (r.Status != RouteStatus.Popping) BeginPop(r: r, result: result);
            return;
        }

        if (_pages.Count > 1)
        {
            var r = _pages[^1];
            if (r.Status == RouteStatus.Popping) return;
            var handler = OnPopPage;
            if (handler is null || handler(arg1: r, arg2: result)) BeginPop(r: r, result: result);
        }
    }

    /// <summary>Pop only if <see cref="CanPop" />. Returns whether a pop occurred.</summary>
    public bool MaybePop(object? result = null)
    {
        if (!CanPop) return false;
        Pop(result);
        return true;
    }

    /// <summary>Pop routes from the top until <paramref name="predicate" /> matches the top route.</summary>
    public void PopUntil(Predicate<Route> predicate)
    {
        int guard = 0;
        while (_pushed.Count > 0 && !predicate(_pushed[^1]) && guard++ < 1024)
            RemoveInstant(_pushed[^1]);
        while (_pushed.Count == 0 && _pages.Count > 1 && !predicate(_pages[^1]) && guard++ < 1024)
            RemoveInstant(_pages[^1]);
    }

    // ── Declarative API (Navigator 2.0) ───────────────────────────────────────

    /// <summary>
    ///     Reconcile the declarative page stack against <paramref name="pages" />: pages matched (by
    ///     <see cref="Page.Key" />, else by reference) keep their live route and state; new pages
    ///     animate in; pages no longer present animate out. Imperatively-pushed routes are untouched.
    /// </summary>
    public void SetPages(IReadOnlyList<Page> pages)
    {
        var matched = new HashSet<Route>();
        var next = new List<Route>(pages.Count);
        bool hadPages = _pages.Count > 0;

        foreach (var p in pages)
        {
            var existing = FindPageRoute(p: p, taken: matched);
            if (existing is not null)
            {
                existing.SourcePage = p;
                existing.Settings = p.ToSettings();
                matched.Add(existing);
                next.Add(existing);
            }
            else
            {
                var route = AdoptPageRoute(p);
                next.Add(route);
                route.StartEnter(hadPages); // first ever page list appears instantly
            }
        }

        // Collect pages that disappeared from the list before mutating _pages — a zero-duration exit
        // removes its route synchronously, which would corrupt enumeration of _pages.
        var exits = new List<Route>();
        foreach (var r in _pages)
        {
            if (!matched.Contains(r) && r.Status != RouteStatus.Popping)
                exits.Add(r);
        }

        _pages.Clear();
        _pages.AddRange(next);

        foreach (var r in exits)
        {
            _exiting.Add(r);
            r.StartExit();
        }

        RebuildHistory();
        _body.MarkNeedsLayout();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void BuildInitialRoutes()
    {
        if (Pages is { Count: > 0 })
        {
            foreach (var p in Pages)
            {
                var r = AdoptPageRoute(p);
                r.StartEnter(false);
                _pages.Add(r);
            }
        }
        else
        {
            Route? initial = null;
            if (Routes is not null || OnGenerateRoute is not null)
                initial = GenerateRoute(new RouteSettings(InitialRoute));
            initial ??= Home is not null ? new WidgetRoute(Home) : null;

            if (initial is not null)
            {
                Install(initial);
                initial.StartEnter(false);
                _pages.Add(initial);
            }
        }

        RebuildHistory();
    }

    private Route? GenerateRoute(RouteSettings settings)
    {
        if (settings.Name is not null && Routes is not null &&
            Routes.TryGetValue(key: settings.Name, value: out var builder))
        {
            var r = new MaterialPageRoute<object?>(builder: builder, settings: settings);
            Install(r);
            return r;
        }

        var generated = OnGenerateRoute?.Invoke(settings) ?? OnUnknownRoute?.Invoke(settings);
        if (generated is not null) Install(generated);
        return generated;
    }

    private Route AdoptPageRoute(Page p)
    {
        var r = p.CreateRoute();
        r.IsPageBased = true;
        r.SourcePage = p;
        Install(r);
        return r;
    }

    private void Install(Route r)
    {
        r.Navigator = this;
        r.OnVisualUpdate = OnRouteVisualUpdate;
        r.OnExitComplete = () => RemoveInstant(r);
    }

    private void AddPushed(Route route)
    {
        Install(route);
        _pushed.Add(route);
        RebuildHistory();
        route.StartEnter(true);
        _body.MarkNeedsLayout();
    }

    private void BeginPop(Route r, object? result)
    {
        r.PendingResult = result;
        if (_pushed.Remove(r) || _pages.Remove(r))
        {
            _exiting.Add(r);
            RebuildHistory();
            r.StartExit();
            _body.MarkNeedsLayout();
        }
    }

    private void RemoveInstant(Route r, object? result = null)
    {
        bool removed = _pushed.Remove(r) | _pages.Remove(r) | _exiting.Remove(r);
        if (!removed) return;

        r.CompleteWith(result ?? r.PendingResult);
        r.ContentOrNull?.Detach();
        r.DisposeRoute();
        RebuildHistory();
        _body.MarkNeedsLayout();
    }

    private void OnRouteVisualUpdate() => _body.MarkNeedsLayout();

    private Route? FindPageRoute(Page p, HashSet<Route> taken)
    {
        foreach (var r in _pages)
        {
            if (!taken.Contains(r) && r.IsPageBased && r.SourcePage is { } sp &&
                PagesMatch(a: sp, b: p))
                return r;
        }

        return null;
    }

    private static bool PagesMatch(Page a, Page b)
    {
        if (a.Key is not null || b.Key is not null)
            return Equals(objA: a.Key, objB: b.Key) && a.GetType() == b.GetType();
        return ReferenceEquals(objA: a, objB: b);
    }

    private void RebuildHistory()
    {
        _history.Clear();
        _history.AddRange(_pages);
        _history.AddRange(_pushed);
        _history.AddRange(_exiting);
    }
}

/// <summary>The base route used to host a navigator's <c>Home</c> widget without a transition.</summary>
internal sealed class WidgetRoute : PageRoute<object?>
{
    private readonly Widget _widget;

    public WidgetRoute(Widget widget) => _widget = widget;

    public override float TransitionDuration => 0f;

    protected override Widget BuildContent(BuildContext context) => _widget;
}
