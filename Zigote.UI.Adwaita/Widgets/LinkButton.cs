// The Label string property shadows the Label widget type inside this class, so reference the
// widget type through an alias.

using LabelWidget = Zigote.UI.Widgets.Controls.Label;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwLinkButton — a flat button whose label renders in the standalone accent colour and
///     underlines on hover, like a GtkLinkButton.
/// </summary>
public sealed class AdwLinkButton : StatelessWidget
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

        var label = new LabelWidget(Label, 14f) {
            MaxLines = 1,
            Color = accent,
        };
        // Cheap hover underline: a 1px bar under the label toggled between accent and transparent.
        var underline = new Container {
            Height = 1f,
            Background = Color.Transparent,
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
        var fill = new FillTransition(c => underline.Background = c);
        fill.Snap(Color.Transparent); // seed so the first hover fades instead of snapping
        pressable.OnStateChanged = () =>
        {
            fill.Target(
                pressable.Hovered || pressable.Pressed
                    ? accent
                    : Color.Transparent
            );
        };

        return Enabled ? pressable : new Opacity(AdwStyle.DisabledOpacity, pressable);
    }
}