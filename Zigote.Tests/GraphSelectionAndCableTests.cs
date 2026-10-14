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

    private sealed record Scene(
        GraphEditorState State,
        GraphCanvas Canvas,
        GraphNode A,
        GraphNode B,
        GraphNode C)
    {
        public void Click(float x, float y)
        {
            Canvas.OnPointerDown(new Offset(x: x, y: y));
            Canvas.OnPointerUp(new Offset(x: x, y: y));
        }

        public void Drag(float x0, float y0, float x1, float y1)
        {
            Canvas.OnPointerDown(new Offset(x: x0, y: y0));
            Canvas.OnPointerMove(new Offset(x: x1, y: y1));
            Canvas.OnPointerUp(new Offset(x: x1, y: y1));
        }

        // No App owns the canvas here, so modifiers arrive the way they do in the app: on a key event.
        public void Hold(Modifiers mods) => Canvas.OnKey(
            keyChar: '\0',
            scancode: 0,
            down: true,
            mods: mods
        );

        public float X(GraphNode node) => State.Graph.EditorData.NodeLayouts[node.Id].X;
    }

    private static Scene Create()
    {
        var registry = new GraphDomainRegistry();
        registry.RegisterDomain(
            new Domain(
                new NodeDefinition {
                    Id = "n",
                    DisplayName = "Node",
                    Inputs = [new PinDefinition { Id = "in" }],
                    Outputs = [new PinDefinition { Id = "out" }],
                }
            )
        );
        var graph = new GraphDocument { DomainId = "test" };

        GraphNode Place(float x, float y)
        {
            var node = new GraphNode { DefinitionId = "n" };
            graph.Nodes.Add(node);
            graph.EditorData.NodeLayouts[node.Id] = new NodeLayoutData {
                X = x,
                Y = y,
                Width = 230f,
            };
            return node;
        }

        var a = Place(x: 40, y: 40);
        var b = Place(x: 40, y: 200);
        var c = Place(x: 400, y: 40);
        var state = new GraphEditorState(graph: graph, registry: registry);
        var canvas = new GraphCanvas(state: state, theme: ThemeData.Dark);
        canvas.Measure(Constraints.Tight(width: 1000, height: 700));
        canvas.Layout(new Offset(x: 0, y: 0));
        return new Scene(
            State: state,
            Canvas: canvas,
            A: a,
            B: b,
            C: c
        );
    }

    [Fact]
    public void SelectionHasAStablePrimaryTogglesAndForgetsDeletedNodes()
    {
        var s = Create();
        int events = 0;
        s.State.SelectionChanged += () => events++;
        s.State.SetSelection([s.A.Id, s.B.Id, s.C.Id]);
        Assert.Equal(expected: s.C.Id, actual: s.State.PrimarySelection);
        s.State.ToggleSelection(s.C.Id);
        Assert.Equal(expected: s.B.Id, actual: s.State.PrimarySelection);
        s.State.AddToSelection(s.A.Id); // re-adding makes it the primary, not a duplicate
        Assert.Equal(expected: s.A.Id, actual: s.State.PrimarySelection);
        Assert.Equal(expected: 2, actual: s.State.SelectedNodes.Count);

        events = 0;
        s.State.SetSelection([s.B.Id, s.A.Id]);
        Assert.Equal(
            expected: 0,
            actual: events
        ); // unchanged selection is silent: a marquee sets it every move

        s.State.Commands.Execute(new DeleteNodeCommand(s.A.Id));
        Assert.Equal(expected: [s.B.Id], actual: s.State.SelectedNodes);
        Assert.Equal(expected: s.B.Id, actual: s.State.PrimarySelection);
        s.State.Commands.Undo(); // the node returns; the selection does not resurrect a stale id
        Assert.Equal(expected: [s.B.Id], actual: s.State.SelectedNodes);
    }

    [Fact]
    public void ModifierClickTogglesAndPlainClickNarrowsAGroupOnlyWithoutADrag()
    {
        var s = Create();
        s.Click(x: 150, y: 55); // A's header
        s.Hold(Modifiers.Shift);
        s.Click(x: 150, y: 215); // B
        Assert.Equal(expected: 2, actual: s.State.SelectedNodes.Count);
        s.Click(x: 150, y: 215); // again: off
        Assert.Equal(expected: [s.A.Id], actual: s.State.SelectedNodes);
        s.Click(x: 150, y: 215);
        s.Hold(Modifiers.None);

        s.Drag(
            x0: 150,
            y0: 55,
            x1: 180,
            y1: 75
        ); // drag one member: the group moves and stays selected
        Assert.Equal(expected: 2, actual: s.State.SelectedNodes.Count);
        Assert.Equal(expected: 70f, actual: s.X(s.A));
        Assert.Equal(expected: 70f, actual: s.X(s.B));

        s.Click(x: 180, y: 75); // plain click on a member: now just that one
        Assert.Equal(expected: [s.A.Id], actual: s.State.SelectedNodes);
    }

    [Fact]
    public void MarqueeTakesTheWholeCardAndExtendsWithShift()
    {
        var s = Create();
        // Crosses only the lower body of A (below its 30px header), which the header-only test missed.
        s.Drag(
            x0: 10,
            y0: 150,
            x1: 100,
            y1: 110
        );
        Assert.Equal(expected: [s.A.Id], actual: s.State.SelectedNodes);

        s.Hold(Modifiers.Shift);
        s.Drag(
            x0: 700,
            y0: 400,
            x1: 500,
            y1: 100
        ); // C, added to A
        Assert.Equal(expected: 2, actual: s.State.SelectedNodes.Count);
        Assert.True(s.State.IsSelected(s.A.Id) && s.State.IsSelected(s.C.Id));
        s.Hold(Modifiers.None);

        s.Click(x: 900, y: 600); // empty canvas
        Assert.Empty(s.State.SelectedNodes);
    }

    [Fact]
    public void KeyboardNudgesDuplicatesWithCablesAndDeletesAsOneStepEach()
    {
        var s = Create();
        s.State.Commands.Execute(
            new AddEdgeCommand(
                new GraphEdge {
                    From = new GraphPinEndpoint(NodeId: s.A.Id, PinId: "out"),
                    To = new GraphPinEndpoint(NodeId: s.C.Id, PinId: "in"),
                }
            )
        );
        s.State.SetSelection([s.A.Id, s.C.Id]);

        s.Canvas.OnKey(
            keyChar: '\0',
            scancode: 79,
            down: true,
            mods: Modifiers.None
        ); // Right
        Assert.Equal(expected: 64f, actual: s.X(s.A));
        Assert.Equal(expected: 424f, actual: s.X(s.C));
        s.State.Commands.Undo();
        Assert.Equal(expected: 40f, actual: s.X(s.A));
        Assert.Equal(expected: 400f, actual: s.X(s.C));

        s.Canvas.OnKey(
            keyChar: 'd',
            scancode: 7,
            down: true,
            mods: Modifiers.Ctrl
        );
        Assert.Equal(expected: 5, actual: s.State.Graph.Nodes.Count);
        Assert.Equal(
            expected: 2,
            actual: s.State.Graph.Edges.Count
        ); // the cable between the pair came along
        Assert.Equal(expected: 2, actual: s.State.SelectedNodes.Count);
        Assert.False(s.State.IsSelected(s.A.Id)); // the copies are selected, ready to be moved
        s.State.Commands.Undo();
        Assert.Equal(expected: 3, actual: s.State.Graph.Nodes.Count);
        Assert.Single(s.State.Graph.Edges);
        Assert.Empty(s.State.SelectedNodes); // nothing left pointing at the removed copies

        s.State.SetSelection([s.A.Id, s.B.Id]);
        s.Canvas.OnKey(
            keyChar: '\0',
            scancode: 76,
            down: true,
            mods: Modifiers.None
        ); // Delete
        Assert.Single(s.State.Graph.Nodes);
        s.State.Commands.Undo();
        Assert.Equal(expected: 3, actual: s.State.Graph.Nodes.Count);

        s.State.Select(s.B.Id);
        s.Canvas.OnKey(
            keyChar: '\0',
            scancode: 41,
            down: true,
            mods: Modifiers.None
        ); // Escape
        Assert.Empty(s.State.SelectedNodes);
    }

    [Fact]
    public void PullingACableByItsInputEndReplugsOrUnplugsIt()
    {
        var s = Create();
        s.State.Commands.Execute(
            new AddEdgeCommand(
                new GraphEdge {
                    From = new GraphPinEndpoint(NodeId: s.A.Id, PinId: "out"),
                    To = new GraphPinEndpoint(NodeId: s.C.Id, PinId: "in"),
                }
            )
        );

        // C's input is at (400, 109); B's input at (40, 269).
        s.Drag(
            x0: 400,
            y0: 109,
            x1: 40,
            y1: 269
        );
        var moved = Assert.Single(s.State.Graph.Edges);
        Assert.Equal(
            expected: new GraphPinEndpoint(NodeId: s.A.Id, PinId: "out"),
            actual: moved.From
        );
        Assert.Equal(expected: new GraphPinEndpoint(NodeId: s.B.Id, PinId: "in"), actual: moved.To);
        s.State.Commands.Undo(); // one step puts it back where it was
        Assert.Equal(
            expected: new GraphPinEndpoint(NodeId: s.C.Id, PinId: "in"),
            actual: Assert.Single(s.State.Graph.Edges).To
        );

        s.Drag(
            x0: 400,
            y0: 109,
            x1: 402,
            y1: 111
        ); // picked up and dropped straight back
        Assert.Equal(
            expected: new GraphPinEndpoint(NodeId: s.C.Id, PinId: "in"),
            actual: Assert.Single(s.State.Graph.Edges).To
        );

        bool searched = false;
        s.Canvas.SearchRequested += (_, _) => searched = true;
        s.Drag(
            x0: 400,
            y0: 109,
            x1: 700,
            y1: 500
        ); // dropped on empty canvas: unplugged, no node picker
        Assert.Empty(s.State.Graph.Edges);
        Assert.False(searched);
    }

    [Fact]
    public void AltClickCutsACable()
    {
        var s = Create();
        s.State.Commands.Execute(
            new AddEdgeCommand(
                new GraphEdge {
                    From = new GraphPinEndpoint(NodeId: s.A.Id, PinId: "out"),
                    To = new GraphPinEndpoint(NodeId: s.C.Id, PinId: "in"),
                }
            )
        );
        // A's output (270, 85) to C's input (400, 109): the curve passes through its midpoint.
        s.Click(x: 335, y: 97);
        Assert.NotNull(s.State.SelectedEdgeId);
        s.Hold(Modifiers.Alt);
        s.Click(x: 335, y: 97);
        Assert.Empty(s.State.Graph.Edges);
        Assert.Null(s.State.SelectedEdgeId);
    }

    private static void Wire(Scene s, GraphNode from, GraphNode to) => s.State.Commands.Execute(
        new AddEdgeCommand(
            new GraphEdge {
                From = new GraphPinEndpoint(NodeId: from.Id, PinId: "out"),
                To = new GraphPinEndpoint(NodeId: to.Id, PinId: "in"),
            }
        )
    );

    private static void Key(Scene s, char ch, Modifiers mods = Modifiers.Ctrl) =>
        s.Canvas.OnKey(
            keyChar: ch,
            scancode: 0,
            down: true,
            mods: mods
        );

    [Fact]
    public void CopyCutAndPasteCarryNodesAndTheCablesBetweenThem()
    {
        var s = Create();
        Wire(s: s, from: s.A, to: s.C);
        s.State.SetSelection([s.A.Id, s.C.Id]);
        Key(s: s, ch: 'c');
        Key(
            s: s,
            ch: 'v'
        ); // the pointer rests at the canvas origin, so the group's corner lands there
        Assert.Equal(expected: 5, actual: s.State.Graph.Nodes.Count);
        Assert.Equal(expected: 2, actual: s.State.Graph.Edges.Count);
        Assert.Equal(expected: 2, actual: s.State.SelectedNodes.Count);
        Assert.False(s.State.IsSelected(s.A.Id));
        Assert.Equal(
            expected: 0f,
            actual: s.State.SelectedNodes.Min(id => s.State.Graph.EditorData.NodeLayouts[id].X)
        );
        Assert.Equal(
            expected: 230f,
            actual: s.State.Graph.EditorData.NodeLayouts[s.State.PrimarySelection!.Value].Width
        );
        s.State.Commands.Undo(); // one step removes the whole paste
        Assert.Equal(expected: 3, actual: s.State.Graph.Nodes.Count);
        Assert.Single(s.State.Graph.Edges);

        s.State.SetSelection([s.A.Id, s.C.Id]);
        Key(s: s, ch: 'x');
        Assert.Equal(expected: [s.B.Id], actual: s.State.Graph.Nodes.Select(n => n.Id));
        Assert.Empty(s.State.Graph.Edges);
        Key(s: s, ch: 'v');
        Assert.Equal(expected: 3, actual: s.State.Graph.Nodes.Count);
        Assert.Single(s.State.Graph.Edges);
    }

    [Fact]
    public void SeveralCablesSelectTogetherAndDisconnectAsOneStep()
    {
        var s = Create();
        Wire(s: s, from: s.A, to: s.C); // passes through (335, 97)
        Wire(s: s, from: s.B, to: s.A); // loops back through (155, 177)
        s.Click(x: 335, y: 97);
        s.Hold(Modifiers.Shift);
        s.Click(x: 155, y: 177);
        Assert.Equal(expected: 2, actual: s.State.SelectedEdges.Count);
        s.Click(x: 155, y: 177); // again: off
        Assert.Single(s.State.SelectedEdges);
        s.Click(x: 155, y: 177);
        s.Hold(Modifiers.None);
        Assert.Empty(s.State.SelectedNodes);

        s.Canvas.OnKey(
            keyChar: '\0',
            scancode: 76,
            down: true,
            mods: Modifiers.None
        ); // Delete
        Assert.Empty(s.State.Graph.Edges);
        Assert.Empty(s.State.SelectedEdges);
        s.State.Commands.Undo();
        Assert.Equal(expected: 2, actual: s.State.Graph.Edges.Count);

        s.Drag(
            x0: 300,
            y0: 70,
            x1: 360,
            y1: 130
        ); // a marquee over bare canvas that crosses one cable
        Assert.Equal(
            expected: s.State.Graph.Edges.Single(e => e.To.NodeId == s.C.Id).Id,
            actual: Assert.Single(s.State.SelectedEdges)
        );
        s.State.Select(s.B.Id); // nodes and cables are never selected together
        Assert.Empty(s.State.SelectedEdges);
    }

    [Fact]
    public void DroppingAnUnconnectedNodeOnACableSplicesItIn()
    {
        var s = Create();
        Wire(s: s, from: s.A, to: s.C);
        s.Drag(
            x0: 150,
            y0: 215,
            x1: 335,
            y1: 97
        ); // B, by its header, onto the cable
        Assert.Equal(expected: 2, actual: s.State.Graph.Edges.Count);
        Assert.Contains(
            collection: s.State.Graph.Edges,
            filter: e => e.From.NodeId == s.A.Id && e.To.NodeId == s.B.Id
        );
        Assert.Contains(
            collection: s.State.Graph.Edges,
            filter: e => e.From.NodeId == s.B.Id && e.To.NodeId == s.C.Id
        );

        s.State.Commands.Undo(); // the move and the re-wiring undo together
        var edge = Assert.Single(s.State.Graph.Edges);
        Assert.Equal(expected: s.C.Id, actual: edge.To.NodeId);
        Assert.Equal(expected: 40f, actual: s.X(s.B));

        s.Drag(
            x0: 150,
            y0: 215,
            x1: 335,
            y1: 400
        ); // dropped clear of any cable: only a move
        Assert.Single(s.State.Graph.Edges);

        // A node that already has cables is never spliced: dragging C across A→C leaves the graph alone.
        s.Drag(
            x0: 500,
            y0: 55,
            x1: 335,
            y1: 97
        );
        Assert.Equal(expected: s.C.Id, actual: Assert.Single(s.State.Graph.Edges).To.NodeId);
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
        Assert.Equal(expected: zoom, actual: view.Zoom);
        Assert.Equal(expected: x - 20f, actual: view.ViewOffsetX, precision: 3);
        Assert.Equal(expected: y + 10f, actual: view.ViewOffsetY, precision: 3);

        s.Canvas.PreciseScroll = false;
        s.Canvas.OnScroll(
            dx: 0f,
            dy: 1f
        ); // an unflagged step in the middle of the same swipe still pans
        Assert.Equal(expected: zoom, actual: view.Zoom);

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
        s.State.Commands.Execute(
            new AddEdgeCommand(
                new GraphEdge {
                    From = new GraphPinEndpoint(NodeId: s.A.Id, PinId: "out"),
                    To = new GraphPinEndpoint(NodeId: s.C.Id, PinId: "in"),
                }
            )
        );
        var inspector = new GraphInspectorPanel(state: s.State, theme: ThemeData.Dark);

        void Relayout()
        {
            inspector.Measure(Constraints.Tight(width: 220, height: 600));
            inspector.Layout(new Offset(x: 0, y: 0));
        }

        Relayout();
        Assert.Contains(expected: "3 nodes · 1 cables", collection: Texts(inspector));

        s.State.Select(s.A.Id);
        Relayout();
        Assert.Contains(expected: "Node", collection: Texts(inspector));

        s.State.SetSelection([s.A.Id, s.B.Id]);
        Relayout();
        Assert.Contains(expected: "2 nodes", collection: Texts(inspector));
        Assert.Contains(expected: "Node  ×2", collection: Texts(inspector));

        s.State.SelectEdge(s.State.Graph.Edges[0].Id);
        Relayout();
        Assert.Contains(expected: "Cable", collection: Texts(inspector));
        Assert.Contains(expected: "Node · in", collection: Texts(inspector));

        s.State.Commands.Execute(new DeleteEdgeCommand(s.State.Graph.Edges[0].Id));
        Relayout();
        Assert.Contains(
            expected: "3 nodes · 0 cables",
            collection: Texts(inspector)
        ); // the cable is gone: back to the graph
    }
}
