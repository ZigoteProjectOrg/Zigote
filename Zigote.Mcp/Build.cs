using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zigote.Mcp;

/// <summary>
///     <c>build</c> and <c>doctor</c>: the steps before there is an app to talk to. Both run a
///     process and hand back only what the model acts on — a compile error as
///     <c>file:line code message</c>, an environment problem with its fix — never the MSBuild
///     chatter around it, which is most of the tokens and none of the information.
/// </summary>
public static partial class Tools
{
    private const int MaxDiagnostics = 40;

    // MSBuild's canonical format — `path(line,col): error CS0103: message [project]` — with the
    // location optional (NETSDK and MSB errors have none), and the `path:line:col: error: message`
    // shape zig and clang print from the native build's Exec steps.
    private static readonly Regex MsbuildLine = new(
        @"^\s*(?:(?<file>[^\s(][^(]*?)(?:\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\))?\s*:\s*)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.Compiled
    );

    private static readonly Regex NativeLine = new(
        @"^\s*(?<file>[^\s:][^:]*):(?<line>\d+):(?<col>\d+):\s*(?<sev>error|warning):\s*(?<msg>.*)$",
        RegexOptions.Compiled
    );

    private sealed record Diagnostic(string Sev, string Code, string Where, string Msg);

    private static JsonObject Build(JsonObject args)
    {
        var argv = new List<string> {
            "build", StrOpt(args: args, name: "project") ?? ".",
            "-nologo", "-v", "q", "-tl:off", "-clp:NoSummary", "-p:GenerateFullPaths=true",
        };
        if (StrOpt(args: args, name: "configuration") is { } config) argv.AddRange(["-c", config]);
        foreach (var extra in args["args"] as JsonArray ?? []) argv.Add(extra!.GetValue<string>());

        var clock = Stopwatch.StartNew();
        (int exit, var lines) = Exec(
            file: AppHost.DotnetHost(),
            args: argv,
            timeoutSeconds: Int(args: args, name: "timeout_seconds") ?? 600
        );
        string verdict = exit == 0 ? "build ok" : $"build FAILED (exit {exit})";
        string report = Diagnostics(lines: lines, listWarnings: Flag(args: args, name: "warnings"))
                        ?? (exit == 0 ? "no warnings" : "no diagnostics parsed — output tail:\n" +
                            string.Join('\n', lines.Where(l => l.Trim().Length > 0).TakeLast(30)));
        return Text($"{verdict} in {clock.Elapsed.TotalSeconds:0.0}s\n{report}");
    }

    /// <summary>
    ///     Build output reduced to its diagnostics: errors in full, warnings counted by code (or
    ///     listed when asked), each once — a multi-targeted project reports every one per target.
    ///     Null when there are none, so a caller can fall back to the raw tail.
    /// </summary>
    public static string? Diagnostics(IEnumerable<string> lines, bool listWarnings = false)
    {
        string cwd = Environment.CurrentDirectory + Path.DirectorySeparatorChar;
        var found = new List<Diagnostic>();
        foreach (string line in lines)
        {
            var m = MsbuildLine.Match(line);
            if (!m.Success) m = NativeLine.Match(line);
            if (!m.Success) continue;
            string file = m.Groups["file"].Value.Trim();
            if (file.StartsWith(cwd)) file = file[cwd.Length..];
            string where = file.Length == 0 ? "" :
                m.Groups["col"].Success ? $"{file}:{m.Groups["line"].Value}:{m.Groups["col"].Value}" :
                m.Groups["line"].Success ? $"{file}:{m.Groups["line"].Value}" : file;
            string msg = m.Groups["msg"].Value;
            found.Add(new Diagnostic(m.Groups["sev"].Value,
                m.Groups["code"].Success && m.Groups["code"].Value.Length > 0 ? m.Groups["code"].Value : "native",
                where, msg.Length > 300 ? msg[..299] + "…" : msg));
        }

        if (found.Count == 0) return null;
        var unique = found.Distinct().ToList();
        var errors = unique.Where(d => d.Sev == "error").ToList();
        var warnings = unique.Where(d => d.Sev == "warning").ToList();

        var s = new StringBuilder($"{errors.Count} error(s), {warnings.Count} warning(s)");
        foreach (var d in errors.Take(MaxDiagnostics)) s.Append($"\nerror {d.Code} {d.Where} {d.Msg}");
        if (errors.Count > MaxDiagnostics) s.Append($"\n… {errors.Count - MaxDiagnostics} more errors");
        if (warnings.Count == 0) return s.ToString();

        if (listWarnings)
        {
            foreach (var d in warnings.Take(MaxDiagnostics)) s.Append($"\nwarning {d.Code} {d.Where} {d.Msg}");
            if (warnings.Count > MaxDiagnostics) s.Append($"\n… {warnings.Count - MaxDiagnostics} more warnings");
        }
        else
        {
            s.Append("\nwarnings by code: ").AppendJoin(", ", warnings.GroupBy(d => d.Code)
                .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}"));
        }

        return s.ToString();
    }

    private static JsonObject Doctor(JsonObject args)
    {
        // The CLI owns the checks (Zigote.Cli/Doctor.cs); this only finds it and condenses the
        // JSON. From a checkout that is the project next to this server's own; elsewhere, an
        // installed `zigote`.
        string? cli = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && cli is null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Zigote.Cli", "Zigote.Cli.csproj");
            if (File.Exists(candidate)) cli = candidate;
        }

        string[] doctor = ["doctor", "--json", "--dir", StrOpt(args: args, name: "dir") ?? Environment.CurrentDirectory];
        (_, var lines) = cli is not null
            ? Exec(file: AppHost.DotnetHost(), args: ["run", "-v", "q", "--project", cli, "--", .. doctor], timeoutSeconds: 180)
            : Exec(file: "zigote", args: doctor, timeoutSeconds: 180);

        var ok = new List<string>();
        var s = new StringBuilder();
        foreach (string line in lines)
        {
            if (!line.StartsWith('{') || Parse(line) is not JsonObject check) continue;
            string status = S(check, "status") ?? "?", title = S(check, "title") ?? "";
            if (status == "ok")
            {
                ok.Add(title);
                continue;
            }

            s.Append($"\n{status} {title}");
            if (S(check, "fix") is { } fix) s.Append($" — {fix}");
            if (check["fixCommand"] is JsonArray cmd) s.Append($" (run: {string.Join(' ', cmd)})");
        }

        if (ok.Count == 0 && s.Length == 0)
            throw new ToolError("doctor printed no checks:\n" + string.Join('\n', lines.TakeLast(20)));
        return Text($"ok: {string.Join(" · ", ok)}{s}");
    }

    /// <summary>Run to completion, both pipes merged in arrival order; killed (tree) at the deadline.</summary>
    private static (int Exit, List<string> Lines) Exec(string file, IEnumerable<string> args, int timeoutSeconds)
    {
        var start = new ProcessStartInfo(file) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) start.ArgumentList.Add(a);

        var lines = new List<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } l) lock (lines) lines.Add(l); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } l) lock (lines) lines.Add(l); };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ToolError($"could not start {file}: {e.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            process.Kill(entireProcessTree: true);
            throw new ToolError($"{Path.GetFileName(file)} {string.Join(' ', args.Take(2))} still running after " +
                                $"{timeoutSeconds}s — killed. Raise timeout_seconds for a cold native build.");
        }

        process.WaitForExit(); // flushes the async readers
        return (process.ExitCode, lines);
    }
}
