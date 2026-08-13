namespace Zigote.UI.Material;

/// <summary>
///     Phone sizing for the Material controls. <see cref="ControlMetrics" /> encodes a macOS
///     pointer rhythm (22/28/36 pt rows, 16 pt checkboxes) that no finger can hit reliably, so on
///     phone-width windows the controls swap those for 44 pt targets — and the few widgets built
///     around a desktop metaphor (floating panels, master/detail, multi-column tables) use the same
///     flag to pick their phone arm. Everything Medium and wider keeps the desktop layout untouched.
///     <para>
///         Read from <c>Measure</c> or <c>Build</c> the same way widgets already read
///         <see cref="ThemeProvider" /> there — the size class comes from the whole window, not the
///         pane, because a narrow pane on a desktop is still driven by a mouse.
///     </para>
/// </summary>
internal static class TouchMetrics
{
    /// <summary>Smallest reliable finger target, in logical px.</summary>
    public const float MinTarget = ControlMetrics.MinTouchTarget;

    /// <summary>True when the window is phone-width and the controls should be finger-sized.</summary>
    public static bool IsCompact =>
        MediaQuery.Of(BuildContext.Current).SizeClass == WindowSizeClass.Compact;

    /// <summary>The desktop value, or a finger-sized one on a phone.</summary>
    public static float Pick(float desktop, float touch = MinTarget) => IsCompact ? touch : desktop;

    /// <summary>The desktop value, raised to at least <paramref name="touch" /> on a phone.</summary>
    public static float AtLeast(float desktop, float touch = MinTarget) =>
        IsCompact ? MathF.Max(x: desktop, y: touch) : desktop;
}
