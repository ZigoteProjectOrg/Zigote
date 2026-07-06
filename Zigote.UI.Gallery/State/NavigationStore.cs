using Zigote.Core.State;

namespace Gallery;

/// <summary>
///     The whole navigation state of the gallery: home, or home with one demo pushed on top.
///     Immutable — every transition is a new value written to the <see cref="NavigationStore" />'s signal.
/// </summary>
internal sealed record GalleryRoute(string? DemoId)
{
    public static readonly GalleryRoute Home = new((string?)null);
}

/// <summary>
///     Single source of truth for routing (Navigator 2.0), held in a <see cref="Signal{T}" />. The UI
///     never pushes routes itself — it writes intents here, and <see cref="GalleryApp" /> projects the
///     value into a declarative page stack via <c>NavigatorState.SetPages</c>.
/// </summary>
internal sealed class NavigationStore
{
    public Signal<GalleryRoute> Route { get; } = new(GalleryRoute.Home);

    public void OpenDemo(string demoId)
    {
        Route.Value = new GalleryRoute(demoId);
    }

    public void GoHome()
    {
        Route.Value = GalleryRoute.Home;
    }
}
