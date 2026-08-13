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
internal sealed class EditorsPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        // Built here, not inside the AdaptiveBuilder below: that builder re-runs on every
        // constraints change, and a fresh editor there would drop the typed text on a resize.
        var code = new CodeEditor("void Main()\n{\n    Console.WriteLine(\"Hello, Zigote!\");\n}");

        return Sections(
            Section(
                title: "Color picker",
                child: new SizedBox(
                    height: 240,
                    child: new ColorPicker(initial: Colors.Blue, onChanged: _ => { })
                )
            ),
            Section(title: "Color swatch field", child: new ColorSwatchField(Colors.Orange)),
            Section(
                title: "Gradient editor",
                child: new SizedBox(
                    height: 72,
                    child: new GradientEditor(
                        gradient: new ColorGradient(
                            [
                                new GradientStop(position: 0f, color: Colors.Blue),
                                new GradientStop(position: 0.5f, color: Colors.Purple),
                                new GradientStop(position: 1f, color: Colors.Red),
                            ]
                        ),
                        onChanged: _ => { }
                    )
                )
            ),
            Section(
                title: "Curve editor",
                child: new SizedBox(
                    height: 180,
                    child: new CurveEditor(
                        curve: new EditableCurve(
                            [
                                new CurveKey(time: 0f, value: 0f),
                                new CurveKey(time: 0.5f, value: 0.85f),
                                new CurveKey(time: 1f, value: 1f),
                            ]
                        ),
                        onChanged: _ => { }
                    )
                )
            ),
            Section(
                title: "Code editor",
                // The editor scrolls its own content with the wheel only, so on a phone whatever
                // the box shows is all that is reachable — give the sample room to fit uncut.
                child: new AdaptiveBuilder((_, size) => new SizedBox(
                        height: size == WindowSizeClass.Compact ? 220 : 170,
                        child: code
                    )
                )
            )
        );
    }
}
