using Zigote.Core;
using Zigote.UI.Material;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     Editor widgets (color, gradient, curve, code). Built once per visit — the route caches the
///     page content, so in-editor edits survive while the page is on the navigation stack.
/// </summary>
internal sealed class EditorsPage : StatelessWidget
{
    protected override Widget Build(BuildContext context)
    {
        // Built here, not inside the AdaptiveBuilder below: that builder re-runs on every
        // constraints change, and a fresh editor there would drop the typed text on a resize.
        var code = new CodeEditor("void Main()\n{\n    Console.WriteLine(\"Hello, Zigote!\");\n}");

        return Sections(
            Section(
                "Color picker",
                new SizedBox(height: 240, child: new ColorPicker(Colors.Blue, _ => { }))
            ),
            Section("Color swatch field", new ColorSwatchField(Colors.Orange)),
            Section(
                "Gradient editor",
                new SizedBox(
                    height: 72,
                    child: new GradientEditor(
                        new ColorGradient(
                            [
                                new GradientStop(0f, Colors.Blue),
                                new GradientStop(0.5f, Colors.Purple),
                                new GradientStop(1f, Colors.Red),
                            ]
                        ),
                        _ => { }
                    )
                )
            ),
            Section(
                "Curve editor",
                new SizedBox(
                    height: 180,
                    child: new CurveEditor(
                        new EditableCurve(
                            [
                                new CurveKey(0f, 0f), new CurveKey(0.5f, 0.85f),
                                new CurveKey(1f, 1f),
                            ]
                        ),
                        _ => { }
                    )
                )
            ),
            Section(
                "Code editor",
                // The editor scrolls its own content with the wheel only, so on a phone whatever
                // the box shows is all that is reachable — give the sample room to fit uncut.
                new AdaptiveBuilder(
                    (_, size) => new SizedBox(
                        height: size == WindowSizeClass.Compact ? 220 : 170,
                        child: code
                    )
                )
            )
        );
    }
}