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

    // Selection order: the last entry is the primary node (the one the inspector edits). A
    // HashSet alone has no order to take "last" from.
    private readonly List<Guid> _selectionOrder = [];

    public GraphEditorState(GraphDocument graph, GraphDomainRegistry registry)
    {
        Graph = graph;
        Registry = registry;
        Commands = new GraphCommandStack(graph);
        Commands.Changed += () =>
        {
            PruneSelection(); // undo/redo and deletes can remove what was selected
            if (AutoCompile) ReactiveCompile();
            else Validate();
            GraphChanged?.Invoke();
        };

        // Compile once on load so the panel shows initial status immediately.
        if (AutoCompile) ReactiveCompile();
    }

    public GraphDocument Graph { get; }
    public GraphCommandStack Commands { get; }
    public GraphDomainRegistry Registry { get; }
    public IReadOnlySet<Guid> SelectedNodes => _selectedNodes;
    public Guid? PrimarySelection => _selectionOrder.Count > 0 ? _selectionOrder[^1] : null;

    // ── Edge selection ────────────────────────────────────────────────────────

    // In click order, like the nodes: the last one is the primary cable.
    private readonly List<Guid> _selectedEdges = [];

    /// <summary>Every selected cable. Nodes and cables are never selected together.</summary>
    public IReadOnlyList<Guid> SelectedEdges => _selectedEdges;

    /// <summary>The primary (most recently selected) cable, or null.</summary>
    public Guid? SelectedEdgeId => _selectedEdges.Count > 0 ? _selectedEdges[^1] : null;

    // ── Validation cache ──────────────────────────────────────────────────────

    public GraphValidationResult? LastValidation { get; private set; }

    // ── Reactive compile ──────────────────────────────────────────────────────

    /// <summary>
    ///     When true, each graph mutation triggers Validate() + Compile() automatically.
    ///     The result is exposed via <see cref="LastCompileResult" /> and <see cref="CompileChanged" />.
    /// </summary>
    public bool AutoCompile { get; set; } = true;

    public GraphCompileResult? LastCompileResult { get; private set; }

    public void Select(Guid? nodeId) => SetSelection(nodeId.HasValue ? [nodeId.Value] : []);

    /// <summary>Adds a node and makes it the primary one. Deselects any edge: one kind at a time.</summary>
    public void AddToSelection(Guid nodeId)
    {
        _selectionOrder.Remove(nodeId);
        _selectionOrder.Add(nodeId);
        _selectedNodes.Add(nodeId);
        _selectedEdges.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>Shift/Ctrl-click: selects an unselected node, deselects a selected one.</summary>
    public void ToggleSelection(Guid nodeId)
    {
        if (!_selectedNodes.Remove(nodeId))
        {
            AddToSelection(nodeId);
            return;
        }

        _selectionOrder.Remove(nodeId);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    ///     Replaces the node selection in one step, firing <see cref="SelectionChanged" /> once and
    ///     only when the set or its primary node actually changed — a marquee calls this every move.
    /// </summary>
    public void SetSelection(IEnumerable<Guid> nodeIds)
    {
        var next = nodeIds.Distinct().ToList();
        if (_selectedEdges.Count == 0 && next.SequenceEqual(_selectionOrder)) return;
        _selectedNodes.Clear();
        _selectionOrder.Clear();
        foreach (var id in next)
        {
            _selectedNodes.Add(id);
            _selectionOrder.Add(id);
        }

        _selectedEdges.Clear();
        SelectionChanged?.Invoke();
    }

    public void ClearSelection() => SetSelection([]);

    private void PruneSelection()
    {
        int removed = _selectionOrder.RemoveAll(id => Graph.FindNode(id) is null);
        if (removed > 0) _selectedNodes.IntersectWith(_selectionOrder);
        int edgesGone = _selectedEdges.RemoveAll(id => Graph.Edges.All(e => e.Id != id));
        if (removed > 0 || edgesGone > 0) SelectionChanged?.Invoke();
    }

    public bool IsSelected(Guid nodeId) => _selectedNodes.Contains(nodeId);

    public void SelectEdge(Guid? edgeId) => SetEdgeSelection(edgeId.HasValue ? [edgeId.Value] : []);

    /// <summary>Shift/Ctrl-click on a cable: adds it to the selected cables, or removes it.</summary>
    public void ToggleEdgeSelection(Guid edgeId)
    {
        var next = _selectedEdges.ToList();
        if (!next.Remove(edgeId)) next.Add(edgeId);
        SetEdgeSelection(next);
    }

    /// <summary>Replaces the cable selection (and drops any node selection) in one step.</summary>
    public void SetEdgeSelection(IEnumerable<Guid> edgeIds)
    {
        var next = edgeIds.Distinct().ToList();
        if (_selectedNodes.Count == 0 && next.SequenceEqual(_selectedEdges)) return;
        _selectedEdges.Clear();
        _selectedEdges.AddRange(next);
        _selectedNodes.Clear();
        _selectionOrder.Clear();
        SelectionChanged?.Invoke();
    }

    public bool IsEdgeSelected(Guid edgeId) => _selectedEdges.Contains(edgeId);

    public GraphValidationResult Validate()
    {
        if (Registry.TryGetDomain(domainId: Graph.DomainId, domain: out var domain) &&
            domain is not null)
            LastValidation = domain.Validate(Graph);
        else
            LastValidation = GraphValidationResult.Ok;
        var diagnostics = LastValidation.Diagnostics.ToList();
        foreach (var diagnostic in GraphInputValidation.Validate(graph: Graph, registry: Registry))
        {
            if (!diagnostics.Any(d => d.NodeId == diagnostic.NodeId && d.PinId == diagnostic.PinId
                    && d.Severity == GraphDiagnosticSeverity.Error
                ))
                diagnostics.Add(diagnostic);
        }

        LastValidation = new GraphValidationResult {
            IsValid = LastValidation.IsValid &&
                      !diagnostics.Any(d => d.Severity == GraphDiagnosticSeverity.Error),
            Diagnostics = diagnostics,
        };
        return LastValidation;
    }

    /// <summary>Force a validate + compile cycle, even when <see cref="AutoCompile" /> is false.</summary>
    public void TriggerCompile() => ReactiveCompile();

    private void ReactiveCompile()
    {
        Validate();
        if (!Registry.TryGetDomain(domainId: Graph.DomainId, domain: out var domain) ||
            domain is null) return;
        var compiled = domain.Compile(
            graph: Graph,
            context: new GraphCompileContext { TargetPlatform = "gallery" }
        );
        var diagnostics = compiled.Diagnostics.ToList();
        foreach (var diagnostic in LastValidation!.Diagnostics)
        {
            if (!diagnostics.Any(d =>
                    d.NodeId == diagnostic.NodeId && d.PinId == diagnostic.PinId &&
                    d.Code == diagnostic.Code
                ))
                diagnostics.Add(diagnostic);
        }

        LastCompileResult = new GraphCompileResult {
            Success = compiled.Success && LastValidation.IsValid,
            Diagnostics = diagnostics,
            CompiledArtifact = compiled.CompiledArtifact,
        };
        CompileChanged?.Invoke();
    }

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action? GraphChanged;
    public event Action? SelectionChanged;
    public event Action? CompileChanged;
}
