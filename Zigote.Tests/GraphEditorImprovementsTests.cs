using Xunit;
using Zigote.Core;
using Zigote.Graphs.Core;
using Zigote.Graphs.Commands;
using Zigote.Graphs.Editor;
using Zigote.Graphs.Registry;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

public class GraphEditorImprovementsTests
{
    private sealed class Domain(params NodeDefinition[] definitions) : IGraphDomain
    {
        public string Id => "test";
        public string DisplayName => "Test";
        public IReadOnlyList<string> SupportedSchemas => [];
        public IReadOnlyList<GraphTypeDefinition> GetTypeDefinitions() => [];
        public IReadOnlyList<NodeDefinition> GetNodeDefinitions() => definitions;
        public bool CanCreateEdge(GraphDocument graph, GraphPinEndpoint from, GraphPinEndpoint to, out string? reason)
        { reason = null; return true; }
        public GraphValidationResult Validate(GraphDocument graph) => GraphValidationResult.Ok;
        public GraphCompileResult Compile(GraphDocument graph, GraphCompileContext context) => new() { Success = true };
    }

    private static GraphEditorState CreateState(NodeDefinition definition)
    {
        var registry = new GraphDomainRegistry();
        registry.RegisterDomain(new Domain(definition));
        var graph = new GraphDocument { DomainId = "test" };
        var node = new GraphNode { DefinitionId = definition.Id };
        graph.Nodes.Add(node);
        graph.EditorData.NodeLayouts[node.Id] = new NodeLayoutData { Width = 200f };
        return new GraphEditorState(graph, registry);
    }

    [Fact]
    public void RequiredInputsRespectDefaultsAndRealSourcePins()
    {
        var required = new PinDefinition { Id = "in", DisplayName = "Signal", IsRequired = true };
        var state = CreateState(new NodeDefinition {
            Id = "node", Inputs = [required,
                new PinDefinition { Id = "default", IsRequired = true, DefaultValue = GraphValue.FromFloat(0) },
                new PinDefinition { Id = "optional" }],
            Outputs = [new PinDefinition { Id = "out" }],
        });
        var node = state.Graph.Nodes[0];
        Assert.Single(state.LastValidation!.Diagnostics);
        Assert.False(state.LastCompileResult!.Success);
        state.AutoCompile = false;
        var edge = new GraphEdge { From = new(node.Id, "missing"), To = new(node.Id, "in") };
        state.Commands.Execute(new AddEdgeCommand(edge));
        Assert.False(state.LastValidation.IsValid);
        state.Commands.Undo();
        state.Commands.Execute(new AddEdgeCommand(new GraphEdge { From = new(node.Id, "out"), To = new(node.Id, "in") }));
        Assert.True(state.LastValidation.IsValid);
        state.Commands.Undo();
        Assert.False(state.LastValidation.IsValid);
    }

    [Fact]
    public void HiddenPaneUsesFullWidthAndRestoresRatio()
    {
        var first = new Box();
        var second = new Box();
        var split = new SplitPane(ThemeData.Dark, first, second) { SplitRatio = 0.63f };
        split.Measure(Constraints.Tight(1000, 600));
        split.Layout(new Offset(0, 0));
        float originalWidth = first.Bounds.Width;
        split.SecondVisible = false;
        split.Layout(new Offset(0, 0));
        Assert.Equal(1000, first.Bounds.Width);
        Assert.Single(split.GetChildren());
        Assert.Same(first, split.HitTest(new Offset(999, 100)));
        split.SecondVisible = true;
        split.Layout(new Offset(0, 0));
        Assert.Equal(originalWidth, first.Bounds.Width);
        Assert.Equal(0.63f, split.SplitRatio);
    }

    [Fact]
    public void NumericDragIsOneUndoAndCancelRestoresAbsentOverride()
    {
        var state = CreateState(new NodeDefinition { Id = "node", Properties = [
            new PropertyDefinition { Id = "gain", Type = GraphTypeRef.Float, DefaultValue = GraphValue.FromFloat(0.5f), Min = 0, Max = 1, Step = 0.01f },
        ] });
        var canvas = new GraphCanvas(state, ThemeData.Dark);
        canvas.Measure(Constraints.Tight(800, 600));
        canvas.Layout(new Offset(0, 0));
        var node = state.Graph.Nodes[0];
        canvas.OnPointerDown(new Offset(110, 80));
        canvas.OnPointerMove(new Offset(210, 80));
        canvas.OnPointerUp(new Offset(210, 80));
        Assert.InRange(node.Properties["gain"].AsFloat(), 0.9f, 1f);
        state.Commands.Undo();
        Assert.False(node.Properties.ContainsKey("gain"));
        canvas.OnPointerDown(new Offset(110, 80));
        canvas.OnPointerMove(new Offset(210, 80));
        canvas.OnPointerCancel();
        Assert.False(node.Properties.ContainsKey("gain"));
    }

    [Fact]
    public void SwitchReleaseOutsideDoesNotToggle()
    {
        var state = CreateState(new NodeDefinition { Id = "node", Properties = [
            new PropertyDefinition { Id = "enabled", Type = GraphTypeRef.Bool, DefaultValue = GraphValue.FromBool(false) },
        ] });
        var canvas = new GraphCanvas(state, ThemeData.Dark);
        canvas.Measure(Constraints.Tight(800, 600));
        canvas.Layout(new Offset(0, 0));
        canvas.OnPointerDown(new Offset(110, 80));
        canvas.OnPointerUp(new Offset(400, 60));
        Assert.Empty(state.Graph.Nodes[0].Properties);
        canvas.OnPointerDown(new Offset(110, 80));
        canvas.OnPointerUp(new Offset(110, 80));
        Assert.True(state.Graph.Nodes[0].Properties["enabled"].AsBool());
        state.Commands.Undo();
        Assert.Empty(state.Graph.Nodes[0].Properties);
    }

    [Fact]
    public void NumericTextEntryClampsAndEscapeCancels()
    {
        var state = CreateState(new NodeDefinition { Id = "node", Properties = [
            new PropertyDefinition { Id = "gain", Type = GraphTypeRef.Float, DefaultValue = GraphValue.FromFloat(0.5f), Min = 0, Max = 1 },
        ] });
        var canvas = new GraphCanvas(state, ThemeData.Dark);
        canvas.Measure(Constraints.Tight(800, 600));
        canvas.Layout(new Offset(0, 0));
        canvas.OnPointerDown(new Offset(110, 80));
        canvas.OnPointerUp(new Offset(110, 80));
        var field = Assert.IsAssignableFrom<TextField>(Assert.Single(canvas.GetChildren()));
        field.Text = "invalid";
        field.OnSubmitted!(field.Text);
        Assert.Single(canvas.GetChildren());
        field.Text = "5";
        field.OnSubmitted!(field.Text);
        Assert.Empty(canvas.GetChildren());
        Assert.Equal(1f, state.Graph.Nodes[0].Properties["gain"].AsFloat());
        state.Commands.Undo();
        Assert.Empty(state.Graph.Nodes[0].Properties);
        canvas.OnPointerDown(new Offset(110, 80));
        canvas.OnPointerUp(new Offset(110, 80));
        field = Assert.IsAssignableFrom<TextField>(Assert.Single(canvas.GetChildren()));
        field.Text = "0.75";
        field.OnKey('\0', 41, true, default);
        Assert.Empty(canvas.GetChildren());
        Assert.Empty(state.Graph.Nodes[0].Properties);
    }

    [Fact]
    public void RightClickEmptyCanvasUsesSearchAtClickPosition()
    {
        var state = CreateState(new NodeDefinition { Id = "node" });
        var canvas = new GraphCanvas(state, ThemeData.Dark);
        canvas.Measure(Constraints.Tight(800, 600));
        canvas.Layout(new Offset(20, 30));
        (float x, float y)? requested = null;
        canvas.SearchRequested += (x, y) => requested = (x, y);
        var click = new Offset(600, 300);
        canvas.OnRightClick(click);
        canvas.OnRightPointerUp(click);
        Assert.Equal((580f, 270f), requested!.Value);
        Assert.Equal(click, canvas.SearchAnchor);
        Assert.Null(canvas.SearchConnection);
        Assert.Single(state.Graph.Nodes);
    }

    [Fact]
    public void ValueEditorReplacesSelectionAndPreservesLabelSpace()
    {
        var state = CreateState(new NodeDefinition { Id = "node", Properties = [
            new PropertyDefinition { Id = "gain", DisplayName = "Gain", Type = GraphTypeRef.Float,
                DefaultValue = GraphValue.FromFloat(0.5f), Unit = "dB" },
        ] });
        var canvas = new GraphCanvas(state, ThemeData.Dark);
        canvas.Measure(Constraints.Tight(800, 600));
        canvas.Layout(new Offset(0, 0));
        canvas.OnPointerDown(new Offset(110, 80));
        canvas.OnPointerUp(new Offset(110, 80));
        var field = Assert.IsAssignableFrom<TextField>(Assert.Single(canvas.GetChildren()));
        Assert.False(field.ShowBackground);
        Assert.Equal(11.5f, field.FontSize);
        Assert.True(field.Bounds.X > 80f);
        Assert.True(field.Bounds.Right < 210f);
        field.OnTextInput("0.75");
        Assert.Equal("0.75", field.Text);
        field.OnSubmitted!(field.Text);
        Assert.Equal(0.75f, state.Graph.Nodes[0].Properties["gain"].AsFloat());
        state.Commands.Undo();
        Assert.Empty(state.Graph.Nodes[0].Properties);
    }

    [Fact]
    public void SearchScrollKeepsKeyboardSelectionVisibleAndEmptySearchCannotAdd()
    {
        var registry = new GraphDomainRegistry();
        registry.RegisterDomain(new Domain(Enumerable.Range(0, 15).Select(i => new NodeDefinition {
            Id = $"node{i:D2}", DomainId = "test", DisplayName = $"Node {i:D2}", Category = "Test",
        }).ToArray()));
        var menu = new GraphSearchMenu(new GraphEditorState(new GraphDocument { DomainId = "test" }, registry), ThemeData.Dark);
        string? chosen = null;
        menu.NodeChosen += (id, _, _) => chosen = id;
        menu.Show(100, 200);
        menu.Measure(Constraints.Tight(340, 228)); // three result rows
        menu.Layout(new Offset(0, 0));
        menu.OnScroll(0, -1);
        var field = Assert.IsAssignableFrom<TextField>(Assert.Single(menu.GetChildren()));
        field.OnKey('\0', 40, true, default);
        Assert.Equal("node02", chosen);
        Assert.False(menu.IsVisible);
        chosen = null;
        menu.Show(100, 200);
        field.OnTextInput("no matches");
        field.OnKey('\0', 40, true, default);
        Assert.Null(chosen);
        Assert.True(menu.IsVisible);
        field.OnKey('\0', 41, true, default);
        Assert.False(menu.IsVisible);
    }

    private sealed class Box : Widget
    {
        public override void Paint(Zigote.Core.Paint.PaintList paint) { }
        private Size _size;
        public override Size Measure(Constraints c) => _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
        public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
    }
}
