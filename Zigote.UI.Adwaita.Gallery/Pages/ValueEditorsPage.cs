namespace AdwaitaGallery.Pages;

/// <summary>
///     The authoring controls a tool needs past a slider: a number you can drag, a full colour
///     picker, a gradient with movable stops and a curve with tangents. All four are live.
/// </summary>
public sealed class ValueEditorsPage : ComposedWidget
{
    private readonly Signal<Color> _color = new(AdwAccentColors.Bg(AdwAccent.Teal));
    private readonly Signal<float> _number = new(1.25f);
    private readonly Signal<int> _stops = new(2);
    private readonly Signal<int> _keys = new(3);

    protected override Widget Build(BuildContext context)
    {
        var curve = new AdwEditableCurve(
            [
                new AdwCurveKey(time: 0f, value: 0f),
                new AdwCurveKey(time: 0.35f, value: 0.9f),
                new AdwCurveKey(time: 1f, value: 0.4f),
            ]
        );

        return new GalleryPage(
            title: "Value Editors",
            description: "Numbers you drag, colours you pick, gradients and curves you shape.",
            iconName: MaterialIcons.Timeline
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Group(
                    title: "Number Input",
                    description:
                    "Type a value, step it with the buttons, or drag the grip sideways to scrub " +
                    "it. Hold Shift while dragging for a tenth of the speed.",
                    new AdwActionRow(title: "Exposure", subtitle: "Unbounded, two decimals") {
                        Suffixes = {
                            new SizedBox(
                                width: 160f,
                                child: new AdwNumberInput(value: 1.25f, step: 0.05f) {
                                    OnChanged = v => _number.Value = v,
                                }
                            ),
                        },
                    },
                    new AdwActionRow(title: "Samples", subtitle: "Whole numbers from 1 to 64") {
                        Suffixes = {
                            new SizedBox(
                                width: 160f,
                                child: new AdwNumberInput(
                                    value: 8f,
                                    step: 1f,
                                    min: 1f,
                                    max: 64f
                                ) {
                                    Decimals = 0,
                                }
                            ),
                        },
                    },
                    new Watch(() => new AdwActionRow("Value") {
                            Suffixes = {
                                Demo.Value(
                                    _number.Value.ToString(
                                        format: "0.00",
                                        provider: System.Globalization.CultureInfo.InvariantCulture
                                    )
                                ),
                            },
                        }
                    )
                ),
                Demo.Titled(
                    title: "Colour Picker",
                    description:
                    "The full picker the colour button opens: a saturation and value square, a " +
                    "hue strip, alpha, and the hex field.",
                    child: Demo.Specimen(
                        new AdwColorPicker(
                            initial: _color.Peek(),
                            onChanged: c => _color.Value = c
                        ),
                        new Watch(() => Demo.Value(
                                $"#{(int)(_color.Value.R * 255):X2}" +
                                $"{(int)(_color.Value.G * 255):X2}" +
                                $"{(int)(_color.Value.B * 255):X2}"
                            )
                        )
                    )
                ),
                Demo.Titled(
                    title: "Gradient Editor",
                    description:
                    "Click the strip to add a stop, drag a stop to move it, double-click one to " +
                    "recolour it and drag it off the strip to remove it.",
                    child: Demo.Specimen(
                        new SizedBox(
                            height: 56f,
                            child: new AdwGradientEditor(
                                gradient: new AdwColorGradient(),
                                onChanged: g => _stops.Value = g.Stops.Count
                            )
                        ),
                        new Watch(() => Demo.Caption($"{_stops.Value} stops"))
                    )
                ),
                Demo.Titled(
                    title: "Curve Editor",
                    description:
                    "Double-click to add a key, drag keys and their tangent handles, and press " +
                    "Delete to remove the selected one.",
                    child: Demo.Specimen(
                        new SizedBox(
                            height: 240f,
                            child: new AdwCurveEditor(
                                curve: curve,
                                onChanged: c => _keys.Value = c.Keys.Count
                            )
                        ),
                        new Watch(() => Demo.Caption($"{_keys.Value} keys"))
                    )
                ),
            },
        };
    }
}
