using System.Runtime.InteropServices;
using Zigote.Runtime.Scene;
using Zigote.Scripting;
using Zigote.Scripting.Compilation;
using Zigote.Scripting.Loading;
using Zigote.Scripting.Metadata;

namespace Zigote.Editor.Export;

/// <summary>
///     Headless export:
///     <c>Zigote.Editor --export &lt;project&gt; [--rids a,b] [--mode jit|aot] [--out dir]</c>.
///     Runs the same pipeline as the editor dialog without a window or engine init (graph compilation
///     and staging are pure); CI drives this via build/export.sh.
/// </summary>
public static class ExportCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? projectPath = null;
        var rids = new List<string>();
        var modes = new List<ExportMode>();
        string? outDir = null;

        for (var i = 2; i < args.Length; i++)
            switch (args[i])
            {
                case "--rids" when i + 1 < args.Length:
                    rids.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "--mode" when i + 1 < args.Length:
                    // "jit", "aot", "both", or a comma list — one pass can produce every flavor.
                    foreach (var m in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        switch (m.ToLowerInvariant())
                        {
                            case "aot": modes.Add(ExportMode.NativeAot); break;
                            case "jit": modes.Add(ExportMode.SelfContained); break;
                            case "both":
                                modes.Add(ExportMode.SelfContained);
                                modes.Add(ExportMode.NativeAot);
                                break;
                        }

                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                default:
                    projectPath ??= args[i];
                    break;
            }

        if (projectPath is null)
        {
            Console.Error.WriteLine(
                "usage: Zigote.Editor --export <project.zigoteproj> [--rids osx-arm64,win-x64,…] [--mode jit|aot|both] [--out dir]"
            );
            return 2;
        }

        projectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(projectPath))
        {
            Console.Error.WriteLine($"[export] project not found: {projectPath}");
            return 2;
        }

        var project = ZigoteProject.Load(projectPath);
        var projDir = Path.GetDirectoryName(projectPath)!;
        Directory.SetCurrentDirectory(projDir);

        // Same registry population as the editor: built-in sample components + the game's assembly.
        var registry = new ScriptRegistry();
        registry.Load(typeof(Component).Assembly);
        string? scriptAsm = null;
        ScriptDomain? domain = null;
        if (project.ScriptProject is { Length: > 0 } sp)
        {
            var csproj = Path.GetFullPath(Path.Combine(projDir, sp));
            Console.WriteLine($"[export] building scripts: {csproj}");
            var result = await ScriptCompiler.BuildAsync(csproj);
            if (!result.Success || result.OutputAssemblyPath is null)
            {
                foreach (var d in result.Diagnostics) Console.Error.WriteLine(d);
                Console.Error.WriteLine("[export] script build failed.");
                return 1;
            }

            // Kept alive for the whole export — the registry holds Types from this load context.
            domain = new ScriptDomain();
            domain.Load(result.OutputAssemblyPath);
            registry.Load(domain.Assembly!);
            scriptAsm = Path.GetFileNameWithoutExtension(result.OutputAssemblyPath);
        }

        if (rids.Count == 0) rids.Add(RuntimeInformation.RuntimeIdentifier);
        if (modes.Count == 0) modes.Add(ExportMode.SelfContained);
        outDir ??= Path.Combine(projDir, "export");

        var input = new ExportInput(
            projectPath,
            project,
            registry,
            scriptAsm
        );
        var options = new ExportOptions(Path.GetFullPath(outDir), rids, modes.Distinct().ToList());
        var ok = await GameExporter.ExportAsync(input, options, new ConsoleProgress());
        GC.KeepAlive(domain);
        return ok ? 0 : 1;
    }

    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value)
        {
            Console.WriteLine(value);
        }
    }
}