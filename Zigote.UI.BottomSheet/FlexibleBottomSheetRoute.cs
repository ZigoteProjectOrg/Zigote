namespace Zigote.UI.BottomSheets;

/// <summary>
///     The modal route a shown bottom sheet lives on — translucent (the page behind keeps painting),
///     with the route transition driving the card's slide up and back down.
///     <para>
///         Push it yourself when you need the sheet inside a nested navigator or with custom
///         <see cref="RouteSettings" />; <see cref="BottomSheets.ShowFlexible{T}" /> is the shorthand.
///     </para>
/// </summary>
public sealed class FlexibleBottomSheetRoute<T> : Route<T>
{
    private readonly Func<BuildContext, FlexibleBottomSheet> _build;
    private readonly float _duration;

    public FlexibleBottomSheetRoute(
        Func<BuildContext, FlexibleBottomSheet> build,
        float duration)
    {
        _build = build;
        _duration = duration;
    }

    /// <summary>The page under the sheet stays visible through the scrim.</summary>
    public override bool Opaque => false;

    public override float TransitionDuration => _duration;

    protected override Widget BuildContent(BuildContext context)
    {
        var sheet = _build(context);
        // The sheet reads this every layout, and the navigator re-lays-out the stack on every tick of
        // it — so the card slides without the sheet owning an animation of its own.
        sheet.Reveal = Transition;
        sheet.Controller.OnClose = result => Navigator?.Pop(result);
        return sheet;
    }
}