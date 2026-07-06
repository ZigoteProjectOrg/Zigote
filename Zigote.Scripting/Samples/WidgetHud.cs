// Sample script — Samples/Scripting/WidgetHud.cs
// Copy to your project and reference Zigote.Scripting.dll to get started.

using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Scripting;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Samples.Scripting;

/// <summary>
///     Demonstrates the widget game HUD: this builds a real <c>Zigote.UI</c> widget tree once in
///     <see cref="OnCreate" />, hands it to <see cref="Hud.Root" />, and just mutates the retained
///     widgets'
///     properties in <see cref="OnUpdate" />. The host (the editor's viewport in play mode) measures,
///     lays
///     out, paints, and routes input to it every frame — so anything from the widget framework works
///     here
///     (layout, <c>Card</c>/<c>ProgressBar</c>/<c>Label</c>, even interactive controls).
/// </summary>
public sealed class WidgetHud : Component
{
    private ProgressBar? _bar;
    private float _elapsed;
    private Widget? _root;
    private Label? _valueLabel;

    [Export]
    [EditorTooltip("Title shown in the top-left status card")]
    public string Title { get; set; } = "Widget HUD";

    protected override void OnCreate()
    {
        _valueLabel = new Label("0.00", 13f, new Color(0.96f, 0.97f, 0.99f));
        _bar = new ProgressBar();

        var card = new Card(
            new Column {
                MainAxisSize = MainAxisSize.Min,
                CrossAxisAlign = CrossAxisAlignment.Start,
                Children = {
                    new Label(Title, 15f, new Color(0.62f, 0.67f, 0.74f)) {
                        FontWeight = FontWeight.SemiBold,
                    },
                    _valueLabel,
                },
            }
        ) {
            Color = new Color(
                0.05f,
                0.06f,
                0.08f,
                0.78f
            ),
            Padding = EdgeInsets.All(12f),
            Radius = 10f,
        };

        // A HUD is a Stack of placed surfaces over (mostly transparent) space: the status card pinned
        // top-left, a progress bar pinned along the bottom. Empty regions pass clicks through to the viewport.
        _root = new Stack {
            Children = {
                new Align(Alignment.TopLeft, new Padding(EdgeInsets.All(16f), card)),
                new Align(
                    Alignment.BottomCenter,
                    new Padding(EdgeInsets.All(20f), new SizedBox(360f, 8f, _bar))
                ),
            },
        };

        Hud.Root = _root;
    }

    protected override void OnUpdate(float deltaTime)
    {
        _elapsed += deltaTime;

        // Mutate the retained widgets in place — no rebuild, state preserved (this is the framework's model).
        var t = (MathF.Sin(_elapsed) + 1f) * 0.5f;
        if (_bar is not null) _bar.Value = t;
        if (_valueLabel is not null) _valueLabel.Text = t.ToString("F2");
    }

    protected override void OnDestroy()
    {
        // Hand the HUD back. PlaySession also clears Hud on stop, so this only matters when the component is
        // destroyed mid-play; guard so we never clobber a HUD another component installed afterwards.
        if (ReferenceEquals(Hud.Root, _root)) Hud.Root = null;
        _root = null;
        _bar = null;
        _valueLabel = null;
    }
}