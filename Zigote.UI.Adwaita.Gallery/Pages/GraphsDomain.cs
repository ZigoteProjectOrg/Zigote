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

    private static PinDefinition Input(string id, string name, GraphTypeRef type,
        GraphValue? fallback = null) =>
        new() {
            Id = id,
            DisplayName = name,
            Type = type,
            Direction = PinDirection.Input,
            IsRequired = fallback is null,
            DefaultValue = fallback,
        };

    private static PinDefinition Output(GraphTypeRef type) =>
        new() {
            Id = "out",
            DisplayName = "Result",
            Type = type,
            Direction = PinDirection.Output,
            AllowsMultipleConnections = true,
        };

    private static PropertyDefinition Number(string id, string name, float value, float min = 0,
        float max = 1) =>
        new() {
            Id = id,
            DisplayName = name,
            Type = GraphTypeRef.Float,
            DefaultValue = GraphValue.FromFloat(value),
            Min = min,
            Max = max,
            Step = 0.01f,
        };

    private static NodeDefinition Node(string id, string name, string category, string description,
        PinDefinition[] inputs, PinDefinition[] outputs, params PropertyDefinition[] properties) =>
        new() {
            Id = id,
            DomainId = DomainId,
            SchemaId = DomainId,
            DisplayName = name,
            Category = category,
            Description = description,
            Inputs = inputs,
            Outputs = outputs,
            Properties = properties,
            Tags = [name, description],
            Capabilities = NodeCapabilities.Collapsible,
        };

    public IReadOnlyList<NodeDefinition> GetNodeDefinitions() => [
        Node(
            id: "demo.number",
            name: "Number",
            category: "Sources",
            description: "A numeric source. Drag the value or click to type.",
            inputs: [],
            outputs: [Output(GraphTypeRef.Float)],
            Number(id: "value", name: "Value", value: 0.5f)
        ),
        Node(
            id: "demo.math",
            name: "Math",
            category: "Math",
            description: "Combine two values; B has a default when disconnected.",
            inputs: [
                Input(id: "a", name: "A", type: GraphTypeRef.Float),
                Input(
                    id: "b",
                    name: "B (default 0.25)",
                    type: GraphTypeRef.Float,
                    fallback: GraphValue.FromFloat(0.25f)
                ),
            ],
            outputs: [Output(GraphTypeRef.Float)],
            new PropertyDefinition {
                Id = "operation",
                DisplayName = "Operation",
                Type = GraphTypeRef.Int,
                DefaultValue = GraphValue.FromInt(0),
                EnumLabels = ["Add", "Multiply", "Maximum"],
            },
            Number(
                id: "scale",
                name: "Scale",
                value: 1,
                min: 0,
                max: 2
            )
        ),
        Node(
            id: "demo.text",
            name: "Text",
            category: "Sources",
            description: "A string source. Connect the yellow socket to Caption.",
            inputs: [],
            outputs: [Output(GraphTypeRef.String)],
            new PropertyDefinition {
                Id = "text",
                DisplayName = "Text",
                Type = GraphTypeRef.String,
                DefaultValue = GraphValue.FromString("Hello, graphs!"),
            }
        ),
        Node(
            id: "demo.toggle",
            name: "Switch",
            category: "Sources",
            description: "A boolean source. Click Enabled to toggle the preview.",
            inputs: [],
            outputs: [Output(GraphTypeRef.Bool)],
            new PropertyDefinition {
                Id = "enabled",
                DisplayName = "Enabled",
                Type = GraphTypeRef.Bool,
                DefaultValue = GraphValue.FromBool(true),
            }
        ),
        Node(
            id: "demo.output",
            name: "Preview",
            category: "Output",
            description: "A live widget compiled from the connected graph.",
            inputs: [
                Input(id: "value", name: "Value", type: GraphTypeRef.Float),
                Input(
                    id: "caption",
                    name: "Caption",
                    type: GraphTypeRef.String,
                    fallback: GraphValue.FromString("Live preview")
                ),
                Input(
                    id: "enabled",
                    name: "Enabled",
                    type: GraphTypeRef.Bool,
                    fallback: GraphValue.FromBool(true)
                ),
            ],
            outputs: [],
            new PropertyDefinition {
                Id = "digits",
                DisplayName = "Decimals",
                Type = GraphTypeRef.Int,
                DefaultValue = GraphValue.FromInt(2),
                Min = 0,
                Max = 4,
                Step = 1,
            }
        ),
    ];

    public bool CanCreateEdge(GraphDocument graph, GraphPinEndpoint from, GraphPinEndpoint to,
        out string? reason)
    {
        var pending = new Stack<Guid>();
        var seen = new HashSet<Guid>();
        pending.Push(to.NodeId);
        while (pending.TryPop(out var current))
        {
            if (current == from.NodeId)
            {
                reason = "This connection would create a feedback loop.";
                return false;
            }

            if (!seen.Add(current)) continue;
            foreach (var edge in graph.Edges.Where(e => e.From.NodeId == current))
                pending.Push(edge.To.NodeId);
        }

        reason = null;
        return true;
    }

    public GraphValidationResult Validate(GraphDocument graph)
    {
        var registry = Registry();
        var errors = GraphInputValidation.Validate(graph: graph, registry: registry).ToList();
        if (graph.Nodes.Count(n => n.DefinitionId == "demo.output") != 1)
            errors.Add(Error("Keep exactly one Preview node."));
        foreach (var node in graph.Nodes.Where(n =>
                     registry.GetNodeDefinition(n.DefinitionId) is null
                 ))
            errors.Add(Error(message: "Unknown node type.", node: node.Id));
        foreach (var edge in graph.Edges)
        {
            if (!GraphConnections.CanConnect(
                    graph: graph,
                    registry: registry,
                    a: edge.From,
                    b: edge.To,
                    reason: out string reason,
                    ignoreEdge: edge.Id
                ))
                errors.Add(Error(message: reason, node: edge.To.NodeId, pin: edge.To.PinId));
        }

        return new GraphValidationResult {
            IsValid = errors.Count == 0,
            Diagnostics = errors,
        };
    }

    private static GraphDiagnostic Error(string message, Guid? node = null, string? pin = null) =>
        new() {
            Severity = GraphDiagnosticSeverity.Error,
            Code = "demo.connection",
            Message = message,
            NodeId = node,
            PinId = pin,
        };

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

        GraphValue Property(GraphNode n, string id) =>
            n.Properties.TryGetValue(key: id, value: out var v) && !v.IsNull
                ? v
                : definitions[n.DefinitionId].Properties.First(p => p.Id == id).DefaultValue;

        GraphValue Read(GraphNode node, string pin)
        {
            var edge = graph.Edges.FirstOrDefault(e =>
                e.To == new GraphPinEndpoint(NodeId: node.Id, PinId: pin)
            );
            return edge is null
                ? definitions[node.DefinitionId].Inputs.First(p => p.Id == pin).DefaultValue!
                : Value(graph.FindNode(edge.From.NodeId)!);
        }

        GraphValue Value(GraphNode node)
        {
            if (values.TryGetValue(key: node.Id, value: out var cached)) return cached;
            GraphValue result;
            switch (node.DefinitionId)
            {
                case "demo.number": result = Property(n: node, id: "value"); break;
                case "demo.text": result = Property(n: node, id: "text"); break;
                case "demo.toggle": result = Property(n: node, id: "enabled"); break;
                case "demo.math":
                    float a = Read(node: node, pin: "a").AsFloat(),
                        b = Read(node: node, pin: "b").AsFloat();
                    float v = Property(n: node, id: "operation").AsInt() switch {
                        1 => a * b, 2 => MathF.Max(x: a, y: b), _ => a + b,
                    };
                    result = GraphValue.FromFloat(v * Property(n: node, id: "scale").AsFloat());
                    break;
                default: throw new InvalidOperationException("Unknown demo source");
            }

            values[node.Id] = result;
            return result;
        }

        var output = graph.Nodes.Single(n => n.DefinitionId == "demo.output");
        return new Result(
            Value: Read(node: output, pin: "value").AsFloat(),
            Caption: Read(node: output, pin: "caption").AsString(),
            Enabled: Read(node: output, pin: "enabled").AsBool(),
            Digits: Math.Clamp(value: Property(n: output, id: "digits").AsInt(), min: 0, max: 4)
        );
    }

    public GraphCompileResult Compile(GraphDocument graph, GraphCompileContext context)
    {
        var validation = Validate(graph);
        if (!validation.IsValid)
            return new GraphCompileResult { Diagnostics = validation.Diagnostics };
        var result = Evaluate(graph);
        return new GraphCompileResult {
            Success = true,
            CompiledArtifact = new PreviewCard(result: result, theme: theme),
        };
    }

    // The editor measures previews in screen space, including at overview zoom.
    private sealed class PreviewCard(Result result, ThemeData theme) : Widget
    {
        private Size _size;

        public override Size Measure(Constraints c) =>
            _size = c.Constrain(new Size(width: 214f, height: 160f));

        public override void Layout(Offset origin) => Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );

        public override void Paint(PaintList paint)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            float scale = MathF.Min(x: Bounds.Width / 214f, y: Bounds.Height / 160f);
            paint.AddClipStart(Bounds);
            paint.AddRect(
                bounds: Bounds,
                color: theme.Primary.WithAlpha(result.Enabled ? 0.18f : 0.05f)
            );

            void Line(string text, float fraction, float fontSize, Color color)
            {
                float size = fontSize * scale;
                int max = Math.Max(
                    val1: 1,
                    val2: (int)((Bounds.Width - (12f * scale)) / (size * 0.6f))
                );
                if (text.Length > max) text = text[..Math.Max(val1: 0, val2: max - 1)] + "…";
                float width = text.Length * size * 0.55f;
                paint.AddText(
                    text: text,
                    baselineX: Bounds.X + ((Bounds.Width - width) / 2),
                    baselineY: Bounds.Y + (Bounds.Height * fraction),
                    color: color,
                    fontSize: size
                );
            }

            Line(
                text: result.Caption,
                fraction: 0.30f,
                fontSize: 13f,
                color: theme.OnSurface
            );
            Line(
                text: result.Enabled
                    ? result.Value.ToString(
                        format: $"F{result.Digits}",
                        provider: System.Globalization.CultureInfo.InvariantCulture
                    )
                    : "Disabled",
                fraction: 0.57f,
                fontSize: 28f,
                color: result.Enabled ? theme.Primary : theme.TextMuted
            );
            Line(
                text: "Updates as you edit",
                fraction: 0.80f,
                fontSize: 10f,
                color: theme.TextMuted
            );
            paint.AddClipEnd();
        }
    }

    public static GraphDocument Sample(int sample)
    {
        var graph = new GraphDocument {
            Name = sample switch {
                1 => "Repair the wiring", 2 => "Explore a larger graph", _ => "Live playground",
            },
            DomainId = DomainId,
            SchemaId = DomainId,
        };

        GraphNode Add(string id, float x, float y, bool collapsed = false)
        {
            var node = new GraphNode { DefinitionId = id };
            graph.Nodes.Add(node);
            graph.EditorData.NodeLayouts[node.Id] = new NodeLayoutData {
                X = x,
                Y = y,
                Width = 230,
                IsCollapsed = collapsed,
            };
            return node;
        }

        void Wire(GraphNode a, GraphNode b, string pin) => graph.Edges.Add(
            new GraphEdge {
                From = new GraphPinEndpoint(NodeId: a.Id, PinId: "out"),
                To = new GraphPinEndpoint(NodeId: b.Id, PinId: pin),
            }
        );

        var number = Add(id: "demo.number", x: 30, y: 30);
        var math = Add(id: "demo.math", x: 330, y: 30);
        var text = Add(id: "demo.text", x: 30, y: 210);
        var toggle = Add(
            id: "demo.toggle",
            x: 330,
            y: 290,
            collapsed: true
        );
        var output = Add(id: "demo.output", x: 660, y: 30);
        Wire(a: number, b: math, pin: "a");
        Wire(a: math, b: output, pin: "value");
        Wire(a: text, b: output, pin: "caption");
        Wire(a: toggle, b: output, pin: "enabled");
        if (sample == 1)
        {
            // Broken on purpose: a string wired into a number input, plus a missing required A.
            graph.Edges.RemoveAt(0);
            graph.Edges.RemoveAt(0);
            Wire(a: text, b: output, pin: "value");
        }

        if (sample == 2)
        {
            graph.Edges.RemoveAll(e =>
                e.To == new GraphPinEndpoint(NodeId: output.Id, PinId: "value")
            );
            var last = math;
            for (int i = 0; i < 24; i++)
            {
                var next = Add(
                    id: "demo.math",
                    x: 330 + (i % 6 * 290),
                    y: 450 + (i / 6 * 210),
                    collapsed: i % 3 == 0
                );
                Wire(a: last, b: next, pin: "a");
                last = next;
            }

            graph.EditorData.NodeLayouts[output.Id].X = 2110;
            graph.EditorData.NodeLayouts[output.Id].Y = 660;
            Wire(a: last, b: output, pin: "value");
        }

        return graph;
    }
}
