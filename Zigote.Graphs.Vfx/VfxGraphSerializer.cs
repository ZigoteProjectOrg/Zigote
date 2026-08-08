using System.Text.Json;
using Zigote.Graphs.Core;

namespace Zigote.Graphs.Vfx;

/// <summary>
///     Serializes a VFX <see cref="GraphDocument" /> to/from a compact JSON string via plain DTOs. The
///     live
///     <see cref="GraphDocument" /> (with its <see cref="GraphValue" /> tagged unions and Guid-keyed
///     dictionaries) does not round-trip through <c>System.Text.Json</c> directly, and embedding it in
///     a
///     scene saved under <c>ReferenceHandler.Preserve</c> would crash the deserializer — so the editor
///     stores this string on the node instead (<c>SceneNode.VfxGraphJson</c>).
/// </summary>
public static class VfxGraphSerializer
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };

    public static string Serialize(GraphDocument graph)
    {
        var dto = new GraphDto {
            Name = graph.Name,
            DomainId = graph.DomainId,
            SchemaId = graph.SchemaId,
        };

        foreach (var node in graph.Nodes)
        {
            var nd = new NodeDto {
                Id = node.Id,
                DefinitionId = node.DefinitionId,
                Version = node.DefinitionVersion,
            };
            foreach (var (key, value) in node.Properties) nd.Props.Add(ToPropDto(key, value));
            if (graph.EditorData.NodeLayouts.TryGetValue(node.Id, out var layout))
                nd.Layout = new LayoutDto {
                    X = layout.X,
                    Y = layout.Y,
                    W = layout.Width,
                    H = layout.Height,
                };
            dto.Nodes.Add(nd);
        }

        foreach (var e in graph.Edges)
            dto.Edges.Add(
                new EdgeDto {
                    FromNode = e.From.NodeId,
                    FromPin = e.From.PinId,
                    ToNode = e.To.NodeId,
                    ToPin = e.To.PinId,
                }
            );

        return JsonSerializer.Serialize(dto, Options);
    }

    public static GraphDocument Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<GraphDto>(json, Options) ?? new GraphDto();
        var graph = new GraphDocument {
            Name = dto.Name,
            DomainId = string.IsNullOrEmpty(dto.DomainId) ? VfxNodeLibrary.DomainId : dto.DomainId,
            SchemaId = string.IsNullOrEmpty(dto.SchemaId)
                ? VfxNodeLibrary.EmitterSchema
                : dto.SchemaId,
        };

        foreach (var nd in dto.Nodes)
        {
            var node = new GraphNode {
                Id = nd.Id,
                DefinitionId = nd.DefinitionId,
                DefinitionVersion = nd.Version,
            };
            foreach (var p in nd.Props)
            {
                var value = ToValue(p);
                if (value is not null) node.Properties[p.Key] = value;
            }

            graph.Nodes.Add(node);
            if (nd.Layout is { } l)
                graph.EditorData.NodeLayouts[node.Id] =
                    new NodeLayoutData {
                        X = l.X,
                        Y = l.Y,
                        Width = l.W,
                        Height = l.H,
                    };
        }

        foreach (var e in dto.Edges)
            graph.Edges.Add(
                new GraphEdge {
                    From = new GraphPinEndpoint(e.FromNode, e.FromPin),
                    To = new GraphPinEndpoint(e.ToNode, e.ToPin),
                }
            );

        return graph;
    }

    private static PropDto ToPropDto(string key, GraphValue v)
    {
        var p = new PropDto {
            Key = key,
            Kind = (int)v.Kind,
        };
        switch (v.Kind)
        {
            case GraphValueKind.Bool: p.B = v.AsBool(); break;
            case GraphValueKind.Int: p.I = v.AsInt(); break;
            case GraphValueKind.Float: p.F = v.AsFloat(); break;
            case GraphValueKind.String: p.S = v.AsString(); break;
            case GraphValueKind.Float2: p.V = v.AsFloat2(); break;
            case GraphValueKind.Float3: p.V = v.AsFloat3(); break;
            case GraphValueKind.Float4: p.V = v.AsFloat4(); break;
        }

        return p;
    }

    private static GraphValue? ToValue(PropDto p)
    {
        return (GraphValueKind)p.Kind switch {
            GraphValueKind.Bool => GraphValue.FromBool(p.B ?? false),
            GraphValueKind.Int => GraphValue.FromInt(p.I ?? 0),
            GraphValueKind.Float => GraphValue.FromFloat(p.F ?? 0f),
            GraphValueKind.String => GraphValue.FromString(p.S ?? ""),
            GraphValueKind.Float2 when p.V is { Length: >= 2 } => GraphValue.FromFloat2(
                p.V[0],
                p.V[1]
            ),
            GraphValueKind.Float3 when p.V is { Length: >= 3 } => GraphValue.FromFloat3(
                p.V[0],
                p.V[1],
                p.V[2]
            ),
            GraphValueKind.Float4 when p.V is { Length: >= 4 } => GraphValue.FromFloat4(
                p.V[0],
                p.V[1],
                p.V[2],
                p.V[3]
            ),
            _ => null,
        };
    }

    // ── DTOs ───────────────────────────────────────────────────────────────────
    private sealed class GraphDto
    {
        public string DomainId = "";
        public string Name = "";
        public string SchemaId = "";
        public List<NodeDto> Nodes { get; set; } = [];
        public List<EdgeDto> Edges { get; set; } = [];
    }

    private sealed class NodeDto
    {
        public Guid Id { get; set; }
        public string DefinitionId { get; set; } = "";
        public int Version { get; set; } = 1;

        // Must keep its setter: System.Text.Json skips getter-only properties on deserialize, which
        // silently drops every node property on round-trip (spawn rates fall back to defaults).
        public List<PropDto> Props { get; set; } = [];

        public LayoutDto? Layout { get; set; }
    }

    private sealed class PropDto
    {
        public string Key { get; set; } = "";
        public int Kind { get; set; }
        public bool? B { get; set; }
        public int? I { get; set; }
        public float? F { get; set; }
        public string? S { get; set; }
        public float[]? V { get; set; }
    }

    private sealed class EdgeDto
    {
        public Guid FromNode { get; set; }
        public string FromPin { get; set; } = "";
        public Guid ToNode { get; set; }
        public string ToPin { get; set; } = "";
    }

    private sealed class LayoutDto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float W { get; set; }
        public float H { get; set; }
    }
}