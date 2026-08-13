namespace Zigote.UI.Widgets;

/// <summary>
///     Ambient context available during the Measure pass via <see cref="Current" />.
///     Provides inherited widget lookup and MediaQuery data to every widget in the tree.
///     Thread-static so it's always correct on the single UI thread without explicit passing.
/// </summary>
public sealed class BuildContext
{
    [ThreadStatic] private static BuildContext? _instance;

    private readonly Stack<InheritedWidget> _inherited = new();

    public static BuildContext Current => _instance ??= new BuildContext();

    /// <summary>
    ///     Current MediaQuery data (screen size, DPI). Updated by UiApp each frame before
    ///     the Measure pass. Override for a subtree by inserting a <see cref="MediaQuery" /> widget.
    /// </summary>
    public MediaQueryData MediaQuery { get; internal set; } = MediaQueryData.Default;

    /// <summary>
    ///     The widget whose <c>Build</c> is currently executing, or null when no build is in flight.
    ///     Set by <see cref="ComposedWidget" /> around its Build call
    ///     so <see cref="DependOn{T}" /> can register the builder as a dependent of an inherited widget.
    /// </summary>
    internal Widget? BuildOwner { get; set; }

    /// <summary>
    ///     Monotonic counter bumped whenever an inherited widget's data changes (theme, media query).
    ///     Measure caches in <see cref="ComposedWidget" /> include it, so
    ///     a wrapper that does not itself depend on the theme is still forced to re-measure its subtree
    ///     when the theme flips — otherwise controls that read the theme in <c>Measure</c> (not
    ///     <c>Build</c>)
    ///     would keep stale colours. Not reset between frames.
    /// </summary>
    public int Generation { get; private set; }

    internal void BumpGeneration() => Generation++;

    internal void Reset()
    {
        _inherited.Clear();
        BuildOwner = null;
    }

    // ── Inherited widget stack (managed by InheritedWidget.Measure) ───────────

    internal void Push(InheritedWidget w) => _inherited.Push(w);

    internal void Pop(InheritedWidget expected)
    {
        if (_inherited.Count > 0 && _inherited.Peek() == expected)
            _inherited.Pop();
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
    // scope during the walk and re-enter it for the detached measure.

    /// <summary>
    ///     Copy the current ancestor scope into <paramref name="buffer" /> (innermost first),
    ///     growing it if needed, and return how many entries were written.
    /// </summary>
    internal int CaptureScope(ref InheritedWidget[] buffer)
    {
        if (buffer.Length < _inherited.Count)
            buffer = new InheritedWidget[Math.Max(val1: 8, val2: _inherited.Count)];

        int n = 0;
        // Stack<T> enumerates top-down and its enumerator is a struct, so this allocates nothing.
        foreach (var w in _inherited) buffer[n++] = w;
        return n;
    }

    /// <summary>
    ///     Re-enter a scope captured by <see cref="CaptureScope" />. Pair with
    ///     <see cref="ExitScope" />.
    /// </summary>
    internal void EnterScope(InheritedWidget[] buffer, int count)
    {
        for (int i = count - 1; i >= 0; i--) _inherited.Push(buffer[i]);
    }

    /// <inheritdoc cref="EnterScope" />
    internal void ExitScope(int count)
    {
        for (int i = 0; i < count && _inherited.Count > 0; i++) _inherited.Pop();
    }

    // ── Lookup API ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Find the nearest ancestor <see cref="InheritedWidget" /> of type
    ///     <typeparamref name="T" />, or null if none is in scope.
    /// </summary>
    public T? FindAncestor<T>() where T : InheritedWidget
    {
        foreach (var w in _inherited)
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
    ///     reading the theme during Measure) this behaves like <see cref="FindAncestor{T}" />.
    /// </summary>
    public T? DependOn<T>() where T : InheritedWidget
    {
        var w = FindAncestor<T>();
        if (w is not null && BuildOwner is not null) w.AddDependent(BuildOwner);
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
