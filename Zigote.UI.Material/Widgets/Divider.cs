namespace Zigote.UI.Material;

/// <summary>
///     A hairline horizontal or vertical separator, macOS style. Defaults to a 1px line in the
///     theme's <see cref="ThemeData.Separator" /> colour; honours the <see cref="Vertical" />,
///     <see cref="Thickness" />, <see cref="Indent" /> and <see cref="EndIndent" /> properties.
/// </summary>
public sealed class Divider : ComposedWidget
{
    public Divider(float thickness = 1f, float indent = 0f)
    {
        Thickness = thickness;
        Indent = indent;
    }

    public float Thickness { get; set; } = 1f;
    public float Indent { get; set; }
    public float EndIndent { get; set; } = 0f;
    public Color? Color { get; set; }
    public bool Vertical { get; set; } = false;

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var c = Color ?? theme.Separator;
        if (Vertical)
        {
            return new Container {
                Width = Thickness,
                Margin = EdgeInsets.Only(top: Indent, bottom: EndIndent),
                Background = c,
            };
        }

        return new Container {
            Height = Thickness,
            Margin = EdgeInsets.Only(left: Indent, right: EndIndent),
            Background = c,
        };
    }
}
