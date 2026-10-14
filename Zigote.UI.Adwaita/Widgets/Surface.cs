namespace Zigote.UI.Adwaita;

/// <summary>
///     Marks a subtree as sitting on a dialog sheet rather than the window, so a widget that has
///     to paint over its own surface (a page sliding over another) can match it. Installed by
///     <see cref="AdwDialog" />.
/// </summary>
public sealed class AdwDialogSurface : InheritedWidget
{
    public AdwDialogSurface(Widget? child = null) => Child = child;

    /// <summary>
    ///     The opaque colour the subtree sits on in <paramref name="theme" />: the dialog sheet under
    ///     an <see cref="AdwDialogSurface" />, the window otherwise.
    /// </summary>
    public static Color Of(BuildContext ctx, ThemeData theme) =>
        ctx.DependOn<AdwDialogSurface>() is null
            ? AdwPalette.For(theme).WindowBg
            : AdwPalette.For(theme).DialogBg;

    public override bool UpdateShouldNotify(InheritedWidget oldWidget) => false;
}
