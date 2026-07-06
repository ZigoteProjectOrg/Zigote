namespace Zigote.Core.Diagnostics;

/// <summary>Outcome of a <see cref="DebugCommand" />. <see cref="Message" /> is echoed to the console.</summary>
public readonly record struct DebugCommandResult(bool Ok, string Message)
{
    public static DebugCommandResult Success(string message = "")
    {
        return new DebugCommandResult(true, message);
    }

    public static DebugCommandResult Failure(string message)
    {
        return new DebugCommandResult(false, message);
    }
}

/// <summary>
///     A named runtime action (design doc §12). Registration sites bind the closure; the console and
///     debug-menu buttons both dispatch through it.
/// </summary>
public sealed class DebugCommand
{
    public required string Name { get; init; }
    public string Category { get; init; } = "general";
    public string Description { get; init; } = "";

    /// <summary>One-line usage hint, e.g. <c>"set &lt;name&gt; &lt;value&gt;"</c>.</summary>
    public string? Usage { get; init; }

    public required Func<string[], DebugCommandResult> Execute { get; init; }
}

/// <summary>
///     Greenfield registry + parser for debug commands (design doc §12, §9.14). Process-wide. Commands
///     are registered by whichever layer owns the action (Core/UI/Editor) and executed from the
///     in-menu
///     console. <see cref="Execute" /> tokenises the input, dispatches, logs the result to
///     <see cref="DebugLog" />, and returns it for the console to display.
/// </summary>
public static class DebugCommands
{
    private static readonly Dictionary<string, DebugCommand> Map = new(
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly List<DebugCommand> Ordered = [];
    private static readonly List<string> HistoryList = [];

    public static int Version { get; private set; }

    public static IReadOnlyList<DebugCommand> All => Ordered;
    public static IReadOnlyList<string> History => HistoryList;

    public static void Register(DebugCommand cmd)
    {
        if (Map.TryGetValue(cmd.Name, out var existing)) Ordered.Remove(existing);
        Map[cmd.Name] = cmd;
        Ordered.Add(cmd);
        Version++;
    }

    public static void Register(string name, string description,
        Func<string[], DebugCommandResult> execute,
        string category = "general", string? usage = null)
    {
        Register(
            new DebugCommand {
                Name = name,
                Description = description,
                Execute = execute,
                Category = category,
                Usage = usage,
            }
        );
    }

    /// <summary>Register a zero-argument action command.</summary>
    public static void Register(string name, string description, Action action,
        string category = "general")
    {
        Register(
            new DebugCommand {
                Name = name,
                Description = description,
                Category = category,
                Execute = _ =>
                {
                    action();
                    return DebugCommandResult.Success();
                },
            }
        );
    }

    public static DebugCommand? Find(string name)
    {
        return Map.GetValueOrDefault(name);
    }

    /// <summary>Command names beginning with <paramref name="prefix" /> (for auto-complete), sorted.</summary>
    public static List<string> Complete(string prefix)
    {
        var list = new List<string>();
        foreach (var c in Ordered)
            if (c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                list.Add(c.Name);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static DebugCommandResult Execute(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return DebugCommandResult.Success();

        if (HistoryList.Count == 0 || HistoryList[^1] != input)
        {
            HistoryList.Add(input);
            if (HistoryList.Count > 200) HistoryList.RemoveRange(0, HistoryList.Count - 200);
        }

        var tokens = Tokenize(input);
        if (tokens.Count == 0) return DebugCommandResult.Success();

        var name = tokens[0];
        var cmd = Find(name);
        DebugCommandResult result;
        if (cmd is null)
            result = DebugCommandResult.Failure($"Unknown command '{name}'. Type 'help'.");
        else
            try
            {
                result = cmd.Execute(tokens.GetRange(1, tokens.Count - 1).ToArray());
            }
            catch (Exception ex)
            {
                result = DebugCommandResult.Failure($"{name}: {ex.Message}");
            }

        DebugLog.Add(
            result.Ok ? DebugLogLevel.Info : DebugLogLevel.Error,
            $"> {input}" + (string.IsNullOrEmpty(result.Message) ? "" : $"  →  {result.Message}"),
            "console"
        );
        return result;
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            if (i >= input.Length) break;
            if (input[i] == '"')
            {
                i++;
                var start = i;
                while (i < input.Length && input[i] != '"') i++;
                tokens.Add(input[start..i]);
                if (i < input.Length) i++; // closing quote
            }
            else
            {
                var start = i;
                while (i < input.Length && !char.IsWhiteSpace(input[i])) i++;
                tokens.Add(input[start..i]);
            }
        }

        return tokens;
    }

    /// <summary>
    ///     Register the engine-neutral built-ins (help / get / set / vars / clear / log).
    ///     Idempotent. Higher layers add their own (renderer / app / editor) commands on top.
    /// </summary>
    public static void RegisterCoreDefaults()
    {
        Register(
            "help",
            "List commands, or show usage for one",
            args =>
            {
                if (args.Length > 0)
                {
                    var c = Find(args[0]);
                    return c is null
                        ? DebugCommandResult.Failure($"Unknown command '{args[0]}'")
                        : DebugCommandResult.Success(
                            $"{c.Name} — {c.Description}{(c.Usage is null ? "" : $"  ({c.Usage})")}"
                        );
                }

                var names = new List<string>();
                foreach (var c in Ordered) names.Add(c.Name);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return DebugCommandResult.Success(string.Join(", ", names));
            },
            "console",
            "help [command]"
        );

        Register(
            "get",
            "Read a debug variable",
            args =>
            {
                if (args.Length < 1) return DebugCommandResult.Failure("usage: get <name>");
                var v = DebugVariables.Find(args[0]);
                return v is null
                    ? DebugCommandResult.Failure($"No variable '{args[0]}'")
                    : DebugCommandResult.Success($"{v.Name} = {v.Display()}");
            },
            "console",
            "get <name>"
        );

        Register(
            "set",
            "Write a debug variable",
            args =>
            {
                if (args.Length < 2) return DebugCommandResult.Failure("usage: set <name> <value>");
                var v = DebugVariables.Find(args[0]);
                if (v is null) return DebugCommandResult.Failure($"No variable '{args[0]}'");
                var err = v.TrySet(
                    string.Join(
                        ' ',
                        args,
                        1,
                        args.Length - 1
                    )
                );
                return err is null
                    ? DebugCommandResult.Success($"{v.Name} = {v.Display()}")
                    : DebugCommandResult.Failure(err);
            },
            "console",
            "set <name> <value>"
        );

        Register(
            "vars",
            "List debug variables (optionally filtered)",
            args =>
            {
                var filter = args.Length > 0 ? args[0] : null;
                var lines = new List<string>();
                foreach (var v in DebugVariables.All)
                    if (filter is null || v.Name.Contains(
                            filter,
                            StringComparison.OrdinalIgnoreCase
                        ))
                        lines.Add($"{v.Name} = {v.Display()}");
                return DebugCommandResult.Success(
                    lines.Count == 0 ? "(none)" : string.Join(", ", lines)
                );
            },
            "console",
            "vars [filter]"
        );

        Register(
            "clear",
            "Clear the log buffer",
            DebugLog.Clear,
            "console"
        );

        Register(
            "log",
            "Append a line to the log",
            args =>
            {
                DebugLog.Info(string.Join(' ', args), "console");
                return DebugCommandResult.Success();
            },
            "console",
            "log <message>"
        );
    }
}