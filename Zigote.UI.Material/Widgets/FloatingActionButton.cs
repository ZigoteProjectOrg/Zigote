namespace Zigote.UI.Material;

/// <summary>
///     A circular, accent-filled action button, usually placed
///     in <see cref="Scaffold.FloatingActionButton" />.
///     <c>
///         new FloatingActionButton(onPressed: () => …,
///         child: new Icon("add"))
///     </c>
///     .
/// </summary>
public sealed class FloatingActionButton : ComposedWidget
{
    private readonly Widget? _child;

    public FloatingActionButton(
        Action? onPressed = null,
        Widget? child = null,
        Color? backgroundColor = null,
        Color? foregroundColor = null,
        string? tooltip = null,
        bool mini = false)
    {
        _child = child;
        OnPressed = onPressed;
        BackgroundColor = backgroundColor;
        ForegroundColor = foregroundColor;
        Tooltip = tooltip;
        Mini = mini;
    }

    public Action? OnPressed { get; set; }
    public Color? BackgroundColor { get; set; }
    public Color? ForegroundColor { get; set; }
    public string? Tooltip { get; set; }
    public bool Mini { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var d = Mini ? 40f : 56f;

        if (_child is Icon ic && ic.Color is null)
            ic.Color = ForegroundColor ?? theme.OnPrimary;

        var box = new DecoratedBox {
            Fill = BackgroundColor ?? theme.Primary,
            Radius = d / 2f,
            Elevation = Elevation.Z2,
            Child = new SizedBox(d, d, new Center(_child)),
        };

        Widget result = new Pressable {
            Child = box,
            OnPressed = () => OnPressed?.Invoke(),
            Enabled = OnPressed is not null,
            FocusRadius = d / 2f,
            SemanticsLabel = Tooltip,
        };

        if (Tooltip is { } t) result = new Tooltip(t, result);
        return result;
    }
}
