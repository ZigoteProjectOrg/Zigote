using Zigote.Graphs.Commands;
using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;

namespace Zigote.Graphs.Editor;

/// <summary>
///     Shared state for a single open graph document.
///     All graph editor panels read and write through this object.
///     Supports reactive auto-compile: each command fires validation + domain Compile().
/// </summary>
public sealed class GraphEditorState
{
    // ── Node selection ────────────────────────────────────────────────────────

    private readonly HashSet<Guid> _selectedNodes = [];

    public GraphEditorState(GraphDocument graph, GraphDomainRegistry registry)
    {
        Graph = graph;
        Registry = registry;
        Commands = new GraphCommandStack(graph);
        Commands.Changed += () =>
        {
            SelectedEdgeId = null; // stale selection after structural change
            GraphChanged?.Invoke();
            if (AutoCompile) ReactiveCompile();
        };

        // Compile once on load so the panel shows initial status immediately.
        if (AutoCompile) ReactiveCompile();
    }

    public GraphDocument Graph { get; }
    public GraphCommandStack Commands { get; }
    public GraphDomainRegistry Registry { get; }
    public IReadOnlySet<Guid> SelectedNodes => _selectedNodes;
    public Guid? PrimarySelection => _selectedNodes.Count > 0 ? _selectedNodes.Last() : null;

    // ── Edge selection ────────────────────────────────────────────────────────

    public Guid? SelectedEdgeId { get; private set; }

    // ── Validation cache ──────────────────────────────────────────────────────

    public GraphValidationResult? LastValidation { get; private set; }

    // ── Reactive compile ──────────────────────────────────────────────────────

    /// <summary>
    ///     When true, each graph mutation triggers Validate() + Compile() automatically.
    ///     The result is exposed via <see cref="LastCompileResult" /> and <see cref="CompileChanged" />.
    /// </summary>
    public bool AutoCompile { get; set; } = true;

    public GraphCompileResult? LastCompileResult { get; private set; }

    public void Select(Guid? nodeId)
    {
        _selectedNodes.Clear();
        if (nodeId.HasValue) _selectedNodes.Add(nodeId.Value);
        SelectedEdgeId = null;
        SelectionChanged?.Invoke();
    }

    public void AddToSelection(Guid nodeId)
    {
        _selectedNodes.Add(nodeId);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        _selectedNodes.Clear();
        SelectedEdgeId = null;
        SelectionChanged?.Invoke();
    }

    public bool IsSelected(Guid nodeId)
    {
        return _selectedNodes.Contains(nodeId);
    }

    public void SelectEdge(Guid? edgeId)
    {
        SelectedEdgeId = edgeId;
        _selectedNodes.Clear();
        SelectionChanged?.Invoke();
    }

    public bool IsEdgeSelected(Guid edgeId)
    {
        return SelectedEdgeId == edgeId;
    }

    public GraphValidationResult Validate()
    {
        if (Registry.TryGetDomain(Graph.DomainId, out var domain) && domain is not null)
            LastValidation = domain.Validate(Graph);
        else
            LastValidation = GraphValidationResult.Ok;
        return LastValidation;
    }

    /// <summary>Force a validate + compile cycle, even when <see cref="AutoCompile" /> is false.</summary>
    public void TriggerCompile()
    {
        ReactiveCompile();
    }

    private void ReactiveCompile()
    {
        Validate();
        if (!Registry.TryGetDomain(Graph.DomainId, out var domain) || domain is null) return;
        LastCompileResult = domain.Compile(
            Graph,
            new GraphCompileContext { TargetPlatform = "gallery" }
        );
        CompileChanged?.Invoke();
    }

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action? GraphChanged;
    public event Action? SelectionChanged;
    public event Action? CompileChanged;
}
