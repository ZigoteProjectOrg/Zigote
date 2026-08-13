namespace Zigote.UI.Widgets.Navigation;

/// <summary>
///     Identity for a route: an optional <see cref="Name" /> (for named navigation and
///     <c>PopUntil</c>) and free-form <see cref="Arguments" /> passed to the route builder.
/// </summary>
public sealed record RouteSettings(string? Name = null, object? Arguments = null)
{
    /// <summary>An anonymous, argument-less settings instance.</summary>
    public static readonly RouteSettings Empty = new();
}
