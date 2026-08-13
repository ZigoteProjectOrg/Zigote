using Zigote.UI.Theme;

namespace Zigote.UI.Widgets;

/// <summary>
///     <see cref="InheritedWidget" /> that propagates <see cref="ThemeData" /> down the tree.
///     Injected automatically by <see cref="Zigote.UI.Host.ZigoteApp" />; any descendant can read it
///     via
///     <c>ThemeProvider.Of(BuildContext.Current)</c> or <c>Theme.Of(BuildContext.Current)</c>.
/// </summary>
public sealed class ThemeProvider : InheritedWidget
{
    private ThemeData _data;

    public ThemeProvider(ThemeData data, Widget? child = null)
    {
        _data = data;
        Child = child;
    }

    /// <summary>The current theme. Reassigning to a different instance rebuilds dependents.</summary>
    public ThemeData Data
    {
        get => _data;
        set
        {
            if (ReferenceEquals(objA: _data, objB: value)) return;
            _data = value;
            // Invalidate measure caches (controls read the theme in Measure, not Build) and force a
            // relayout, then rebuild any explicit dependents.
            BuildContext.Current.BumpGeneration();
            MarkNeedsLayout();
            NotifyDependents();
        }
    }

    /// <summary>
    ///     Set the theme without firing the generation bump / relayout / dependent rebuilds. For an
    ///     app-owned scope that is re-pushed and re-read on every layout pass (the overlay theme scope),
    ///     where those side effects would be redundant work every frame.
    /// </summary>
    internal void SetDataSilently(ThemeData data) => _data = data;

    /// <summary>
    ///     Returns the nearest <see cref="ThemeData" /> in the tree, registering the building widget as
    ///     a dependent so it rebuilds when the theme changes. Falls back to <see cref="ThemeData.Dark" />
    ///     if no ThemeProvider ancestor exists.
    /// </summary>
    public static ThemeData Of(BuildContext ctx) =>
        ctx.DependOn<ThemeProvider>()?.Data ?? ThemeData.Dark;

    public override bool UpdateShouldNotify(InheritedWidget oldWidget) =>
        oldWidget is not ThemeProvider old || !ReferenceEquals(objA: old.Data, objB: Data);
}

/// <summary>
///     Static helper for accessing the ambient theme.
/// </summary>
public static class Theme
{
    public static ThemeData Of(BuildContext ctx) => ThemeProvider.Of(ctx);
}
