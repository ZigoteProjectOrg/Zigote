using Zigote.Graphs.Core;
using Zigote.Graphs.Editor;
using Zigote.Graphs.Commands;
using Zigote.UI.Material;

namespace AdwaitaGallery.Pages;

/// <summary>Full-height interactive graph editor, independent of Palco and its audio engine.</summary>
public sealed class GraphsPage : ComposedWidget
{
    private readonly Signal<int> _revision = new(0);
    private readonly Signal<bool> _help = new(false);
    private GraphDocument _document = GraphsDomain.Sample(0);
    private int _sample;
    private bool _frameNext = true;

    internal void Load(int sample)
    {
        _sample = sample;
        _document = GraphsDomain.Sample(sample);
        _frameNext = true;
        _revision.Value++;
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        Widget SampleButton(string label, int sample) => new Watch(() => {
            _ = _revision.Value;
            return new AdwButton(label, () => Load(sample)) {
                Style = _sample == sample ? AdwButtonStyle.Suggested : AdwButtonStyle.Regular,
            };
        });
        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
            Children = {
                new Padding(EdgeInsets.All(10f), Demo.Bar(
                    SampleButton("Playground", 0),
                    SampleButton("Repair wiring", 1),
                    SampleButton("Large graph", 2),
                    new AdwButton("Reset example", () => Load(_sample)),
                    new AdwButton("How to use", () => _help.Value = !_help.Value))),
                new Watch(() => {
                    _ = _revision.Value;
                    return new Padding(EdgeInsets.Symmetric(horizontal: 16f, vertical: 6f),
                    Demo.Caption(_sample switch {
                        1 => "Repair: delete the red Text → Value cable. Connect Number → Math A, then Math → Preview Value.",
                        2 => "29 nodes: zoom in to edit, enable Minimap, and press F to frame the board. Reset restores this example.",
                        _ => "Live playground: edit Number or Math to update Preview. Expand Switch to toggle it; Text edits the caption.",
                    }));
                }),
                new Watch(() => _help.Value ? new SizedBox(height: 176f, child: new SingleChildScrollView {
                    Child = new Padding(EdgeInsets.All(12f), new Column(spacing: 5f, mainAxisSize: MainAxisSize.Min,
                        crossAxisAlignment: CrossAxisAlignment.Stretch) { Children = {
                        Demo.Caption("CONNECT · Drag between matching socket colors. Wrong types, occupied inputs, duplicate wires and feedback loops turn red with an explanation."),
                        Demo.Caption("SEARCH · Drop an input or output cable on empty canvas to search compatible nodes. Type, use ↑/↓ and Enter; Esc cancels. Shift+A / Space opens all nodes."),
                        Demo.Caption("EDIT · Drag numeric controls; Shift makes fine adjustments. Click to type. Switches, text, integer values and enum choices are live. Inspector shows descriptions and full controls."),
                        Demo.Caption("ARRANGE · Chevron / H collapses nodes. Shift-click or box-select, then drag the group. Drag the lower-right corner to resize. Delete removes the selected node or wire; right-click opens context actions."),
                        Demo.Caption("NAVIGATE · Right-drag pans; wheel zooms toward the pointer. F / Home frames all. Toggle Minimap or Inspector. Cmd/Ctrl+Z undoes; Cmd/Ctrl+Shift+Z redoes. Add-and-connect and group edits undo together."),
                        Demo.Caption("EXAMPLES · Switching or resetting an example discards its edits. Theme changes preserve the document. The live preview appears only while the graph is valid."),
                    } }),
                }) : new SizedBox(height: 0f)),
                new Expanded(new Watch(() => {
                    _ = _revision.Value;
                    var registry = new GraphsDomain(theme).Registry();
                    var panel = new GraphEditorPanel(_document, registry, theme, App.Active!, inspectorVisible: false);
                    bool frame = _frameNext;
                    _frameNext = false;
                    return new FittedEditor(panel, frame);
                })),
            },
        };
    }

    private sealed class FittedEditor(GraphEditorPanel panel, bool frame) : Widget
    {
        private Size _size;
        public override Size Measure(Constraints c) => _size = panel.Measure(c);
        public override void Layout(Offset origin)
        {
            Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
            panel.Layout(origin);
            if (frame && _size.Width > 0 && _size.Height > 0)
            {
                frame = false;
                FindCanvas(panel)?.FrameAll();
                panel.Layout(origin);
            }
        }
        private static GraphCanvas? FindCanvas(Widget widget) => widget is GraphCanvas canvas ? canvas
            : widget.GetChildren().Select(FindCanvas).FirstOrDefault(c => c is not null);
        public override void Paint(PaintList paint) => panel.Paint(paint);
        public override Widget? HitTest(Offset point) => panel.HitTest(point);
        public override IEnumerable<Widget> GetChildren() => [panel];
    }

    internal static IEnumerable<string> SelfCheck()
    {
        var errors = new List<string>();
        void Check(bool condition, string message) { if (!condition) errors.Add(message); }
        var domain = new GraphsDomain(ThemeData.Dark);
        var registry = domain.Registry();
        var graph = GraphsDomain.Sample(0);
        Check(domain.Validate(graph).IsValid, "playground should be valid");
        Check(domain.Evaluate(graph).Value == 0.75f, "preview should evaluate Number + default B");
        var number = graph.Nodes.First(n => n.DefinitionId == "demo.number");
        number.Properties["value"] = GraphValue.FromFloat(0.25f);
        Check(domain.Evaluate(graph).Value == 0.5f, "numeric edits should change the result");
        var operation = graph.Nodes.First(n => n.DefinitionId == "demo.math");
        operation.Properties["operation"] = GraphValue.FromInt(1);
        Check(domain.Evaluate(graph).Value == 0.0625f, "enum should select multiplication");
        graph.Nodes.First(n => n.DefinitionId == "demo.text").Properties["text"] = GraphValue.FromString("Edited caption");
        graph.Nodes.First(n => n.DefinitionId == "demo.toggle").Properties["enabled"] = GraphValue.FromBool(false);
        graph.Nodes.First(n => n.DefinitionId == "demo.output").Properties["digits"] = GraphValue.FromInt(4);
        var result = domain.Evaluate(graph);
        Check(result.Caption == "Edited caption" && !result.Enabled && result.Digits == 4, "text, boolean and integer controls should affect preview");
        Check(domain.Compile(graph, new()).CompiledArtifact is Widget, "compile should produce a live widget");
        var menuState = new GraphEditorState(graph, registry);
        var menu = new GraphSearchMenu(menuState, ThemeData.Dark);
        string? chosen = null;
        menu.NodeChosen += (id, _, _) => chosen = id;
        menu.Show(0, 0, new GraphPinEndpoint(operation.Id, "b"));
        var field = (TextField)menu.GetChildren().Single();
        field.Text = "number";
        field.OnChanged?.Invoke(field.Text);
        field.OnKey('\0', 40, true, default);
        Check(chosen == "demo.number" && menu.ChosenPinId == "out", "input cable search should offer a compatible source");
        menu.Show(0, 0, new GraphPinEndpoint(number.Id, "out"));
        field.Text = "text";
        field.OnChanged?.Invoke(field.Text);
        chosen = null;
        field.OnKey('\0', 40, true, default);
        Check(chosen is null && menu.IsVisible, "numeric cable search should exclude text-only nodes");
        field.OnKey('\0', 41, true, default);
        Check(!menu.IsVisible, "Escape should cancel search");
        Check(domain.Validate(GraphsDomain.Sample(2)).IsValid, "large graph should be valid");
        var broken = GraphsDomain.Sample(1);
        Check(!domain.Validate(broken).IsValid, "repair example should start invalid");
        var source = broken.Nodes.First(n => n.DefinitionId == "demo.number");
        var math = broken.Nodes.First(n => n.DefinitionId == "demo.math");
        var output = broken.Nodes.First(n => n.DefinitionId == "demo.output");
        var invalid = broken.Edges.Single(e => e.To.PinId == "value");
        Check(!GraphConnections.CanConnect(broken, registry, invalid.From, invalid.To, out _, invalid.Id), "bad stored cable should render invalid");
        var state = new GraphEditorState(broken, registry);
        state.Commands.Execute(new DeleteEdgeCommand(invalid.Id));
        state.Commands.Execute(new AddEdgeCommand(new GraphEdge { From = new(source.Id, "out"), To = new(math.Id, "a") }));
        state.Commands.Execute(new AddEdgeCommand(new GraphEdge { From = new(math.Id, "out"), To = new(output.Id, "value") }));
        Check(state.LastCompileResult?.Success == true, "documented repair should compile");
        state.Commands.Undo();
        Check(state.LastCompileResult?.Success == false, "undo repair should expose the missing input");
        state.Commands.Redo();
        Check(state.LastCompileResult?.Success == true, "redo repair should restore preview");
        var extra = new GraphNode { DefinitionId = "demo.math" };
        state.Commands.Execute(new AddNodeCommand(extra, 0, 0));
        state.Commands.Execute(new AddEdgeCommand(new GraphEdge { From = new(math.Id, "out"), To = new(extra.Id, "a") }));
        Check(!GraphConnections.CanConnect(broken, registry, new(extra.Id, "out"), new(math.Id, "b"), out var reason)
            && reason.Contains("feedback"), "feedback should be rejected before release");
        return errors;
    }
}
