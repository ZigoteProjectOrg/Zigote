using Zigote.Runtime.Scene;
using Zigote.Scripting.Metadata;

namespace Zigote.Editor.Export;

public enum ExportMode
{
    /// <summary>Self-contained JIT publish — works for every desktop RID from any host OS.</summary>
    SelfContained,

    /// <summary>
    ///     NativeAOT publish — host-OS RIDs only (cross-OS AOT is unsupported by .NET).
    ///     Artifacts are suffixed <c>-aot</c>.
    /// </summary>
    NativeAot,
}

/// <summary>
///     One publish+package unit: a platform in a mode. A single export pass runs every
///     selected RID × mode combination.
/// </summary>
public sealed record ExportJob(string Rid, ExportMode Mode);

public enum ExportJobState
{
    Running,
    Succeeded,
    Failed,
    Skipped,
}

public sealed record ExportJobUpdate(ExportJob Job, ExportJobState State, string? Detail = null);

public sealed record ExportOptions(
    string OutputDir,
    IReadOnlyList<string> Rids,
    IReadOnlyList<ExportMode> Modes);

/// <summary>Everything the exporter needs from the host (editor dialog or headless CLI).</summary>
public sealed record ExportInput(
    string ProjectPath,
    ZigoteProject Project,
    ScriptRegistry Scripts,
    string? ScriptAssemblyName);