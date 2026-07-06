namespace Zigote.UI.Widgets;

public abstract record Key;

public sealed record ValueKey<T>(T Value) : Key;