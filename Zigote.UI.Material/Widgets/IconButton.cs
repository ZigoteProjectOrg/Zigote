namespace Zigote.UI.Material;

/// <summary>
///     A tappable icon.
///     <c>
///         new IconButton(icon: new Icon("settings"),
///         onPressed: () => …)
///     </c>
///     . Composes a <see cref="Pressable" /> over the icon; an optional
///     <see cref="Tooltip" /> wraps it.
/// </summary>
public sealed class IconButton : ComposedWidget
{
    private readonly Widget _icon;

    public IconButton(
        Widget icon,
        Action? onPressed = null,
        double? iconSize = null,
        Color? color = null,
        string? tooltip = null)
    {
        _icon = icon;
        OnPressed = onPressed;
        IconSize = (float?)iconSize;
        Color = color;
        Tooltip = tooltip;
    }

    public Action? OnPressed { get; set; }
    public float? IconSize { get; set; }
    public Color? Color { get; set; }
    public string? Tooltip { get; set; }

    protected override Widget Build(BuildContext context)
    {
        if (_icon is Icon ic)
        {
            if (Color is { } c) ic.Color = c;
            if (IconSize is { } s) ic.Size = s;
        }

        // 24pt icon + 16 padding is 40 — under the 44pt finger target, and the small icon sizes
        // toolbars use land near 34. Floor the whole box on a phone; the icon itself is unchanged.
        var box = TouchMetrics.AtLeast((IconSize ?? 24f) + 16f);
        Widget result = new Pressable {
            Child = new SizedBox(box, box, new Center(_icon)),
            OnPressed = () => OnPressed?.Invoke(),
            Enabled = OnPressed is not null,
            FocusRadius = box / 2f,
            SemanticsLabel = Tooltip,
        };

        if (Tooltip is { } t) result = new Tooltip(t, result);
        return result;
    }
}
