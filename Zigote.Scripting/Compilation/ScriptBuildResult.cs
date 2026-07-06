namespace Zigote.Scripting.Compilation;

public sealed class ScriptBuildResult
{
    public bool Success { get; init; }

    /// <summary>
    ///     True when the build was skipped because the inputs (sources + project files + referenced
    ///     project outputs) were unchanged since the last successful build — the cached assembly was
    ///     reused instead of shelling out to <c>dotnet build</c>. See <see cref="ScriptCompiler" />.
    /// </summary>
    public bool Cached { get; init; }

    public string? OutputAssemblyPath { get; init; }
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; init; } = [];
    public string RawOutput { get; init; } = "";

    public static ScriptBuildResult Failure(string message, string raw = "")
    {
        return new ScriptBuildResult {
            Success = false,
            RawOutput = raw,
            Diagnostics = [
                new ScriptDiagnostic {
                    Message = message,
                    Severity = DiagnosticSeverity.Error,
                },
            ],
        };
    }
}