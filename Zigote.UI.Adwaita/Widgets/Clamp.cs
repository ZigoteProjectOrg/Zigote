namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwClamp — constrains its child to a maximum width and centers it, letting it fill
///     narrower spaces. The standard wrapper for preferences pages and reading content.
/// </summary>
public sealed class AdwClamp : ComposedWidget
{
    private Widget _child;
    private float _maximumSize;

    public AdwClamp(Widget child, float maximumSize = AdwMetrics.ClampWidth)
    {
        _child = child;
        _maximumSize = maximumSize;
    }

    public Widget Child
    {
        get => _child;
        set => this.Set(field: ref _child, value: value);
    }

    public float MaximumSize
    {
        get => _maximumSize;
        set => this.Set(field: ref _maximumSize, value: value);
    }

    protected override Widget Build(BuildContext context)
    {
        return new Align {
            Alignment = Alignment.TopCenter,
            Child = new ConstrainedBox(
                constraints: new Constraints(
                    minWidth: 0f,
                    maxWidth: MaximumSize,
                    minHeight: 0f,
                    maxHeight: float.PositiveInfinity
                ),
                child: Child
            ),
        };
    }
}
