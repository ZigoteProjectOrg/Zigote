using Zigote.Core;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets;

/// <summary>
///     Information about the logical screen this subtree is rendering into.
///     Injected at the root by <see cref="App" /> every frame.
/// </summary>
public readonly struct MediaQueryData(
    float width,
    float height,
    float devicePixelRatio = 1f,
    EdgeInsets? padding = null,
    EdgeInsets? viewInsets = null)
{
    public static readonly MediaQueryData Default = new(960f, 640f);

    /// <summary>Logical width of the window/screen.</summary>
    public float Width { get; } = width;

    /// <summary>Logical height of the window/screen.</summary>
    public float Height { get; } = height;

    /// <summary>Physical pixels per logical pixel (e.g. 2.0 on Retina).</summary>
    public float DevicePixelRatio { get; } = devicePixelRatio;

    /// <summary>Safe-area insets (notch, home indicator). Zero on desktop.</summary>
    public EdgeInsets Padding { get; } = padding ?? EdgeInsets.Zero;

    /// <summary>Screen area obscured by system UI (keyboard/IME). Zero on desktop.</summary>
    public EdgeInsets ViewInsets { get; } = viewInsets ?? EdgeInsets.Zero;

    /// <summary>Logical size as a <see cref="Zigote.Core.Size" />.</summary>
    public Size Size => new(Width, Height);
}

/// <summary>
///     <see cref="InheritedWidget" /> that provides <see cref="MediaQueryData" /> to descendants.
///     <para>
///         UiApp injects one at the root automatically. Add your own in the tree to override
///         dimensions for a sub-panel (e.g. a sidebar with its own layout constraints).
///     </para>
///     <para>
///         Usage: <c>var mq = MediaQuery.Of(BuildContext.Current);</c>
///     </para>
/// </summary>
public sealed class MediaQuery : InheritedWidget
{
    private MediaQueryData _data;

    public MediaQuery(MediaQueryData data, Widget? child = null)
    {
        _data = data;
        Child = child;
    }

    /// <summary>The current media data. Reassigning to changed dimensions rebuilds dependents.</summary>
    public MediaQueryData Data
    {
        get => _data;
        set
        {
            if (_data.Width == value.Width && _data.Height == value.Height &&
                _data.DevicePixelRatio == value.DevicePixelRatio)
            {
                _data = value;
                return;
            }

            _data = value;
            BuildContext.Current.BumpGeneration();
            MarkNeedsLayout();
            NotifyDependents();
        }
    }

    /// <summary>
    ///     Returns the nearest <see cref="MediaQueryData" /> in scope, registering the building widget
    ///     as a dependent. Falls back to <see cref="BuildContext.MediaQuery" /> (set by the app) if no
    ///     <see cref="MediaQuery" /> widget ancestor exists.
    /// </summary>
    public static MediaQueryData Of(BuildContext ctx)
    {
        return ctx.DependOn<MediaQuery>()?.Data ?? ctx.MediaQuery;
    }

    public override bool UpdateShouldNotify(InheritedWidget oldWidget)
    {
        return oldWidget is not MediaQuery old
               || old.Data.Width != Data.Width
               || old.Data.Height != Data.Height
               || old.Data.DevicePixelRatio != Data.DevicePixelRatio;
    }
}