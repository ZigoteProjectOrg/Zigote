namespace Zigote.UI.Material;

/// <summary>
///     A rectangular tappable region. This engine has no ink ripple, so it
///     behaves like a <see cref="GestureDetector" /> (tap / double-tap).
///     <c>
///         new InkWell(onTap: () => …,
///         child: …)
///     </c>
///     . <c>onLongPress</c> is accepted but not detected yet.
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
        OnLongPress = onLongPress;
    }

    public Action? OnTap { get; set; }
    public Action? OnDoubleTap { get; set; }
    public Action? OnLongPress { get; set; }

    protected override Widget Build(BuildContext context)
    {
        return new GestureDetector(
            _child,
            OnTap,
            OnDoubleTap,
            OnLongPress
        );
    }
}