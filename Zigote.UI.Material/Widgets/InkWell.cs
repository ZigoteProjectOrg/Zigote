namespace Zigote.UI.Material;

/// <summary>
///     A rectangular tappable region. This engine has no ink ripple, so it
///     behaves like a <see cref="GestureDetector" /> (tap / double-tap / touch long-press).
///     <c>
///         new InkWell(onTap: () => …,
///         child: …)
///     </c>
/// </summary>
public sealed class InkWell : StatelessWidget
{
    private readonly Widget? _child;

    public InkWell(
        Widget? child = null,
        Action? onTap = null,
        Action? onDoubleTap = null,
        Action? onLongPress = null)
    {
        _child = child;
        OnTap = onTap;
        OnDoubleTap = onDoubleTap;
        OnLongPressed = onLongPress;
    }

    public Action? OnTap { get; set; }
    public Action? OnDoubleTap { get; set; }

    /// <inheritdoc cref="GestureDetector.OnLongPressed" />
    public Action? OnLongPressed { get; set; }

    protected override Widget Build(BuildContext context)
    {
        return new GestureDetector(
            _child,
            OnTap,
            OnDoubleTap,
            OnLongPressed
        );
    }
}