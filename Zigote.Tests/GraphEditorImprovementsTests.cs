using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Graphs.Commands;
using Zigote.Graphs.Core;
using Zigote.Graphs.Editor;
using Zigote.Graphs.Registry;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

public class GraphEditorImprovementsTests
{
    private static GraphEditorState CreateState(NodeDefinition definition)
    {
        var registry = new GraphDomainRegistry();
        registry.RegisterDomain(new Domain(definition));
        var graph = new GraphDocument { DomainId = "test" };
        var node = new GraphNode { DefinitionId = definition.Id };
        graph.Nodes.Add(node);
        graph.EditorData.NodeLayouts[node.Id] = new NodeLayoutData { Width = 200f };
        return new GraphEditorState(graph: graph, registry: registry);
    }

    [Fact]
    public void RequiredInputsRespectDefaultsAndRealSourcePins()
    {
        var required = new PinDefinition {
            Id = "in",
            DisplayName = "Signal",
            IsRequired = true,
        };
        var state = CreateState(
            new NodeDefinition {
                Id = "node",
                Inputs = [
                    required,
                    new PinDefinition {
                        Id = "default",
                        IsRequired = true,
                        DefaultValue = GraphValue.FromFloat(0),
                    },
                    new PinDefinition { Id = "optional" },
                ],
                Outputs = [new PinDefinition { Id = "out" }],
            }
        );
        var node = state.Graph.Nodes[0];
        Assert.Single(state.LastValidation!.Diagnostics);
        Assert.False(state.LastCompileResult!.Success);
        state.AutoCompile = false;
        var edge = new GraphEdge {
            From = new GraphPinEndpoint(NodeId: node.Id, PinId: "missing"),
            To = new GraphPinEndpoint(NodeId: node.Id, PinId: "in"),
        };
        state.Commands.Execute(new AddEdgeCommand(edge));
        Assert.False(state.LastValidation.IsValid);
        state.Commands.Undo();
        state.Commands.Execute(
            new AddEdgeCommand(
                new GraphEdge {
                    From = new GraphPinEndpoint(NodeId: node.Id, PinId: "out"),
                    To = new GraphPinEndpoint(NodeId: node.Id, PinId: "in"),
                }
            )
        );
        Assert.True(state.LastValidation.IsValid);
        state.Commands.Undo();
        Assert.False(state.LastValidation.IsValid);
    }

    [Fact]
    public void HiddenPaneUsesFullWidthAndRestoresRatio()
    {
        var first = new Box();
        var second = new Box();
        var split = new AdwPaned(first: first, second: second) { Position = 0.63f };
        split.Measure(Constraints.Tight(width: 1000, height: 600));
        split.Layout(new Offset(x: 0, y: 0));
        float originalWidth = first.Bounds.Width;
        split.Second = null;
        split.Layout(new Offset(x: 0, y: 0));
        Assert.Equal(expected: 1000, actual: first.Bounds.Width);
        Assert.Single(split.GetChildren());
        Assert.Same(expected: first, actual: split.HitTest(new Offset(x: 999, y: 100)));
        split.Second = second;
        split.Layout(new Offset(x: 0, y: 0));
        Assert.Equal(expected: originalWidth, actual: first.Bounds.Width);
        Assert.Equal(expected: 0.63f, actual: split.Position);
    }

    [Fact]
    public void NumericDragIsOneUndoAndCancelRestoresAbsentOverride()
    {
        var state = CreateState(
            new NodeDefinition {
                Id = "node",
                Properties = [
                    new PropertyDefinition {
                        Id = "gain",
                        Type = GraphTypeRef.Float,
                        DefaultValue = GraphValue.FromFloat(0.5f),
                        Min = 0,
                        Max = 1,
                        Step = 0.01f,
                    },
                ],
            }
        );
        var canvas = new GraphCanvas(state: state, theme: ThemeData.Dark);
        canvas.Measure(Constraints.Tight(width: 800, height: 600));
        canvas.Layout(new Offset(x: 0, y: 0));
        var node = state.Graph.Nodes[0];
        canvas.OnPointerDown(new Offset(x: 110, y: 80));
        canvas.OnPointerMove(new Offset(x: 210, y: 80));
        canvas.OnPointerUp(new Offset(x: 210, y: 80));
        Assert.InRange(actual: node.Properties["gain"].AsFloat(), low: 0.9f, high: 1f);
        state.Commands.Undo();
        Assert.False(node.Properties.ContainsKey("gain"));
        canvas.OnPointerDown(new Offset(x: 110, y: 80));
        canvas.OnPointerMove(new Offset(x: 210, y: 80));
        canvas.OnPointerCancel();
        Assert.False(node.Properties.ContainsKey("gain"));
    }

    [Fact]
    public void SwitchReleaseOutsideDoesNotToggle()
    {
        var state = CreateState(
            new NodeDefinition {
                Id = "node",
                Properties = [
                    new PropertyDefinition {
                        Id = "enabled",
                        Type = GraphTypeRef.Bool,
                        DefaultValue = GraphValue.FromBool(false),
                    },
                ],
            }
        );
        var canvas = new GraphCanvas(state: state, theme: ThemeData.Dark);
        canvas.Measure(Constraints.Tight(width: 800, height: 600));
        canvas.Layout(new Offset(x: 0, y: 0));
        canvas.OnPointerDown(new Offset(x: 110, y: 80));
        canvas.OnPointerUp(new Offset(x: 400, y: 60));
        Assert.Empty(state.Graph.Nodes[0].Properties);
        canvas.OnPointerDown(new Offset(x: 110, y: 80));
        canvas.OnPointerUp(new Offset(x: 110, y: 80));
        Assert.True(state.Graph.Nodes[0].Properties["enabled"].AsBool());
        state.Commands.Undo();
        Assert.Empty(state.Graph.Nodes[0].Properties);
    }

    [Fact]
    public void NumericTextEntryClampsAndEscapeCancels()
    {
        var state = CreateState(
            new NodeDefinition {
                Id = "node",
                Properties = [
                    new PropertyDefinition {
                        Id = "gain",
                        Type = GraphTypeRef.Float,
                        DefaultValue = GraphValue.FromFloat(0.5f),
                        Min = 0,
                        Max = 1,
                    },
                ],
            }
        );
        var canvas = new GraphCanvas(state: state, theme: ThemeData.Dark);
        canvas.Measure(Constraints.Tight(width: 800, height: 600));
        canvas.Layout(new Offset(x: 0, y: 0));
        canvas.OnPointerDown(new Offset(x: 110, y: 80));
        canvas.OnPointerUp(new Offset(x: 110, y: 80));
        var field = Assert.IsAssignableFrom<AdwTextField>(Assert.Single(canvas.GetChildren()));
        field.Text = "invalid";
        field.OnSubmitted!(field.Text);
        Assert.Single(canvas.GetChildren());
        field.Text = "5";
        field.OnSubmitted!(field.Text);
        Assert.Empty(canvas.GetChildren());
        Assert.Equal(expected: 1f, actual: state.Graph.Nodes[0].Properties["gain"].AsFloat());
        state.Commands.Undo();
        Assert.Empty(state.Graph.Nodes[0].Properties);
        canvas.OnPointerDown(new Offset(x: 110, y: 80));
        canvas.OnPointerUp(new Offset(x: 110, y: 80));
        field = Assert.IsAssignableFrom<AdwTextField>(Assert.Single(canvas.GetChildren()));
        field.Text = "0.75";
        field.OnKey(
            keyChar: '\0',
            scancode: 41,
            down: true,
            mods: default
        );
        Assert.Empty(canvas.GetChildren());
        Assert.Empty(state.Graph.Nodes[0].Properties);
    }

    [Fact]
    public void RightClickEmptyCanvasUsesSearchAtClickPosition()
    {
        var state = CreateState(new NodeDefinition { Id = "node" });
        var canvas = new GraphCanvas(state: state, theme: ThemeData.Dark);
        canvas.Measure(Constraints.Tight(width: 800, height: 600));
        canvas.Layout(new Offset(x: 20, y: 30));
        (float x, float y)? requested = null;
        canvas.SearchRequested += (x, y) => requested = (x, y);
        var click = new Offset(x: 600, y: 300);
        canvas.OnRightClick(click);
        canvas.OnRightPointerUp(click);
        Assert.Equal(expected: (580f, 270f), actual: requested!.Value);
        Assert.Equal(expected: click, actual: canvas.SearchAnchor);
        Assert.Null(canvas.SearchConnection);
        Assert.Single(state.Graph.Nodes);
    }

    [Fact]
    public void ValueEditorReplacesSelectionAndPreservesLabelSpace()
    {
        var state = CreateState(
            new NodeDefinition {
                Id = "node",
                Properties = [
                    new PropertyDefinition {
                        Id = "gain",
                        DisplayName = "Gain",
                        Type = GraphTypeRef.Float,
                        DefaultValue = GraphValue.FromFloat(0.5f),
                        Unit = "dB",
                    },
                ],
            }
        );
        var canvas = new GraphCanvas(state: state, theme: ThemeData.Dark);
        canvas.Measure(Constraints.Tight(width: 800, height: 600));
        canvas.Layout(new Offset(x: 0, y: 0));
        canvas.OnPointerDown(new Offset(x: 110, y: 80));
        canvas.OnPointerUp(new Offset(x: 110, y: 80));
        var field = Assert.IsAssignableFrom<AdwTextField>(Assert.Single(canvas.GetChildren()));
        Assert.False(field.ShowBackground);
        Assert.Equal(expected: 11.5f, actual: field.FontSize);
        Assert.True(field.Bounds.X > 80f);
        Assert.True(field.Bounds.Right < 210f);
        field.OnTextInput("0.75");
        Assert.Equal(expected: "0.75", actual: field.Text);
        field.OnSubmitted!(field.Text);
        Assert.Equal(expected: 0.75f, actual: state.Graph.Nodes[0].Properties["gain"].AsFloat());
        state.Commands.Undo();
        Assert.Empty(state.Graph.Nodes[0].Properties);
    }

    [Fact]
    public void SearchScrollKeepsKeyboardSelectionVisibleAndEmptySearchCannotAdd()
    {
        var registry = new GraphDomainRegistry();
        registry.RegisterDomain(
            new Domain(
                [
                    .. Enumerable.Range(start: 0, count: 15).Select(i => new NodeDefinition {
                            Id = $"node{i:D2}",
                            DomainId = "test",
                            DisplayName = $"Node {i:D2}",
                            Category = "Test",
                        }
                    ),
                ]
            )
        );
        var menu = new GraphSearchMenu(
            state: new GraphEditorState(
                graph: new GraphDocument { DomainId = "test" },
                registry: registry
            ),
            theme: ThemeData.Dark
        );
        string? chosen = null;
        menu.NodeChosen += (id, _, _) => chosen = id;
        menu.Show(worldX: 100, worldY: 200);
        menu.Measure(Constraints.Tight(width: 340, height: 228)); // three result rows
        menu.Layout(new Offset(x: 0, y: 0));
        menu.OnScroll(dx: 0, dy: -1);
        var field = Assert.IsAssignableFrom<AdwTextField>(Assert.Single(menu.GetChildren()));
        field.OnKey(
            keyChar: '\0',
            scancode: 40,
            down: true,
            mods: default
        );
        Assert.Equal(expected: "node02", actual: chosen);
        Assert.False(menu.IsVisible);
        chosen = null;
        menu.Show(worldX: 100, worldY: 200);
        field.OnTextInput("no matches");
        field.OnKey(
            keyChar: '\0',
            scancode: 40,
            down: true,
            mods: default
        );
        Assert.Null(chosen);
        Assert.True(menu.IsVisible);
        field.OnKey(
            keyChar: '\0',
            scancode: 41,
            down: true,
            mods: default
        );
        Assert.False(menu.IsVisible);
    }

    private sealed class Domain(params NodeDefinition[] definitions) : IGraphDomain
    {
        public string Id => "test";
        public string DisplayName => "Test";
        public IReadOnlyList<string> SupportedSchemas => [];
        public IReadOnlyList<GraphTypeDefinition> GetTypeDefinitions() => [];
        public IReadOnlyList<NodeDefinition> GetNodeDefinitions() => definitions;

        public bool CanCreateEdge(GraphDocument graph, GraphPinEndpoint from, GraphPinEndpoint to,
            out string? reason)
        {
            reason = null;
            return true;
        }

        public GraphValidationResult Validate(GraphDocument graph) => GraphValidationResult.Ok;

        public GraphCompileResult Compile(GraphDocument graph, GraphCompileContext context) =>
            new() { Success = true };
    }

    private sealed class Box : Widget
    {
        private Size _size;
        public override void Paint(PaintList paint) { }

        public override Size Measure(Constraints c) =>
            _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));

        public override void Layout(Offset origin) => Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }
}
