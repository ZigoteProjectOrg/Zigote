namespace Zigote.UI.Widgets.Navigation;

/// <summary>
///     Navigation shortcuts on <see cref="BuildContext" />. Each delegates to the nearest
///     enclosing <see cref="Navigator" />:
///     <code>
///   // inside a Build / event handler
///   var result = await context.Push(ctx =&gt; new DetailPage(item));
///   context.Pop("saved");
/// </code>
/// </summary>
public static class NavigationExtensions
{
    /// <summary>Push a route; the task completes with the pop result.</summary>
    public static Task<T?> Push<T>(this BuildContext context, Route<T> route)
    {
        return Navigator.Of(context).Push(route);
    }

    /// <summary>Push a page built from <paramref name="builder" />.</summary>
    public static Task<object?> Push(this BuildContext context, WidgetBuilder builder)
    {
        return Navigator.Of(context).Push(builder);
    }

    /// <summary>Push a page showing the given widget.</summary>
    public static Task<object?> Push(this BuildContext context, Widget page)
    {
        return Navigator.Of(context).Push(page);
    }

    /// <summary>Push a named route.</summary>
    public static Task<object?> PushNamed(this BuildContext context, string name,
        object? arguments = null)
    {
        return Navigator.Of(context).PushNamed(name, arguments);
    }

    /// <summary>Replace the current route with <paramref name="route" />.</summary>
    public static Task<T?> PushReplacement<T>(this BuildContext context, Route<T> route,
        object? result = null)
    {
        return Navigator.Of(context).PushReplacement(route, result);
    }

    /// <summary>Pop the current route, optionally returning <paramref name="result" />.</summary>
    public static void Pop(this BuildContext context, object? result = null)
    {
        Navigator.Of(context).Pop(result);
    }

    /// <summary>Pop only if possible; returns whether a pop occurred.</summary>
    public static bool MaybePop(this BuildContext context, object? result = null)
    {
        return Navigator.MaybeOf(context)?.MaybePop(result) ?? false;
    }

    /// <summary>Whether the nearest navigator has a route it can pop.</summary>
    public static bool CanPop(this BuildContext context)
    {
        return Navigator.MaybeOf(context)?.CanPop ?? false;
    }
}
