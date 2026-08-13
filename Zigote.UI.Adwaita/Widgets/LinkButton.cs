// The Label string property shadows the Label widget type inside this class, so reference the
// widget type through an alias.

using LabelWidget = Zigote.UI.Widgets.Controls.Label;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwLinkButton — a flat button whose label renders in the standalone accent colour and
///     underlines on hover, like a GtkLinkButton.
/// </summary>
public sealed class AdwLinkButton : ComposedWidget
{
    private bool _enabled = true;
    private string _label;

    public AdwLinkButton(string label, Action? onPressed = null)
    {
        _label = label;
        OnPressed = onPressed;
    }

    public string Label
    {
        get => _label;
        set => this.Set(ref _label, value);
    }

    public Action? OnPressed { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        // ThemeData.PrimaryDark, not AdwPalette.Accent: the palette one is hardcoded blue, while
        // this tracks whichever of the nine GNOME system accents is selected.
        var accent = theme.PrimaryDark;

        // `link { text-decoration: underline }` is unconditional in Adwaita — the underline is
        // the affordance, and hover only brightens the hue
        // (`HSL(from $link_color h calc(s * 1.1) calc(l * 1.1))`).
        var hover = accent.Lighten(0.1f);
        var label = new LabelWidget(Label, AdwTypography.Body, accent) { MaxLines = 1 };
        var underline = new Container {
            Height = 1f,
            Background = accent,
        };

        var pressable = new Pressable {
            Enabled = Enabled,
            FocusRadius = AdwMetrics.ControlRadius,
            SemanticsLabel = Label,
            OnPressed = () => OnPressed?.Invoke(),
            Child = new ConstrainedBox(
                new Constraints(minHeight: AdwMetrics.ButtonHeight),
                new Align(
                    Alignment.Center,
                    new Padding(
                        EdgeInsets.Symmetric(Spacing.Sm),
                        // Stack sizes to the label; the positioned underline spans exactly the
                        // label's width. (A Stretch Column here inflated the label to the full
                        // row width, collapsing sibling links onto each other.)
                        new Stack {
                            Children = {
                                label,
                                new Positioned(
                                    underline,
                                    0,
                                    right: 0,
                                    bottom: 0,
                                    height: 1
                                ),
                            },
                        }
                    )
                ) {
                    WidthFactor = 1f,
                    HeightFactor = 1f,
                }
            ),
        };
        // Container.Background self-invalidates, so the apply is a bare setter.
        var fill = new FillTransition(c =>
            {
                underline.Background = c;
                label.Color = c;
                label.MarkNeedsPaint();
            }
        );
        fill.Snap(accent);
        pressable.OnStateChanged = () =>
        {
            // Only :hover brightens — `&:active { color: $link_color }` puts the pressed state
            // back on the base hue.
            fill.Target(pressable.Hovered && !pressable.Pressed ? hover : accent);
        };

        return Enabled ? pressable : new Opacity(AdwStyle.DisabledOpacity, pressable);
    }
}
