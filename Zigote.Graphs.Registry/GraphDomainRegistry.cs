namespace Zigote.Graphs.Registry;

/// <summary>
///     Central registry for all graph domains and their type/node definitions.
///     The editor resolves all domain-specific behavior through this registry.
/// </summary>
public sealed class GraphDomainRegistry
{
    private readonly Dictionary<string, IGraphDomain> _domains = new();
    private readonly Dictionary<string, NodeDefinition> _nodes = new();
    private readonly Dictionary<string, GraphTypeDefinition> _types = new();

    public GraphDomainRegistry()
    {
        // Register built-in core types.
        foreach (var t in CoreTypeDefinitions.All)
            _types[t.Id] = t;
    }

    public IReadOnlyCollection<IGraphDomain> AllDomains => _domains.Values;

    public IReadOnlyCollection<GraphTypeDefinition> AllTypes => _types.Values;

    public IReadOnlyCollection<NodeDefinition> AllNodeDefinitions => _nodes.Values;

    // ── Domain management ─────────────────────────────────────────────────────

    public void RegisterDomain(IGraphDomain domain)
    {
        _domains[domain.Id] = domain;

        foreach (var t in domain.GetTypeDefinitions())
            _types[t.Id] = t;

        foreach (var n in domain.GetNodeDefinitions())
            _nodes[n.Id] = n;
    }

    public IGraphDomain GetDomain(string domainId)
    {
        if (!_domains.TryGetValue(key: domainId, value: out var d))
            throw new KeyNotFoundException($"Graph domain '{domainId}' is not registered.");
        return d;
    }

    public bool TryGetDomain(string domainId, out IGraphDomain? domain) =>
        _domains.TryGetValue(key: domainId, value: out domain);

    // ── Type registry ─────────────────────────────────────────────────────────

    public GraphTypeDefinition? GetTypeDefinition(string typeId) =>
        _types.TryGetValue(key: typeId, value: out var t) ? t : null;

    // ── Node definition registry ──────────────────────────────────────────────

    public NodeDefinition? GetNodeDefinition(string definitionId) =>
        _nodes.TryGetValue(key: definitionId, value: out var n) ? n : null;

    public IEnumerable<NodeDefinition> NodeDefinitionsForDomain(string domainId) =>
        _nodes.Values.Where(n => n.DomainId == domainId);

    public IEnumerable<NodeDefinition> Search(string query, string? domainId = null)
    {
        var source = domainId is null ? _nodes.Values : NodeDefinitionsForDomain(domainId);
        if (string.IsNullOrWhiteSpace(query)) return source;
        string lower = query.ToLowerInvariant();
        return source.Where(n =>
            n.DisplayName.Contains(
                value: lower,
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) ||
            n.Category.Contains(value: lower, comparisonType: StringComparison.OrdinalIgnoreCase) ||
            n.Tags.Any(t => t.Contains(
                    value: lower,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
            )
        );
    }
}
