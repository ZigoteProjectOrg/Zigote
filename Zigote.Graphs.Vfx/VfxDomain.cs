using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;

namespace Zigote.Graphs.Vfx;

/// <summary>
///     The <see cref="IGraphDomain" /> for VFX emitter graphs (<c>zigote.vfx</c> / <c>vfx.emitter</c>
///     ).
///     Delegates node defs to <see cref="VfxNodeLibrary" /> and compilation to
///     <see cref="VfxGraphCompiler" />.
///     Pure logic — the editor-side scene glue lives separately (see <c>Zigote.Editor/Vfx</c>).
/// </summary>
public sealed class VfxDomain : IGraphDomain
{
    public string Id => VfxNodeLibrary.DomainId;
    public string DisplayName => "VFX";
    public IReadOnlyList<string> SupportedSchemas => [VfxNodeLibrary.EmitterSchema];

    public IReadOnlyList<GraphTypeDefinition> GetTypeDefinitions() =>
        VfxNodeLibrary.TypeDefinitions;

    public IReadOnlyList<NodeDefinition> GetNodeDefinitions() => VfxNodeLibrary.Definitions;

    public bool CanCreateEdge(GraphDocument graph, GraphPinEndpoint from, GraphPinEndpoint to,
        out string? reason)
    {
        reason = null;
        if (from.NodeId == to.NodeId)
        {
            reason = "A node cannot connect to itself.";
            return false;
        }

        var fromNode = graph.FindNode(from.NodeId);
        var toNode = graph.FindNode(to.NodeId);
        if (fromNode is null || toNode is null)
        {
            reason = "Endpoint node not found.";
            return false;
        }

        var fromType = VfxNodeLibrary.PinType(
            definitionId: fromNode.DefinitionId,
            pinId: from.PinId,
            direction: PinDirection.Output
        );
        var toType = VfxNodeLibrary.PinType(
            definitionId: toNode.DefinitionId,
            pinId: to.PinId,
            direction: PinDirection.Input
        );
        if (fromType is null || toType is null)
        {
            reason = "Unknown pin.";
            return false;
        }

        if (fromType.Value.Id != toType.Value.Id &&
            fromType.Value.Id != GraphTypeRef.Any.Id && toType.Value.Id != GraphTypeRef.Any.Id)
        {
            reason = $"Type mismatch: {fromType} → {toType}.";
            return false;
        }

        return true;
    }

    public GraphValidationResult Validate(GraphDocument graph)
    {
        var diags = new List<GraphDiagnostic>();
        int outputs = graph.Nodes.Count(n => n.DefinitionId == VfxNodeLibrary.Output);
        if (outputs > 1)
        {
            diags.Add(
                new GraphDiagnostic {
                    Severity = GraphDiagnosticSeverity.Error,
                    Code = "VFX0003",
                    Message = "Graph has more than one VFX Output node.",
                    DomainId = Id,
                }
            );
        }

        diags.AddRange(VfxGraphCompiler.Compile(graph).Diagnostics);
        return new GraphValidationResult {
            IsValid = !diags.Any(d => d.Severity == GraphDiagnosticSeverity.Error),
            Diagnostics = diags,
        };
    }

    public GraphCompileResult Compile(GraphDocument graph, GraphCompileContext context)
    {
        var compiled = VfxGraphCompiler.Compile(graph);
        return new GraphCompileResult {
            Success = compiled.Success,
            Diagnostics = compiled.Diagnostics,
            CompiledArtifact = compiled,
        };
    }

    /// <summary>A registry with the VFX domain (and core types) registered, ready for the graph editor.</summary>
    public static GraphDomainRegistry CreateRegistry()
    {
        var registry = new GraphDomainRegistry();
        registry.RegisterDomain(new VfxDomain());
        return registry;
    }
}
