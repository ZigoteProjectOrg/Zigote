using Zigote.Graphs.Core;
using Zigote.Vfx;

namespace Zigote.Graphs.Vfx;

/// <summary>The compiled output of a VFX graph: the runtime emitter asset + diagnostics.</summary>
public sealed class CompiledVfxGraph
{
    public bool Success { get; init; }
    public VfxEmitterAsset Asset { get; init; } = new();
    public IReadOnlyList<GraphDiagnostic> Diagnostics { get; init; } = [];
}
