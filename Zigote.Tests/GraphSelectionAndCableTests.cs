using Xunit;
using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Graphs.Commands;
using Zigote.Graphs.Core;
using Zigote.Graphs.Editor;
using Zigote.Graphs.Registry;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Tests;

/// <summary>
///     Selection and cable gestures on the canvas, driven through the same pointer and key entry
///     points the app uses. Three cards: A (40,40) and B (40,200) on the left, C (400,40) on the
///     right. Each is 230 wide and 84 tall, with its output socket at +45 and its input at +69.
/// </summary>
public class GraphSelectionAndCableTests
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

    private sealed record Scene(GraphEditorState State, GraphCanvas Canvas, GraphNode A, GraphNode B, GraphNode C)
    {
        public void Click(float x, float y)
        {
            Canvas.OnPointerDown(new Offset(x, y));
            Canvas.OnPointerUp(new Offset(x, y));
        }

        public void Drag(float x0, float y0, float x1, float y1)
        {
            Canvas.OnPointerDown(new Offset(x0, y0));
            Canvas.OnPointerMove(new Offset(x1, y1));
            Canvas.OnPointerUp(new Offset(x1, y1));
        }

        // No App owns the canvas here, so modifiers arrive the way they do in the app: on a key event.
        public void Hold(Modifiers mods) => Canvas.OnKey(keyChar: '\0', scancode: 0, down: true, mods: mods);

        public float X(GraphNode node) => State.Graph.EditorData.NodeLayouts[node.Id].X;
    }

    private static Scene Create()
    {
        var registry = new GraphDomainRegistry();
        registry.RegisterDomain(new Domain(new NodeDefinition {
            Id = "n", DisplayName = "Node",
            Inputs = [new PinDefinition { Id = "in" }],
            Outputs = [new PinDefinition { Id = "out" }],
        }));
        var graph = new GraphDocument { DomainId = "test" };
        GraphNode Place(float x, float y)
        {
            var node = new GraphNode { DefinitionId = "n" };
            graph.Nodes.Add(node);
            graph.EditorData.NodeLayouts[node.Id] = new NodeLayoutData { X = x, Y = y, Width = 230f };
            return node;
        }

        var a = Place(40, 40);
        var b = Place(40, 200);
        var c = Place(400, 40);
        var state = new GraphEditorState(graph, registry);
        var canvas = new GraphCanvas(state, ThemeData.Dark);
        canvas.Measure(Constraints.Tight(1000, 700));
        canvas.Layout(new Offset(0, 0));
        return new Scene(state, canvas, a, b, c);
    }

    [Fact]
    public void SelectionHasAStablePrimaryTogglesAndForgetsDeletedNodes()
    {
        var s = Create();
        int events = 0;
        s.State.SelectionChanged += () => events++;
        s.State.SetSelection([s.A.Id, s.B.Id, s.C.Id]);
        Assert.Equal(s.C.Id, s.State.PrimarySelection);
        s.State.ToggleSelection(s.C.Id);
        Assert.Equal(s.B.Id, s.State.PrimarySelection);
        s.State.AddToSelection(s.A.Id); // re-adding makes it the primary, not a duplicate
        Assert.Equal(s.A.Id, s.State.PrimarySelection);
        Assert.Equal(2, s.State.SelectedNodes.Count);

        events = 0;
        s.State.SetSelection([s.B.Id, s.A.Id]);
        Assert.Equal(0, events); // unchanged selection is silent: a marquee sets it every move

        s.State.Commands.Execute(new DeleteNodeCommand(s.A.Id));
        Assert.Equal([s.B.Id], s.State.SelectedNodes);
        Assert.Equal(s.B.Id, s.State.PrimarySelection);
        s.State.Commands.Undo(); // the node returns; the selection does not resurrect a stale id
        Assert.Equal([s.B.Id], s.State.SelectedNodes);
    }

    [Fact]
    public void ModifierClickTogglesAndPlainClickNarrowsAGroupOnlyWithoutADrag()
    {
        var s = Create();
        s.Click(150, 55); // A's header
        s.Hold(Modifiers.Shift);
        s.Click(150, 215); // B
        Assert.Equal(2, s.State.SelectedNodes.Count);
        s.Click(150, 215); // again: off
        Assert.Equal([s.A.Id], s.State.SelectedNodes);
        s.Click(150, 215);
        s.Hold(Modifiers.None);

        s.Drag(150, 55, 180, 75); // drag one member: the group moves and stays selected
        Assert.Equal(2, s.State.SelectedNodes.Count);
        Assert.Equal(70f, s.X(s.A));
        Assert.Equal(70f, s.X(s.B));

        s.Click(180, 75); // plain click on a member: now just that one
        Assert.Equal([s.A.Id], s.State.SelectedNodes);
    }

    [Fact]
    public void MarqueeTakesTheWholeCardAndExtendsWithShift()
    {
        var s = Create();
        // Crosses only the lower body of A (below its 30px header), which the header-only test missed.
        s.Drag(10, 150, 100, 110);
        Assert.Equal([s.A.Id], s.State.SelectedNodes);

        s.Hold(Modifiers.Shift);
        s.Drag(700, 400, 500, 100); // C, added to A
        Assert.Equal(2, s.State.SelectedNodes.Count);
        Assert.True(s.State.IsSelected(s.A.Id) && s.State.IsSelected(s.C.Id));
        s.Hold(Modifiers.None);

        s.Click(900, 600); // empty canvas
        Assert.Empty(s.State.SelectedNodes);
    }

    [Fact]
    public void KeyboardNudgesDuplicatesWithCablesAndDeletesAsOneStepEach()
    {
        var s = Create();
        s.State.Commands.Execute(new AddEdgeCommand(new GraphEdge { From = new(s.A.Id, "out"), To = new(s.C.Id, "in") }));
        s.State.SetSelection([s.A.Id, s.C.Id]);

        s.Canvas.OnKey(keyChar: '\0', scancode: 79, down: true, mods: Modifiers.None); // Right
        Assert.Equal(64f, s.X(s.A));
        Assert.Equal(424f, s.X(s.C));
        s.State.Commands.Undo();
        Assert.Equal(40f, s.X(s.A));
        Assert.Equal(400f, s.X(s.C));

        s.Canvas.OnKey(keyChar: 'd', scancode: 7, down: true, mods: Modifiers.Ctrl);
        Assert.Equal(5, s.State.Graph.Nodes.Count);
        Assert.Equal(2, s.State.Graph.Edges.Count); // the cable between the pair came along
        Assert.Equal(2, s.State.SelectedNodes.Count);
        Assert.False(s.State.IsSelected(s.A.Id)); // the copies are selected, ready to be moved
        s.State.Commands.Undo();
        Assert.Equal(3, s.State.Graph.Nodes.Count);
        Assert.Single(s.State.Graph.Edges);
        Assert.Empty(s.State.SelectedNodes); // nothing left pointing at the removed copies

        s.State.SetSelection([s.A.Id, s.B.Id]);
        s.Canvas.OnKey(keyChar: '\0', scancode: 76, down: true, mods: Modifiers.None); // Delete
        Assert.Single(s.State.Graph.Nodes);
        s.State.Commands.Undo();
        Assert.Equal(3, s.State.Graph.Nodes.Count);

        s.State.Select(s.B.Id);
        s.Canvas.OnKey(keyChar: '\0', scancode: 41, down: true, mods: Modifiers.None); // Escape
        Assert.Empty(s.State.SelectedNodes);
    }

    [Fact]
    public void PullingACableByItsInputEndReplugsOrUnplugsIt()
    {
        var s = Create();
        s.State.Commands.Execute(new AddEdgeCommand(new GraphEdge { From = new(s.A.Id, "out"), To = new(s.C.Id, "in") }));

        // C's input is at (400, 109); B's input at (40, 269).
        s.Drag(400, 109, 40, 269);
        var moved = Assert.Single(s.State.Graph.Edges);
        Assert.Equal(new GraphPinEndpoint(s.A.Id, "out"), moved.From);
        Assert.Equal(new GraphPinEndpoint(s.B.Id, "in"), moved.To);
        s.State.Commands.Undo(); // one step puts it back where it was
        Assert.Equal(new GraphPinEndpoint(s.C.Id, "in"), Assert.Single(s.State.Graph.Edges).To);

        s.Drag(400, 109, 402, 111); // picked up and dropped straight back
        Assert.Equal(new GraphPinEndpoint(s.C.Id, "in"), Assert.Single(s.State.Graph.Edges).To);

        bool searched = false;
        s.Canvas.SearchRequested += (_, _) => searched = true;
        s.Drag(400, 109, 700, 500); // dropped on empty canvas: unplugged, no node picker
        Assert.Empty(s.State.Graph.Edges);
        Assert.False(searched);
    }

    [Fact]
    public void AltClickCutsACable()
    {
        var s = Create();
        s.State.Commands.Execute(new AddEdgeCommand(new GraphEdge { From = new(s.A.Id, "out"), To = new(s.C.Id, "in") }));
        // A's output (270, 85) to C's input (400, 109): the curve passes through its midpoint.
        s.Click(335, 97);
        Assert.NotNull(s.State.SelectedEdgeId);
        s.Hold(Modifiers.Alt);
        s.Click(335, 97);
        Assert.Empty(s.State.Graph.Edges);
        Assert.Null(s.State.SelectedEdgeId);
    }

    private static void Wire(Scene s, GraphNode from, GraphNode to) => s.State.Commands.Execute(
        new AddEdgeCommand(new GraphEdge { From = new(from.Id, "out"), To = new(to.Id, "in") }));

    private static void Key(Scene s, char ch, Modifiers mods = Modifiers.Ctrl) =>
        s.Canvas.OnKey(keyChar: ch, scancode: 0, down: true, mods: mods);

    [Fact]
    public void CopyCutAndPasteCarryNodesAndTheCablesBetweenThem()
    {
        var s = Create();
        Wire(s, s.A, s.C);
        s.State.SetSelection([s.A.Id, s.C.Id]);
        Key(s, 'c');
        Key(s, 'v'); // the pointer rests at the canvas origin, so the group's corner lands there
        Assert.Equal(5, s.State.Graph.Nodes.Count);
        Assert.Equal(2, s.State.Graph.Edges.Count);
        Assert.Equal(2, s.State.SelectedNodes.Count);
        Assert.False(s.State.IsSelected(s.A.Id));
        Assert.Equal(0f, s.State.SelectedNodes.Min(id => s.State.Graph.EditorData.NodeLayouts[id].X));
        Assert.Equal(230f, s.State.Graph.EditorData.NodeLayouts[s.State.PrimarySelection!.Value].Width);
        s.State.Commands.Undo(); // one step removes the whole paste
        Assert.Equal(3, s.State.Graph.Nodes.Count);
        Assert.Single(s.State.Graph.Edges);

        s.State.SetSelection([s.A.Id, s.C.Id]);
        Key(s, 'x');
        Assert.Equal([s.B.Id], s.State.Graph.Nodes.Select(n => n.Id));
        Assert.Empty(s.State.Graph.Edges);
        Key(s, 'v');
        Assert.Equal(3, s.State.Graph.Nodes.Count);
        Assert.Single(s.State.Graph.Edges);
    }

    [Fact]
    public void SeveralCablesSelectTogetherAndDisconnectAsOneStep()
    {
        var s = Create();
        Wire(s, s.A, s.C); // passes through (335, 97)
        Wire(s, s.B, s.A); // loops back through (155, 177)
        s.Click(335, 97);
        s.Hold(Modifiers.Shift);
        s.Click(155, 177);
        Assert.Equal(2, s.State.SelectedEdges.Count);
        s.Click(155, 177); // again: off
        Assert.Single(s.State.SelectedEdges);
        s.Click(155, 177);
        s.Hold(Modifiers.None);
        Assert.Empty(s.State.SelectedNodes);

        s.Canvas.OnKey(keyChar: '\0', scancode: 76, down: true, mods: Modifiers.None); // Delete
        Assert.Empty(s.State.Graph.Edges);
        Assert.Empty(s.State.SelectedEdges);
        s.State.Commands.Undo();
        Assert.Equal(2, s.State.Graph.Edges.Count);

        s.Drag(300, 70, 360, 130); // a marquee over bare canvas that crosses one cable
        Assert.Equal(s.State.Graph.Edges.Single(e => e.To.NodeId == s.C.Id).Id, Assert.Single(s.State.SelectedEdges));
        s.State.Select(s.B.Id); // nodes and cables are never selected together
        Assert.Empty(s.State.SelectedEdges);
    }

    [Fact]
    public void DroppingAnUnconnectedNodeOnACableSplicesItIn()
    {
        var s = Create();
        Wire(s, s.A, s.C);
        s.Drag(150, 215, 335, 97); // B, by its header, onto the cable
        Assert.Equal(2, s.State.Graph.Edges.Count);
        Assert.Contains(s.State.Graph.Edges, e => e.From.NodeId == s.A.Id && e.To.NodeId == s.B.Id);
        Assert.Contains(s.State.Graph.Edges, e => e.From.NodeId == s.B.Id && e.To.NodeId == s.C.Id);

        s.State.Commands.Undo(); // the move and the re-wiring undo together
        var edge = Assert.Single(s.State.Graph.Edges);
        Assert.Equal(s.C.Id, edge.To.NodeId);
        Assert.Equal(40f, s.X(s.B));

        s.Drag(150, 215, 335, 400); // dropped clear of any cable: only a move
        Assert.Single(s.State.Graph.Edges);

        // A node that already has cables is never spliced: dragging C across A→C leaves the graph alone.
        s.Drag(500, 55, 335, 97);
        Assert.Equal(s.C.Id, Assert.Single(s.State.Graph.Edges).To.NodeId);
    }

    [Fact]
    public void WheelZoomsTrackpadPansAndCtrlScrollZoomsEither()
    {
        var s = Create();
        var view = s.State.Graph.EditorData;
        s.Canvas.OnScroll(dx: 0f, dy: 1f); // a wheel notch
        Assert.True(view.Zoom > 1f);
        float zoom = view.Zoom, x = view.ViewOffsetX, y = view.ViewOffsetY;

        s.Canvas.OnScroll(dx: 1f, dy: 0f); // a mouse's tilt wheel is still a wheel
        Assert.True(view.Zoom == zoom && view.ViewOffsetX == x);

        s.Canvas.PreciseScroll = true; // what the native layer reports for a trackpad
        s.Canvas.OnScroll(dx: 0.5f, dy: 0.25f);
        Assert.Equal(zoom, view.Zoom);
        Assert.Equal(x - 20f, view.ViewOffsetX, precision: 3);
        Assert.Equal(y + 10f, view.ViewOffsetY, precision: 3);

        s.Canvas.PreciseScroll = false;
        s.Canvas.OnScroll(dx: 0f, dy: 1f); // an unflagged step in the middle of the same swipe still pans
        Assert.Equal(zoom, view.Zoom);

        s.Hold(Modifiers.Ctrl);
        s.Canvas.OnScroll(dx: 0f, dy: 0.5f);
        Assert.True(view.Zoom > zoom);
    }

    private static IEnumerable<string> Texts(Widget root)
    {
        if (root is Label label) yield return label.Text;
        foreach (var child in root.GetChildren())
        foreach (string text in Texts(child))
            yield return text;
    }

    [Fact]
    public void InspectorFollowsWhatIsSelected()
    {
        var s = Create();
        s.State.Commands.Execute(new AddEdgeCommand(new GraphEdge { From = new(s.A.Id, "out"), To = new(s.C.Id, "in") }));
        var inspector = new GraphInspectorPanel(s.State, ThemeData.Dark);
        void Relayout()
        {
            inspector.Measure(Constraints.Tight(220, 600));
            inspector.Layout(new Offset(0, 0));
        }

        Relayout();
        Assert.Contains("3 nodes · 1 cables", Texts(inspector));

        s.State.Select(s.A.Id);
        Relayout();
        Assert.Contains("Node", Texts(inspector));

        s.State.SetSelection([s.A.Id, s.B.Id]);
        Relayout();
        Assert.Contains("2 nodes", Texts(inspector));
        Assert.Contains("Node  ×2", Texts(inspector));

        s.State.SelectEdge(s.State.Graph.Edges[0].Id);
        Relayout();
        Assert.Contains("Cable", Texts(inspector));
        Assert.Contains("Node · in", Texts(inspector));

        s.State.Commands.Execute(new DeleteEdgeCommand(s.State.Graph.Edges[0].Id));
        Relayout();
        Assert.Contains("3 nodes · 0 cables", Texts(inspector)); // the cable is gone: back to the graph
    }
}
