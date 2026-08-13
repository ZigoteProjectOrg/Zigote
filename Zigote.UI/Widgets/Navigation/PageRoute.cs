using Zigote.Core;

namespace Zigote.UI.Widgets.Navigation;

/// <summary>
///     A full-screen, opaque modal route with a horizontal slide-and-fade transition — the base for
///     ordinary page navigation. The incoming page slides in from the right and fades up; on pop it
///     reverses, revealing the page beneath.
/// </summary>
public abstract class PageRoute<T> : Route<T>
{
    /// <summary>Fraction of the page width the entering page is offset by at the start of the slide.</summary>
    public float SlideFraction { get; init; } = 0.25f;

    public override Offset TransitionOffset(Size size, float t) => new(
        x: (1f - t) * size.Width * SlideFraction,
        y: 0f
    );

    public override float TransitionOpacity(float t) => t;
}

/// <summary>
///     The standard page route: builds its content from a <see cref="WidgetBuilder" />.
///     <code>
///   context.Push(new MaterialPageRoute&lt;string&gt;(ctx =&gt; new DetailPage()));
/// </code>
/// </summary>
public class MaterialPageRoute<T> : PageRoute<T>
{
    private readonly WidgetBuilder _builder;

    public MaterialPageRoute(WidgetBuilder builder, RouteSettings? settings = null)
    {
        _builder = builder;
        Settings = settings ?? RouteSettings.Empty;
    }

    protected override Widget BuildContent(BuildContext context) => _builder(context);
}

/// <summary>Non-generic <see cref="MaterialPageRoute{T}" /> (result type <c>object?</c>).</summary>
public sealed class MaterialPageRoute : MaterialPageRoute<object?>
{
    public MaterialPageRoute(WidgetBuilder builder, RouteSettings? settings = null)
        : base(builder: builder, settings: settings) { }
}
