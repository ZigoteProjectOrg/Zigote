using Zigote.Graphs.Core;
using Zigote.Graphs.Editor;
using Zigote.Graphs.Commands;
using Zigote.Graphs.Registry;
using Zigote.UI.Adwaita;

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

        Widget SampleButton(string label, int sample) => new Watch(() =>
            {
                _ = _revision.Value;
                return new AdwButton(label: label, onPressed: () => Load(sample)) {
                    Style = _sample == sample ? AdwButtonStyle.Suggested : AdwButtonStyle.Regular,
                };
            }
        );

        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
            Children = {
                new Padding(
                    padding: EdgeInsets.All(10f),
                    child: Demo.Bar(
                        SampleButton(label: "Playground", sample: 0),
                        SampleButton(label: "Repair wiring", sample: 1),
                        SampleButton(label: "Large graph", sample: 2),
                        new AdwButton(label: "Reset example", onPressed: () => Load(_sample)),
                        new AdwButton(
                            label: "How to use",
                            onPressed: () => _help.Value = !_help.Value
                        )
                    )
                ),
                new Watch(() =>
                    {
                        _ = _revision.Value;
                        return new Padding(
                            padding: EdgeInsets.Symmetric(horizontal: 16f, vertical: 6f),
                            child: Demo.Caption(
                                _sample switch {
                                    1 =>
                                        "Repair: delete the red Text → Value cable. Connect Number → Math A, then Math → Preview Value.",
                                    2 =>
                                        "29 nodes: zoom in to edit, enable Minimap, and press F to frame the board. Reset restores this example.",
                                    _ =>
                                        "Live playground: edit Number or Math to update Preview. Expand Switch to toggle it; Text edits the caption.",
                                }
                            )
                        );
                    }
                ),
                new Watch(() => _help.Value
                    ? new SizedBox(
                        height: 176f,
                        child: new SingleChildScrollView {
                            Child = new Padding(
                                padding: EdgeInsets.All(12f),
                                child: new Column(
                                    spacing: 5f,
                                    mainAxisSize: MainAxisSize.Min,
                                    crossAxisAlignment: CrossAxisAlignment.Stretch
                                ) {
                                    Children = {
                                        Demo.Caption(
                                            "CONNECT · Drag between matching socket colors. Wrong types, occupied inputs, duplicate wires and feedback loops turn red with an explanation."
                                        ),
                                        Demo.Caption(
                                            "SEARCH · Drop an input or output cable on empty canvas to search compatible nodes. Type, use ↑/↓ and Enter; Esc cancels. Shift+A / Space opens all nodes."
                                        ),
                                        Demo.Caption(
                                            "EDIT · Drag numeric controls; Shift makes fine adjustments. Click to type. Switches, text, integer values and enum choices are live. Inspector shows descriptions and full controls."
                                        ),
                                        Demo.Caption(
                                            "ARRANGE · Chevron / H collapses nodes. Shift-click or box-select, then drag the group. Drag the lower-right corner to resize. Delete removes the selected node or wire; right-click opens context actions."
                                        ),
                                        Demo.Caption(
                                            "NAVIGATE · Right-drag pans; wheel zooms toward the pointer. F / Home frames all. Toggle Minimap or Inspector. Cmd/Ctrl+Z undoes; Cmd/Ctrl+Shift+Z redoes. Add-and-connect and group edits undo together."
                                        ),
                                        Demo.Caption(
                                            "EXAMPLES · Switching or resetting an example discards its edits. Theme changes preserve the document. The live preview appears only while the graph is valid."
                                        ),
                                    },
                                }
                            ),
                        }
                    )
                    : new SizedBox(height: 0f)
                ),
                new Expanded(
                    new Watch(() =>
                        {
                            _ = _revision.Value;
                            var registry = new GraphsDomain(theme).Registry();
                            var panel = new GraphEditorPanel(
                                graph: _document,
                                registry: registry,
                                theme: theme,
                                app: App.Active!,
                                inspectorVisible: false
                            );
                            bool frame = _frameNext;
                            _frameNext = false;
                            return new FittedEditor(panel: panel, frame: frame);
                        }
                    )
                ),
            },
        };
    }

    private sealed class FittedEditor(GraphEditorPanel panel, bool frame) : Widget
    {
        private Size _size;
        public override Size Measure(Constraints c) => _size = panel.Measure(c);

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _size.Width,
                height: _size.Height
            );
            panel.Layout(origin);
            if (frame && _size.Width > 0 && _size.Height > 0)
            {
                frame = false;
                FindCanvas(panel)?.FrameAll();
                panel.Layout(origin);
            }
        }

        private static GraphCanvas? FindCanvas(Widget widget) => widget is GraphCanvas canvas
            ? canvas
            : widget.GetChildren().Select(FindCanvas).FirstOrDefault(c => c is not null);

        public override void Paint(PaintList paint) => panel.Paint(paint);
        public override Widget? HitTest(Offset point) => panel.HitTest(point);
        public override IEnumerable<Widget> GetChildren() => [panel];
    }

    internal static IEnumerable<string> SelfCheck()
    {
        var errors = new List<string>();

        void Check(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        var domain = new GraphsDomain(ThemeData.Dark);
        var registry = domain.Registry();
        var graph = GraphsDomain.Sample(0);
        Check(condition: domain.Validate(graph).IsValid, message: "playground should be valid");
        Check(
            condition: domain.Evaluate(graph).Value == 0.75f,
            message: "preview should evaluate Number + default B"
        );
        var number = graph.Nodes.First(n => n.DefinitionId == "demo.number");
        number.Properties["value"] = GraphValue.FromFloat(0.25f);
        Check(
            condition: domain.Evaluate(graph).Value == 0.5f,
            message: "numeric edits should change the result"
        );
        var operation = graph.Nodes.First(n => n.DefinitionId == "demo.math");
        operation.Properties["operation"] = GraphValue.FromInt(1);
        Check(
            condition: domain.Evaluate(graph).Value == 0.0625f,
            message: "enum should select multiplication"
        );
        graph.Nodes.First(n => n.DefinitionId == "demo.text").Properties["text"] =
            GraphValue.FromString("Edited caption");
        graph.Nodes.First(n => n.DefinitionId == "demo.toggle").Properties["enabled"] =
            GraphValue.FromBool(false);
        graph.Nodes.First(n => n.DefinitionId == "demo.output").Properties["digits"] =
            GraphValue.FromInt(4);
        var result = domain.Evaluate(graph);
        Check(
            condition: result.Caption == "Edited caption" && !result.Enabled && result.Digits == 4,
            message: "text, boolean and integer controls should affect preview"
        );
        Check(
            condition: domain.Compile(graph: graph, context: new GraphCompileContext())
                .CompiledArtifact is Widget,
            message: "compile should produce a live widget"
        );
        var menuState = new GraphEditorState(graph: graph, registry: registry);
        var menu = new GraphSearchMenu(state: menuState, theme: ThemeData.Dark);
        string? chosen = null;
        menu.NodeChosen += (id, _, _) => chosen = id;
        menu.Show(
            worldX: 0,
            worldY: 0,
            connection: new GraphPinEndpoint(NodeId: operation.Id, PinId: "b")
        );
        var field = (AdwTextField)menu.GetChildren().Single();
        field.Text = "number";
        field.OnChanged?.Invoke(field.Text);
        field.OnKey(
            keyChar: '\0',
            scancode: 40,
            down: true,
            mods: default
        );
        Check(
            condition: chosen == "demo.number" && menu.ChosenPinId == "out",
            message: "input cable search should offer a compatible source"
        );
        menu.Show(
            worldX: 0,
            worldY: 0,
            connection: new GraphPinEndpoint(NodeId: number.Id, PinId: "out")
        );
        field.Text = "text";
        field.OnChanged?.Invoke(field.Text);
        chosen = null;
        field.OnKey(
            keyChar: '\0',
            scancode: 40,
            down: true,
            mods: default
        );
        Check(
            condition: chosen is null && menu.IsVisible,
            message: "numeric cable search should exclude text-only nodes"
        );
        field.OnKey(
            keyChar: '\0',
            scancode: 41,
            down: true,
            mods: default
        );
        Check(condition: !menu.IsVisible, message: "Escape should cancel search");
        Check(
            condition: domain.Validate(GraphsDomain.Sample(2)).IsValid,
            message: "large graph should be valid"
        );
        var broken = GraphsDomain.Sample(1);
        Check(
            condition: !domain.Validate(broken).IsValid,
            message: "repair example should start invalid"
        );
        var source = broken.Nodes.First(n => n.DefinitionId == "demo.number");
        var math = broken.Nodes.First(n => n.DefinitionId == "demo.math");
        var output = broken.Nodes.First(n => n.DefinitionId == "demo.output");
        var invalid = broken.Edges.Single(e => e.To.PinId == "value");
        Check(
            condition: !GraphConnections.CanConnect(
                graph: broken,
                registry: registry,
                a: invalid.From,
                b: invalid.To,
                reason: out _,
                ignoreEdge: invalid.Id
            ),
            message: "bad stored cable should render invalid"
        );
        var state = new GraphEditorState(graph: broken, registry: registry);
        state.Commands.Execute(new DeleteEdgeCommand(invalid.Id));
        state.Commands.Execute(
            new AddEdgeCommand(
                new GraphEdge {
                    From = new GraphPinEndpoint(NodeId: source.Id, PinId: "out"),
                    To = new GraphPinEndpoint(NodeId: math.Id, PinId: "a"),
                }
            )
        );
        state.Commands.Execute(
            new AddEdgeCommand(
                new GraphEdge {
                    From = new GraphPinEndpoint(NodeId: math.Id, PinId: "out"),
                    To = new GraphPinEndpoint(NodeId: output.Id, PinId: "value"),
                }
            )
        );
        Check(
            condition: state.LastCompileResult?.Success == true,
            message: "documented repair should compile"
        );
        state.Commands.Undo();
        Check(
            condition: state.LastCompileResult?.Success == false,
            message: "undo repair should expose the missing input"
        );
        state.Commands.Redo();
        Check(
            condition: state.LastCompileResult?.Success == true,
            message: "redo repair should restore preview"
        );
        var extra = new GraphNode { DefinitionId = "demo.math" };
        state.Commands.Execute(new AddNodeCommand(node: extra, x: 0, y: 0));
        state.Commands.Execute(
            new AddEdgeCommand(
                new GraphEdge {
                    From = new GraphPinEndpoint(NodeId: math.Id, PinId: "out"),
                    To = new GraphPinEndpoint(NodeId: extra.Id, PinId: "a"),
                }
            )
        );
        Check(
            condition: !GraphConnections.CanConnect(
                           graph: broken,
                           registry: registry,
                           a: new GraphPinEndpoint(NodeId: extra.Id, PinId: "out"),
                           b: new GraphPinEndpoint(NodeId: math.Id, PinId: "b"),
                           reason: out string reason
                       )
                       && reason.Contains("feedback"),
            message: "feedback should be rejected before release"
        );
        return errors;
    }
}
