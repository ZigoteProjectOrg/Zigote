namespace Zigote.UI.Widgets;

/// <summary>
///     Inherited-widget lookup and MediaQuery data for the widget tree.
///     <para>
///         Two kinds exist. <see cref="Current" /> is the shared per-thread context the measure walk
///         drives: inherited widgets push themselves on the way down and pop on the way up, so it
///         answers for wherever the walk is right now — and for nothing once the walk is over.
///     </para>
///     <para>
///         The context a widget hands to <c>Build</c> (and to builder callbacks) is <b>anchored</b> to
///         that widget. It answers like <see cref="Current" /> during the walk, but code may also keep
///         it: called later — from a click handler, an async continuation — it walks the widget's real
///         ancestors, so <c>Navigator.Of(context).Push(…)</c> in an <c>onPressed</c> finds the
///         navigator the widget sits under, where the shared stack would long since be empty.
///     </para>
/// </summary>
public sealed class BuildContext
{
    [ThreadStatic] private static BuildContext? _instance;

    // Null on the shared per-thread context; the widget an anchored context answers for otherwise.
    private readonly Widget? _anchor;

    // Walk state, used on the shared context only — an anchored one delegates to Shared.
    private readonly Stack<InheritedWidget>? _inherited;
    private Widget? _buildOwner;
    private int _generation;
    private MediaQueryData _mediaQuery = MediaQueryData.Default;

    private BuildContext() => _inherited = new Stack<InheritedWidget>();

    private BuildContext(Widget anchor) => _anchor = anchor;

    public static BuildContext Current => _instance ??= new BuildContext();

    // The context that owns the walk state. Resolved per call rather than captured, so an anchored
    // context used on another thread reaches that thread's walk, like Current would.
    private BuildContext Shared => _anchor is null ? this : Current;

    private Stack<InheritedWidget> Stack => Shared._inherited!;

    /// <summary>
    ///     The context <paramref name="widget" /> hands to code it runs on its own behalf — see the
    ///     class remarks. One allocation per widget, made on first use and kept for its lifetime.
    /// </summary>
    internal static BuildContext For(Widget widget) =>
        widget.AnchoredContext ??= new BuildContext(widget);

    /// <summary>
    ///     Current MediaQuery data (screen size, DPI). Updated by the app each frame before
    ///     the Measure pass. Override for a subtree by inserting a <see cref="MediaQuery" /> widget.
    /// </summary>
    public MediaQueryData MediaQuery
    {
        get => Shared._mediaQuery;
        internal set => Shared._mediaQuery = value;
    }

    /// <summary>
    ///     The widget whose <c>Build</c> is currently executing, or null when no build is in flight.
    ///     Set by <see cref="ComposedWidget" /> around its Build call
    ///     so <see cref="DependOn{T}" /> can register the builder as a dependent of an inherited widget.
    /// </summary>
    internal Widget? BuildOwner
    {
        get => Shared._buildOwner;
        set => Shared._buildOwner = value;
    }

    /// <summary>
    ///     Monotonic counter bumped whenever an inherited widget's data changes (theme, media query).
    ///     Measure caches in <see cref="ComposedWidget" /> include it, so
    ///     a wrapper that does not itself depend on the theme is still forced to re-measure its subtree
    ///     when the theme flips — otherwise controls that read the theme in <c>Measure</c> (not
    ///     <c>Build</c>)
    ///     would keep stale colours. Not reset between frames.
    /// </summary>
    public int Generation => Shared._generation;

    internal void BumpGeneration() => Shared._generation++;

    internal void Reset()
    {
        Stack.Clear();
        BuildOwner = null;
    }

    // ── Inherited widget stack (managed by InheritedWidget.Measure) ───────────

    internal void Push(InheritedWidget w) => Stack.Push(w);

    internal void Pop(InheritedWidget expected)
    {
        var stack = Stack;
        if (stack.Count > 0 && stack.Peek() == expected)
            stack.Pop();
    }

    // ── Detached scopes ───────────────────────────────────────────────────────
    // The stack above only exists for the duration of the measure/layout walk: InheritedWidget
    // pushes on the way down and pops on the way up, and Reset() empties it between frames. Code
    // that measures a subtree OUTSIDE that walk (Watch, when a signal changes from a click handler)
    // therefore builds with an EMPTY stack — every ThemeProvider.Of below it silently falls back to
    // ThemeData.Dark, and because ComposedWidget clears NeedsBuild the wrong colours are then
    // retained until something else invalidates that widget. That is a black titlebar after a page
    // change, and a whole page in the wrong appearance after a settings toggle.
    // The ancestors of a mounted widget do not move between frames, so the fix is to snapshot the
    // scope during the walk and re-enter it for the detached measure. (Anchored contexts cover the
    // other half — lookups made from a kept context — by walking the parent chain instead.)

    /// <summary>
    ///     Copy the current ancestor scope into <paramref name="buffer" /> (innermost first),
    ///     growing it if needed, and return how many entries were written.
    /// </summary>
    internal int CaptureScope(ref InheritedWidget[] buffer)
    {
        var stack = Stack;
        if (buffer.Length < stack.Count)
            buffer = new InheritedWidget[Math.Max(val1: 8, val2: stack.Count)];

        int n = 0;
        // Stack<T> enumerates top-down and its enumerator is a struct, so this allocates nothing.
        foreach (var w in stack) buffer[n++] = w;
        return n;
    }

    /// <summary>
    ///     Re-enter a scope captured by <see cref="CaptureScope" />. Pair with
    ///     <see cref="ExitScope" />.
    /// </summary>
    internal void EnterScope(InheritedWidget[] buffer, int count)
    {
        var stack = Stack;
        for (int i = count - 1; i >= 0; i--) stack.Push(buffer[i]);
    }

    /// <inheritdoc cref="EnterScope" />
    internal void ExitScope(int count)
    {
        var stack = Stack;
        for (int i = 0; i < count && stack.Count > 0; i++) stack.Pop();
    }

    // ── Lookup API ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Find the nearest ancestor <see cref="InheritedWidget" /> of type
    ///     <typeparamref name="T" />, or null if none is in scope.
    /// </summary>
    public T? FindAncestor<T>() where T : InheritedWidget
    {
        // An anchored context answers from its widget's real ancestors first. That is correct at
        // any time — mid-walk, or long after it in a click handler, when the stack is empty. The
        // stack remains the fallback for what the parent chain cannot show: a widget measured
        // before it is attached (tests, off-tree measurement), or overlay content measured under a
        // provider the App pushes without parenting it.
        if (_anchor is not null)
        {
            for (var w = _anchor.Parent; w is not null; w = w.Parent)
            {
                if (w is T t)
                    return t;
            }
        }

        foreach (var w in Stack)
        {
            if (w is T t)
                return t;
        }

        return null;
    }

    /// <summary>
    ///     Find the nearest ancestor <see cref="InheritedWidget" /> of type
    ///     <typeparamref name="T" /> without registering a dependency.
    /// </summary>
    public T? Read<T>() where T : InheritedWidget => FindAncestor<T>();

    /// <summary>
    ///     Find the nearest ancestor <see cref="InheritedWidget" /> of type <typeparamref name="T" />
    ///     and register the widget currently building (<see cref="BuildOwner" />) as a dependent, so it
    ///     is rebuilt when that inherited widget's data changes. Outside a Build pass (e.g. a control
    ///     reading the theme during Measure, or a click handler) this behaves like
    ///     <see cref="FindAncestor{T}" />.
    /// </summary>
    public T? DependOn<T>() where T : InheritedWidget
    {
        var w = FindAncestor<T>();
        if (w is not null && BuildOwner is { } owner) w.AddDependent(owner);
        return w;
    }

    /// <summary>
    ///     Find the nearest ancestor <see cref="InheritedWidget" /> of type
    ///     <typeparamref name="T" />. Throws if not found.
    /// </summary>
    public T Require<T>() where T : InheritedWidget
    {
        return FindAncestor<T>()
               ?? throw new InvalidOperationException(
                   $"{typeof(T).Name} not found in the widget tree. "
                   + "Ensure it is an ancestor of the widget that calls this."
               );
    }

    /// <summary>
    ///     Static accessor mirroring <c>T.of(context)</c>.
    ///     Example: <c>BuildContext.Of&lt;MyTheme&gt;(ctx)</c>
    /// </summary>
    public static T Of<T>(BuildContext ctx) where T : InheritedWidget => ctx.Require<T>();
}
