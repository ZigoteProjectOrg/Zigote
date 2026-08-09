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
        set => this.Set(ref _child, value);
    }

    public float MaximumSize
    {
        get => _maximumSize;
        set => this.Set(ref _maximumSize, value);
    }

    protected override Widget Build(BuildContext context)
    {
        return new Align {
            Alignment = Alignment.TopCenter,
            Child = new ConstrainedBox(
                new Constraints(
                    0f,
                    MaximumSize,
                    0f,
                    float.PositiveInfinity
                ),
                Child
            ),
        };
    }
}