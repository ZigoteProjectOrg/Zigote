using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Zigote.Mcp;

/// <summary>
///     The app side of the bridge: launches Zigote apps with <c>ZIGOTE_INSPECT=0</c>, reads the
///     port they announce, and speaks the one-line inspect protocol to whichever app a tool
///     addresses. Also remembers the last launched app so tools can omit <c>port</c> entirely —
///     the common session is "launch, then poke at that one app".
///     <para>
///         Launched apps keep a rolling log of everything they print, surfaced by the
///         <c>logs</c> tool — and kept after the app exits, because the output of a crashed app
///         is the one artifact worth reading. Under <c>watch</c> mode a rude edit restarts the
///         process, which re-announces a fresh port; the drain thread keeps scanning and updates
///         the app's port, so "the last launched app" keeps meaning the same app across reloads.
///     </para>
/// </summary>
public static class AppHost
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private const int LogLines = 400;

    // "zigote inspect: 127.0.0.1:PORT" — printed by InspectServer.Start exactly for launchers.
    private static readonly Regex PortLine = new(@"zigote inspect: 127\.0\.0\.1:(\d+)", RegexOptions.Compiled);

    private sealed class LaunchedApp(Process process)
    {
        public Process Process { get; } = process;
        public int Port; // updated in place when a watch restart announces a new one
        public readonly Queue<string> Log = [];
    }

    private static readonly List<LaunchedApp> Apps = [];
    private static readonly Lock Gate = new();

    /// <summary>
    ///     Send one command to the app at <paramref name="port" /> and return its one-line JSON
    ///     reply. The server answers and closes, so read-to-end is the whole reply.
    /// </summary>
    public static string Query(int port, string command)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = (int)QueryTimeout.TotalMilliseconds;
            client.SendTimeout = (int)QueryTimeout.TotalMilliseconds;

            using var stream = client.GetStream();
            var request = Encoding.UTF8.GetBytes(command + "\n");
            stream.Write(request);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Trim();
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            throw new ToolError(
                $"no Zigote app answered on port {port} ({e.Message}). Launch one with the " +
                "`launch` tool, or start it with ZIGOTE_INSPECT=0 and pass the port it prints.");
        }
    }

    /// <summary>The port a tool call addresses: the explicit one, else the last app still alive.</summary>
    public static int Resolve(int? port)
    {
        if (port is { } p) return p;

        lock (Gate)
        {
            for (var i = Apps.Count - 1; i >= 0; i--)
                if (!Apps[i].Process.HasExited)
                    return Apps[i].Port;

            if (Apps.Count > 0)
                throw new ToolError(
                    "every app launched in this session has exited — `logs` has their last " +
                    "output, which usually says why");
        }

        throw new ToolError(
            "no app to talk to: nothing launched in this session and no `port` given. Use the " +
            "`launch` tool first, or pass the port of an app started with ZIGOTE_INSPECT=0.");
    }

    /// <summary>
    ///     <c>dotnet run</c> (or <c>dotnet watch run</c>) the project with the inspect socket on,
    ///     wait for the announced port, and remember the process. <c>-v q --nologo</c> keeps the
    ///     build's own chatter out of the stdout being scanned — the same trick Zigote.Cli uses
    ///     for the preview target list.
    /// </summary>
    public static (int Port, int Pid) Launch(string project, string? preview, bool watch, int waitSeconds)
    {
        var start = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The watch verb mirrors `zigote preview` (Zigote.Cli/Preview.cs): dotnet watch is what
        // turns a running app into a hot-reloading one — save a file, the widget rebuilds.
        string[] verb = watch
            ? ["watch", "run", "--non-interactive"]
            : ["run", "-v", "q", "--nologo"];
        foreach (var a in verb) start.ArgumentList.Add(a);
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.Environment["ZIGOTE_INSPECT"] = "0"; // 0 = any free port, announced on stdout
        if (preview is { Length: > 0 }) start.Environment["ZIGOTE_PREVIEW"] = preview;

        var process = Process.Start(start)
                      ?? throw new ToolError("could not start dotnet — is the .NET SDK on PATH?");
        var app = new LaunchedApp(process);

        // Drain both pipes for the process's whole life — a full pipe blocks the app — into the
        // rolling log, watching every line for a port announcement: the first one completes the
        // launch, later ones are watch restarts moving the app to a fresh port.
        var announced = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Drain(StreamReader pipe)
        {
            new Thread(() =>
            {
                while (pipe.ReadLine() is { } line)
                {
                    lock (app.Log)
                    {
                        app.Log.Enqueue(line);
                        while (app.Log.Count > LogLines) app.Log.Dequeue();
                    }

                    if (PortLine.Match(line) is { Success: true } m)
                    {
                        var port = int.Parse(m.Groups[1].Value);
                        lock (Gate) app.Port = port;
                        announced.TrySetResult(port);
                    }
                }
            }) { IsBackground = true, Name = "zigote-mcp-drain" }.Start();
        }

        Drain(process.StandardOutput);
        Drain(process.StandardError);

        // Whichever comes first: the port, the process dying, or the caller's patience. A cold
        // build of an app that drags the native engine in can legitimately take minutes, which is
        // why the wait is a parameter rather than a constant.
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (!announced.Task.IsCompleted)
        {
            if (process.HasExited)
                throw new ToolError(
                    $"the app exited (code {process.ExitCode}) before announcing an inspect port. " +
                    $"Recent output:\n{Tail(app, 40)}");
            if (DateTime.UtcNow > deadline)
            {
                Kill(process);
                throw new ToolError(
                    $"no inspect port announced within {waitSeconds}s — the build may still be " +
                    $"running (raise wait_seconds) or the app never started. Recent output:\n{Tail(app, 40)}");
            }

            Thread.Sleep(100);
        }

        lock (Gate) Apps.Add(app);
        return (announced.Task.Result, process.Id);
    }

    /// <summary>
    ///     The last lines a launched app printed — build errors, DebugLog output, unhandled
    ///     exceptions. By pid, or the most recent launch; exited apps keep their log until
    ///     stopped, because a crash's output is read after the crash.
    /// </summary>
    public static string Logs(int? pid, int lines)
    {
        LaunchedApp app;
        lock (Gate)
        {
            app = (pid is { } p ? Apps.LastOrDefault(a => a.Process.Id == p) : Apps.LastOrDefault())
                  ?? throw new ToolError(pid is { } q
                      ? $"pid {q} is not an app this server launched"
                      : "nothing launched in this session — logs only exist for apps `launch` started");
        }

        var state = app.Process.HasExited ? $"exited (code {app.Process.ExitCode})" : "running";
        return $"pid {app.Process.Id}, {state}, port {app.Port}\n{Tail(app, lines)}";
    }

    /// <summary>Stop one launched app by pid, or every app this server launched.</summary>
    public static string Stop(int? pid)
    {
        lock (Gate)
        {
            var targets = pid is { } p ? Apps.Where(a => a.Process.Id == p).ToList() : [.. Apps];
            if (targets.Count == 0)
                throw new ToolError(pid is { } q
                    ? $"pid {q} is not an app this server launched"
                    : "nothing launched in this session — apps you started yourself are yours to stop");

            foreach (var app in targets)
            {
                Kill(app.Process);
                Apps.Remove(app);
            }

            return $"stopped {targets.Count} app(s)";
        }
    }

    public static void StopAll()
    {
        lock (Gate)
        {
            foreach (var app in Apps) Kill(app.Process);
            Apps.Clear();
        }
    }

    private static void Kill(Process process)
    {
        // The whole tree: `dotnet run` is a launcher whose child owns the window.
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill — the desired state either way.
        }
    }

    private static string Tail(LaunchedApp app, int lines)
    {
        lock (app.Log)
        {
            if (app.Log.Count == 0) return "(no output)";
            return string.Join('\n', app.Log.TakeLast(Math.Clamp(lines, 1, LogLines)));
        }
    }
}
