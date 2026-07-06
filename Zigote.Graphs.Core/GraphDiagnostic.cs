namespace Zigote.Graphs.Core;

/// <summary>A validation or compilation diagnostic attached to a graph, node, or pin.</summary>
public sealed class GraphDiagnostic
{
    public GraphDiagnosticSeverity Severity { get; init; }

    /// <summary>Domain-namespaced error code, e.g. "SG0007", "RG0003".</summary>
    public string Code { get; init; } = "";

    public string Message { get; init; } = "";

    /// <summary>Node the diagnostic is attached to, if any.</summary>
    public Guid? NodeId { get; init; }

    /// <summary>Pin on the node the diagnostic is attached to, if any.</summary>
    public string? PinId { get; init; }

    public string? DomainId { get; init; }
}

public enum GraphDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed class GraphValidationResult
{
    public static readonly GraphValidationResult Ok = new() { IsValid = true };

    public bool IsValid { get; init; }
    public IReadOnlyList<GraphDiagnostic> Diagnostics { get; init; } = [];
}