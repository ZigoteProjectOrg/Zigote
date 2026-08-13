using Zigote.Core.State;
using Zigote.UI.Theme;

namespace Gallery;

internal enum ThemeMode
{
    Light,
    Dark,
}

/// <summary>
///     App-wide appearance state as a <see cref="Signal{T}" />. <see cref="GalleryApp" /> watches
///     <see cref="Mode" /> and pushes the resolved <see cref="ThemeData" /> into the framework;
///     widgets
///     only ever talk to the store.
/// </summary>
internal sealed class ThemeStore
{
    public Signal<ThemeMode> Mode { get; } = new(ThemeMode.Dark);

    public ThemeData Data => Mode.Value == ThemeMode.Dark ? ThemeData.Dark : ThemeData.Light;

    public void Set(ThemeMode mode) => Mode.Value = mode;
}
