using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;
using Zigote.Graphs.Editor;

namespace AdwaitaGallery.Pages;

/// <summary>A self-contained, typed graph domain: numbers and text become a live output card.</summary>
internal sealed class GraphsDomain(ThemeData theme) : IGraphDomain
{
    public const string DomainId = "gallery.graphs";
    public string Id => DomainId;
    public string DisplayName => "Graph playground";
    public IReadOnlyList<string> SupportedSchemas => [DomainId];
    public IReadOnlyList<GraphTypeDefinition> GetTypeDefinitions() => [];

    private static PinDefinition Input(string id, string name, GraphTypeRef type, GraphValue? fallback = null) =>
        new() { Id = id, DisplayName = name, Type = type, Direction = PinDirection.Input,
            IsRequired = fallback is null, DefaultValue = fallback };
    private static PinDefinition Output(GraphTypeRef type) =>
        new() { Id = "out", DisplayName = "Result", Type = type, Direction = PinDirection.Output, AllowsMultipleConnections = true };
    private static PropertyDefinition Number(string id, string name, float value, float min = 0, float max = 1) =>
        new() { Id = id, DisplayName = name, Type = GraphTypeRef.Float, DefaultValue = GraphValue.FromFloat(value), Min = min, Max = max, Step = 0.01f };
    private static NodeDefinition Node(string id, string name, string category, string description,
        PinDefinition[] inputs, PinDefinition[] outputs, params PropertyDefinition[] properties) =>
        new() { Id = id, DomainId = DomainId, SchemaId = DomainId, DisplayName = name, Category = category,
            Description = description, Inputs = inputs, Outputs = outputs, Properties = properties,
            Tags = [name, description], Capabilities = NodeCapabilities.Collapsible };

    public IReadOnlyList<NodeDefinition> GetNodeDefinitions() => [
        Node("demo.number", "Number", "Sources", "A numeric source. Drag the value or click to type.", [], [Output(GraphTypeRef.Float)],
            Number("value", "Value", 0.5f)),
        Node("demo.math", "Math", "Math", "Combine two values; B has a default when disconnected.",
            [Input("a", "A", GraphTypeRef.Float), Input("b", "B (default 0.25)", GraphTypeRef.Float, GraphValue.FromFloat(0.25f))],
            [Output(GraphTypeRef.Float)],
            new PropertyDefinition { Id = "operation", DisplayName = "Operation", Type = GraphTypeRef.Int,
                DefaultValue = GraphValue.FromInt(0), EnumLabels = ["Add", "Multiply", "Maximum"] },
            Number("scale", "Scale", 1, 0, 2)),
        Node("demo.text", "Text", "Sources", "A string source. Connect the yellow socket to Caption.", [], [Output(GraphTypeRef.String)],
            new PropertyDefinition { Id = "text", DisplayName = "Text", Type = GraphTypeRef.String, DefaultValue = GraphValue.FromString("Hello, graphs!") }),
        Node("demo.toggle", "Switch", "Sources", "A boolean source. Click Enabled to toggle the preview.", [], [Output(GraphTypeRef.Bool)],
            new PropertyDefinition { Id = "enabled", DisplayName = "Enabled", Type = GraphTypeRef.Bool, DefaultValue = GraphValue.FromBool(true) }),
        Node("demo.output", "Preview", "Output", "A live widget compiled from the connected graph.",
            [Input("value", "Value", GraphTypeRef.Float),
             Input("caption", "Caption", GraphTypeRef.String, GraphValue.FromString("Live preview")),
             Input("enabled", "Enabled", GraphTypeRef.Bool, GraphValue.FromBool(true))], [],
            new PropertyDefinition { Id = "digits", DisplayName = "Decimals", Type = GraphTypeRef.Int, DefaultValue = GraphValue.FromInt(2), Min = 0, Max = 4, Step = 1 }),
    ];

    public bool CanCreateEdge(GraphDocument graph, GraphPinEndpoint from, GraphPinEndpoint to, out string? reason)
    {
        var pending = new Stack<Guid>();
        var seen = new HashSet<Guid>();
        pending.Push(to.NodeId);
        while (pending.TryPop(out var current))
        {
            if (current == from.NodeId) { reason = "This connection would create a feedback loop."; return false; }
            if (!seen.Add(current)) continue;
            foreach (var edge in graph.Edges.Where(e => e.From.NodeId == current)) pending.Push(edge.To.NodeId);
        }
        reason = null;
        return true;
    }

    public GraphValidationResult Validate(GraphDocument graph)
    {
        var registry = Registry();
        var errors = GraphInputValidation.Validate(graph, registry).ToList();
        if (graph.Nodes.Count(n => n.DefinitionId == "demo.output") != 1)
            errors.Add(Error("Keep exactly one Preview node."));
        foreach (var node in graph.Nodes.Where(n => registry.GetNodeDefinition(n.DefinitionId) is null))
            errors.Add(Error("Unknown node type.", node.Id));
        foreach (var edge in graph.Edges)
            if (!GraphConnections.CanConnect(graph, registry, edge.From, edge.To, out var reason, edge.Id))
                errors.Add(Error(reason, edge.To.NodeId, edge.To.PinId));
        return new GraphValidationResult { IsValid = errors.Count == 0, Diagnostics = errors };
    }
    private static GraphDiagnostic Error(string message, Guid? node = null, string? pin = null) =>
        new() { Severity = GraphDiagnosticSeverity.Error, Code = "demo.connection", Message = message, NodeId = node, PinId = pin };
    public GraphDomainRegistry Registry()
    {
        var registry = new GraphDomainRegistry();
        registry.RegisterDomain(this);
        return registry;
    }

    internal sealed record Result(float Value, string Caption, bool Enabled, int Digits);

    // Evaluate only after validation: the graph is acyclic, endpoints exist and types agree.
    internal Result Evaluate(GraphDocument graph)
    {
        var definitions = GetNodeDefinitions().ToDictionary(d => d.Id);
        var values = new Dictionary<Guid, GraphValue>();
        GraphValue Property(GraphNode n, string id) => n.Properties.TryGetValue(id, out var v) && !v.IsNull
            ? v : definitions[n.DefinitionId].Properties.First(p => p.Id == id).DefaultValue;
        GraphValue Read(GraphNode node, string pin)
        {
            var edge = graph.Edges.FirstOrDefault(e => e.To == new GraphPinEndpoint(node.Id, pin));
            return edge is null ? definitions[node.DefinitionId].Inputs.First(p => p.Id == pin).DefaultValue!
                : Value(graph.FindNode(edge.From.NodeId)!);
        }
        GraphValue Value(GraphNode node)
        {
            if (values.TryGetValue(node.Id, out var cached)) return cached;
            GraphValue result;
            switch (node.DefinitionId)
            {
                case "demo.number": result = Property(node, "value"); break;
                case "demo.text": result = Property(node, "text"); break;
                case "demo.toggle": result = Property(node, "enabled"); break;
                case "demo.math":
                    float a = Read(node, "a").AsFloat(), b = Read(node, "b").AsFloat();
                    float v = Property(node, "operation").AsInt() switch { 1 => a * b, 2 => MathF.Max(a, b), _ => a + b };
                    result = GraphValue.FromFloat(v * Property(node, "scale").AsFloat()); break;
                default: throw new InvalidOperationException("Unknown demo source");
            }
            values[node.Id] = result;
            return result;
        }
        var output = graph.Nodes.Single(n => n.DefinitionId == "demo.output");
        return new Result(Read(output, "value").AsFloat(), Read(output, "caption").AsString(),
            Read(output, "enabled").AsBool(), Math.Clamp(Property(output, "digits").AsInt(), 0, 4));
    }

    public GraphCompileResult Compile(GraphDocument graph, GraphCompileContext context)
    {
        var validation = Validate(graph);
        if (!validation.IsValid) return new GraphCompileResult { Diagnostics = validation.Diagnostics };
        var result = Evaluate(graph);
        return new GraphCompileResult { Success = true, CompiledArtifact = new PreviewCard(result, theme) };
    }

    // The editor measures previews in screen space, including at overview zoom.
    private sealed class PreviewCard(Result result, ThemeData theme) : Widget
    {
        private Size _size;
        public override Size Measure(Constraints c) => _size = c.Constrain(new Size(214f, 160f));
        public override void Layout(Offset origin) => Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
        public override void Paint(PaintList paint)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            float scale = MathF.Min(Bounds.Width / 214f, Bounds.Height / 160f);
            paint.AddClipStart(Bounds);
            paint.AddRect(Bounds, theme.Primary.WithAlpha(result.Enabled ? 0.18f : 0.05f));
            void Line(string text, float fraction, float fontSize, Color color)
            {
                float size = fontSize * scale;
                int max = Math.Max(1, (int)((Bounds.Width - 12f * scale) / (size * 0.6f)));
                if (text.Length > max) text = text[..Math.Max(0, max - 1)] + "…";
                float width = text.Length * size * 0.55f;
                paint.AddText(text, Bounds.X + (Bounds.Width - width) / 2,
                    Bounds.Y + Bounds.Height * fraction, color, size);
            }
            Line(result.Caption, 0.30f, 13f, theme.OnSurface);
            Line(result.Enabled ? result.Value.ToString($"F{result.Digits}", System.Globalization.CultureInfo.InvariantCulture) : "Disabled",
                0.57f, 28f, result.Enabled ? theme.Primary : theme.TextMuted);
            Line("Updates as you edit", 0.80f, 10f, theme.TextMuted);
            paint.AddClipEnd();
        }
    }

    public static GraphDocument Sample(int sample)
    {
        var graph = new GraphDocument { Name = sample switch { 1 => "Repair the wiring", 2 => "Explore a larger graph", _ => "Live playground" }, DomainId = DomainId, SchemaId = DomainId };
        GraphNode Add(string id, float x, float y, bool collapsed = false)
        {
            var node = new GraphNode { DefinitionId = id };
            graph.Nodes.Add(node);
            graph.EditorData.NodeLayouts[node.Id] = new NodeLayoutData { X = x, Y = y, Width = 230, IsCollapsed = collapsed };
            return node;
        }
        void Wire(GraphNode a, GraphNode b, string pin) => graph.Edges.Add(new GraphEdge { From = new(a.Id, "out"), To = new(b.Id, pin) });
        var number = Add("demo.number", 30, 30);
        var math = Add("demo.math", 330, 30);
        var text = Add("demo.text", 30, 210);
        var toggle = Add("demo.toggle", 330, 290, collapsed: true);
        var output = Add("demo.output", 660, 30);
        Wire(number, math, "a");
        Wire(math, output, "value");
        Wire(text, output, "caption");
        Wire(toggle, output, "enabled");
        if (sample == 1)
        {
            // Broken on purpose: a string wired into a number input, plus a missing required A.
            graph.Edges.RemoveAt(0);
            graph.Edges.RemoveAt(0);
            Wire(text, output, "value");
        }
        if (sample == 2)
        {
            graph.Edges.RemoveAll(e => e.To == new GraphPinEndpoint(output.Id, "value"));
            var last = math;
            for (int i = 0; i < 24; i++)
            {
                var next = Add("demo.math", 330 + (i % 6) * 290, 450 + (i / 6) * 210, collapsed: i % 3 == 0);
                Wire(last, next, "a");
                last = next;
            }
            graph.EditorData.NodeLayouts[output.Id].X = 2110;
            graph.EditorData.NodeLayouts[output.Id].Y = 660;
            Wire(last, output, "value");
        }
        return graph;
    }
}
