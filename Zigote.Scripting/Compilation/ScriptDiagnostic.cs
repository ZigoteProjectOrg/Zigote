namespace Zigote.Scripting.Compilation;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

public sealed class ScriptDiagnostic
{
    public required string Message { get; init; }
    public string? File { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public DiagnosticSeverity Severity { get; init; }

    public override string ToString()
    {
        return File is null
            ? $"[{Severity}] {Message}"
            : $"[{Severity}] {File}({Line},{Column}): {Message}";
    }
}