using System.Diagnostics;

namespace Zigote.Cli;

/// <summary>
///     Running other programs, which is most of what this tool does.
///     <para>
///         Every verb here is a front-end over adb, simctl, appimagetool or the .NET SDK, so
///         "start a process and wait" and "read a short tool's stdout" were written four and three
///         times respectively before this file existed. The two rules worth keeping in one place:
///         a tool that cannot be started is a <see cref="CliError" /> naming it, not an unhandled
///         exception, and a probe that fails is <c>null</c> rather than a throw — `doctor` asks
///         questions whose answer is legitimately "that is not installed".
///     </para>
/// </summary>
internal static class Shell
{
    /// <summary>
    ///     stdout of a short-lived tool, or null when it is absent, fails, or hangs.
    ///     <para>
    ///         The timeout is not decoration: every caller here is a probe (`adb devices`,
    ///         `dotnet --version`, `simctl list`), and a wedged probe wedges the verb that asked —
    ///         a doctor that hangs is worse than the problems it diagnoses, and a `run` that never
    ///         reaches the device list looks like a broken tool.
    ///     </para>
    /// </summary>
    internal static string? Capture(
        string file,
        string[] arguments,
        bool requireSuccess = false,
        int timeoutMilliseconds = 30_000
    )
    {
        try
        {
            var start = new ProcessStartInfo(file) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return requireSuccess && process.ExitCode != 0 ? null : output;
        }
        catch (Exception)
        {
            return
                null; // not installed, not on PATH — a question whose answer is legitimately "no"
        }
    }

    /// <summary>A process start info with the arguments already in it — no quoting to get wrong.</summary>
    internal static ProcessStartInfo Command(string file, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>Run it to completion, inheriting this console, and return its exit code.</summary>
    internal static int Wait(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new CliError(NotStarted(start.FileName));
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>The SDK, the tool nearly every verb ends up calling.</summary>
    internal static int Dotnet(IEnumerable<string> arguments) =>
        Wait(Command(file: "dotnet", arguments: arguments));

    private static string NotStarted(string file) =>
        file == "dotnet"
            ? "could not start dotnet — is the .NET SDK on PATH?"
            : $"could not start {file}.";
}

/// <summary>
///     Finding the projects a verb acts on.
///     <para>
///         Found rather than asked for, everywhere: the app is the directory you are standing in.
///         That is a choice with a cost — getting it wrong silently builds the wrong thing — so the
///         rule is the same in every verb: exactly one match is used, none and several are both
///         errors that say what to do about it.
///     </para>
/// </summary>
internal static class Projects
{
    /// <summary>
    ///     The shared app project a head should compile. Looks for a single non-head csproj at the
    ///     top of the tree, then one directory down — the layout `create` produces.
    /// </summary>
    internal static string App(string root)
    {
        var candidates = Directory
            .EnumerateFiles(
                path: root,
                searchPattern: "*.csproj",
                searchOption: SearchOption.TopDirectoryOnly
            )
            .Concat(
                Directory
                    .EnumerateDirectories(root)
                    .Where(d => !Path.GetFileName(d).StartsWith('.'))
                    .SelectMany(d => Directory.EnumerateFiles(
                            path: d,
                            searchPattern: "*.csproj",
                            searchOption: SearchOption.TopDirectoryOnly
                        )
                    )
            )
            .Where(p => !IsHead(p))
            .ToList();

        return candidates.Count switch {
            0 => throw new CliError(
                $"no app project found under '{root}'. Run this from inside the app, or pass --dir."
            ),
            1 => candidates[0],
            _ => throw new CliError(
                "more than one app project here, so which one the head belongs to is ambiguous: " +
                string.Join(separator: ", ", values: candidates.Select(Path.GetFileName)) +
                ". Run from inside one of them."
            ),
        };
    }

    /// <summary>The single platform head of a kind (".Android", ".iOS"), or null when there is none.</summary>
    internal static string? Head(string root, string suffix)
    {
        var heads = Directory
            .EnumerateFiles(
                path: root,
                searchPattern: "*.csproj",
                searchOption: SearchOption.AllDirectories
            )
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
            )
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            )
            .Where(p => Path.GetFileNameWithoutExtension(p)
                .EndsWith(value: suffix, comparisonType: StringComparison.Ordinal)
            )
            .ToList();

        return heads.Count switch {
            1 => heads[0],
            0 => null,
            _ => throw new CliError(
                $"more than one {suffix} head here: " +
                string.Join(separator: ", ", values: heads.Select(Path.GetFileName)) +
                ". Run from inside one of them, or pass --dir."
            ),
        };
    }

    /// <summary>The head, or an error that names the command which would create it.</summary>
    internal static string RequireHead(string root, string suffix, string create) =>
        Head(root: root, suffix: suffix)
        ?? throw new CliError($"no {suffix.TrimStart('.')} head under '{root}'. {create}");

    private static bool IsHead(string project) =>
        Path.GetFileNameWithoutExtension(project) is var name
        && (name.EndsWith(value: ".Android", comparisonType: StringComparison.Ordinal)
            || name.EndsWith(value: ".iOS", comparisonType: StringComparison.Ordinal));
}
