using System.Text;

namespace Zigote.Editor.Scene;

/// <summary>
///     A lean, user-facing summary of a glTF import: object counts plus any
///     warnings/errors the importer collected while normalising the file.
///     This replaces the loader's previous scattered <c>Console.Error.WriteLine</c>
///     calls with a structured result the editor can surface (snackbar + log).
///     It is intentionally small — counts and string diagnostics — not the full
///     severity/code/object diagnostic framework, which the lean engine doesn't need.
/// </summary>
public sealed class GltfImportReport
{
    public readonly List<string> Errors = [];

    public readonly List<string> Warnings = [];
    public int Materials;
    public int Meshes;
    public int Nodes;
    public int Primitives;

    public int Scenes;
    public int Textures;
    public string SourceName { get; init; } = "";

    public bool HasWarnings => Warnings.Count > 0;
    public bool HasErrors => Errors.Count > 0;

    public void Warn(string message)
    {
        // De-dup so an attribute missing on every primitive of a mesh is reported once.
        if (!Warnings.Contains(message)) Warnings.Add(message);
    }

    public void Error(string message)
    {
        if (!Errors.Contains(message)) Errors.Add(message);
    }

    /// <summary>Compact single line, suitable for a snackbar.</summary>
    public string OneLine()
    {
        string tail = (HasErrors, HasWarnings) switch {
            (true, _) => $" — {Errors.Count} error(s), {Warnings.Count} warning(s)",
            (false, true) => $" — {Warnings.Count} warning(s)",
            _ => "",
        };
        return $"Imported {SourceName}: {Primitives} prim(s), {Materials} material(s){tail}";
    }

    /// <summary>Multi-line report for the log (mirrors the design doc's import report).</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Asset: {SourceName}");
        sb.AppendLine($"Scenes: {Scenes}");
        sb.AppendLine($"Nodes: {Nodes}");
        sb.AppendLine($"Meshes: {Meshes}");
        sb.AppendLine($"Primitives: {Primitives}");
        sb.AppendLine($"Materials: {Materials}");
        sb.AppendLine($"Textures: {Textures}");

        if (HasErrors)
        {
            sb.AppendLine();
            sb.AppendLine("Errors:");
            foreach (string e in Errors) sb.AppendLine($"- {e}");
        }

        if (HasWarnings)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (string w in Warnings) sb.AppendLine($"- {w}");
        }

        return sb.ToString();
    }
}
