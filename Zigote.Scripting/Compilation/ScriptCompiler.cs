using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Zigote.Scripting.Compilation;

/// <summary>
///     Compiles a user script project by shelling out to <c>dotnet build</c>.
///     Returns structured diagnostics so the editor console can display errors inline.
///     <para>
///         <b>Incremental cache.</b> Before building, a fingerprint of the build inputs is computed —
///         every <c>.cs</c>/<c>.csproj</c>/<c>.props</c>/<c>.targets</c> under the project
///         (content-hashed),
///         each referenced project's built assembly (timestamp+size), and the NuGet restore state. If
///         it
///         matches the fingerprint stored after the last successful build (and that assembly still
///         exists),
///         the <c>dotnet build</c> is skipped entirely and the cached assembly is reused
///         (<see cref="ScriptBuildResult.Cached" />). This makes opening a project / a spurious
///         file-watcher
///         event near-instant when nothing actually changed, instead of paying the MSBuild startup
///         cost.
///     </para>
/// </summary>
public static class ScriptCompiler
{
    // MSBuild error format: "path(line,col): error CS0000: message"
    private static readonly Regex DiagPattern =
        new(
            pattern:
            @"^(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+\w+:\s*(?<msg>.+)$",
            options: RegexOptions.Compiled | RegexOptions.Multiline
        );

    public static async Task<ScriptBuildResult> BuildAsync(
        string projectPath,
        bool release = false,
        bool force = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(projectPath))
            return ScriptBuildResult.Failure($"Project not found: {projectPath}");

        string config = release ? "Release" : "Debug";
        string outputDir = Path.Combine(
            path1: Path.GetDirectoryName(projectPath)!,
            path2: "bin",
            path3: config
        );

        // Incremental cache: skip the dotnet build when the inputs are unchanged since the last success.
        // A hashing failure (unreadable file, etc.) leaves `fingerprint` null → always build (safe default).
        string cachePath = CachePath(projectPath: projectPath, config: config);
        string? fingerprint = null;
        try
        {
            fingerprint = ComputeBuildFingerprint(projectPath: projectPath, config: config);
        }
        catch
        {
            // ignore — fall through to a full build
        }

        if (!force && fingerprint != null && ReadCache(cachePath) is { } cached &&
            cached.Fingerprint == fingerprint && cached.Dll is { } dll && File.Exists(dll))
        {
            return new ScriptBuildResult {
                Success = true,
                Cached = true,
                OutputAssemblyPath = dll,
            };
        }

        var psi = new ProcessStartInfo(
            fileName: "dotnet",
            arguments: $"build \"{projectPath}\" -c {config} --nologo"
        ) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdout.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderr.AppendLine(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct);

        string raw = stdout.ToString() + stderr;
        var diagnostics = ParseDiagnostics(raw);
        bool succeeded = proc.ExitCode == 0;

        if (!succeeded)
        {
            return new ScriptBuildResult {
                Success = false,
                RawOutput = raw,
                Diagnostics = diagnostics,
            };
        }

        // Find the built DLL. Pick the project's OWN assembly by name (csproj name == assembly name
        // by convention) — not just the first .dll, since ProjectReferences (e.g. Zigote.ECS) copy
        // their own DLLs into the same output folder and enumeration order is not guaranteed.
        string expectedDll = Path.GetFileNameWithoutExtension(projectPath) + ".dll";

        bool NotRef(string f) => !f.Contains("ref" + Path.DirectorySeparatorChar);

        string? dllPath =
            Directory.GetFiles(
                    path: outputDir,
                    searchPattern: expectedDll,
                    searchOption: SearchOption.AllDirectories
                )
                .FirstOrDefault(NotRef)
            ?? Directory.GetFiles(
                    path: outputDir,
                    searchPattern: "*.dll",
                    searchOption: SearchOption.AllDirectories
                )
                .FirstOrDefault(NotRef);

        // Record the fingerprint of the inputs that produced this assembly so the next build can skip
        // dotnet entirely if nothing changed. Recompute it here (after the build): MSBuild may have
        // restored packages / regenerated obj artefacts, which we want reflected. Best-effort.
        if (fingerprint != null && dllPath != null)
        {
            try
            {
                WriteCache(
                    path: cachePath,
                    cache: new BuildCache(
                        Fingerprint: ComputeBuildFingerprint(
                            projectPath: projectPath,
                            config: config
                        ),
                        Dll: dllPath
                    )
                );
            }
            catch
            {
                // ignore — a missing cache just means the next build runs in full
            }
        }

        return new ScriptBuildResult {
            Success = true,
            OutputAssemblyPath = dllPath,
            RawOutput = raw,
            Diagnostics = diagnostics,
        };
    }

    // ── Incremental-build fingerprint ─────────────────────────────────────────

    /// <summary>
    ///     Compute a hash of everything that should trigger a rebuild: the project's own source and
    ///     project files (content-hashed), each <c>&lt;ProjectReference&gt;</c>'s built assembly
    ///     (timestamp+size, so an engine rebuild invalidates the cache), the NuGet restore state, and the
    ///     build configuration. Deterministic (inputs are sorted). Public so it can be unit-tested.
    /// </summary>
    public static string ComputeBuildFingerprint(string projectPath, string config = "Debug")
    {
        string projDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var sb = new StringBuilder();

        // 1. Source + project files under the project (content-hashed; obj/ and bin/ excluded).
        foreach (string file in EnumerateInputFiles(projDir))
        {
            string rel = Path.GetRelativePath(relativeTo: projDir, path: file)
                .Replace(oldChar: '\\', newChar: '/');
            sb.Append(rel).Append('=').Append(HashFileContent(file)).Append('\n');
        }

        // 2. Referenced projects' built assemblies (timestamp+size) — rebuilding the engine bumps these.
        foreach (var dll in ResolveProjectReferenceAssemblies(
                     projectPath: projectPath,
                     projDir: projDir,
                     config: config
                 ))
            sb.Append("ref:").Append(dll.Key).Append('=').Append(dll.Value).Append('\n');

        // 3. NuGet restore / package graph state.
        string assets = Path.Combine(path1: projDir, path2: "obj", path3: "project.assets.json");
        if (File.Exists(assets)) sb.Append("assets=").Append(Stamp(assets)).Append('\n');

        sb.Append("config=").Append(config);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static IEnumerable<string> EnumerateInputFiles(string projDir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string pattern in new[] {
                     "*.cs",
                     "*.csproj",
                     "*.props",
                     "*.targets",
                 })
        foreach (string file in Directory.EnumerateFiles(
                     path: projDir,
                     searchPattern: pattern,
                     searchOption: SearchOption.AllDirectories
                 ))
        {
            if (!IsGeneratedDir(projDir: projDir, file: file))
                seen.Add(Path.GetFullPath(file));
        }

        var ordered = seen.ToList();
        ordered.Sort(StringComparer.Ordinal); // stable order → deterministic hash
        return ordered;
    }

    // Skip MSBuild's intermediate (obj/) and output (bin/) trees — those are build products, not inputs.
    private static bool IsGeneratedDir(string projDir, string file)
    {
        string rel = Path.GetRelativePath(relativeTo: projDir, path: file)
            .Replace(oldChar: '\\', newChar: '/');
        return rel.StartsWith(value: "obj/", comparisonType: StringComparison.Ordinal) ||
               rel.Contains(
                   value: "/obj/",
                   comparisonType: StringComparison.Ordinal
               )
               || rel.StartsWith(
                   value: "bin/",
                   comparisonType: StringComparison.Ordinal
               ) ||
               rel.Contains(
                   value: "/bin/",
                   comparisonType: StringComparison.Ordinal
               );
    }

    private static string HashFileContent(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Stamp(string path)
    {
        var fi = new FileInfo(path);
        return $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
    }

    /// <summary>
    ///     Resolve each <c>&lt;ProjectReference&gt;</c> in the csproj to its built assembly under
    ///     <c>bin/{config}</c> and return name→(timestamp:size) stamps. Captures "a dependency changed"
    ///     (e.g. the engine was rebuilt). Missing/unbuilt references simply contribute nothing.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> ResolveProjectReferenceAssemblies(
        string projectPath, string projDir, string config)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        XDocument doc;
        try
        {
            doc = XDocument.Load(projectPath);
        }
        catch
        {
            return result;
        }

        foreach (var pr in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            string? include = pr.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;

            string refProjPath = Path.GetFullPath(
                Path.Combine(path1: projDir, path2: include.Replace(oldChar: '\\', newChar: '/'))
            );
            string? refDir = Path.GetDirectoryName(refProjPath);
            if (refDir == null) continue;
            string refName = Path.GetFileNameWithoutExtension(refProjPath);
            string binDir = Path.Combine(path1: refDir, path2: "bin", path3: config);
            if (!Directory.Exists(binDir)) continue;

            foreach (string asm in Directory.EnumerateFiles(
                         path: binDir,
                         searchPattern: refName + ".dll",
                         searchOption: SearchOption.AllDirectories
                     ))
            {
                result[Path.GetRelativePath(relativeTo: refDir, path: asm)
                    .Replace(oldChar: '\\', newChar: '/')] = Stamp(asm);
            }
        }

        return result;
    }

    private static string CachePath(string projectPath, string config)
    {
        string projDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        return Path.Combine(
            path1: projDir,
            path2: "obj",
            path3: $".zigote-scriptbuild-{config}.json"
        );
    }

    private static BuildCache? ReadCache(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<BuildCache>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string path, BuildCache cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path: path, contents: JsonSerializer.Serialize(cache));
    }

    private static IReadOnlyList<ScriptDiagnostic> ParseDiagnostics(string output)
    {
        var list = new List<ScriptDiagnostic>();
        foreach (Match m in DiagPattern.Matches(output))
        {
            string sevStr = m.Groups["sev"].Value;
            var severity = sevStr == "error"
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning;
            list.Add(
                new ScriptDiagnostic {
                    File = m.Groups["file"].Value.Trim(),
                    Line = int.Parse(m.Groups["line"].Value),
                    Column = int.Parse(m.Groups["col"].Value),
                    Message = m.Groups["msg"].Value.Trim(),
                    Severity = severity,
                }
            );
        }

        return list;
    }

    // ── Cache file ────────────────────────────────────────────────────────────

    private sealed record BuildCache(string Fingerprint, string? Dll);
}
