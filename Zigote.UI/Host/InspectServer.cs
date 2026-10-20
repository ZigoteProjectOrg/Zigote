using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Diagnostics;
using Zigote.Core.Events;
using Zigote.UI.Debug;
using Zigote.UI.Semantics;
using Zigote.UI.Widgets;

namespace Zigote.UI.Host;

/// <summary>
///     Hands the live widget tree, the accessibility tree and the preview targets to whatever asks,
///     over
///     a loopback socket.
///     <para>
///         This is what the IDE panels read, and — like <see cref="WidgetPreview" /> — it is
///         deliberately
///         not an IDE feature. It is a socket of one-word commands, so a Rider tool window, a
///         terminal (<c>echo widgets | nc 127.0.0.1 $port</c>), a test or a script all get the same
///         data
///         with nothing installed. Nothing here knows an editor exists.
///     </para>
///     <para>
///         Off unless <c>ZIGOTE_INSPECT</c> is set to a port — <c>0</c> asks the OS for a free one,
///         which
///         is then printed as <c>zigote inspect: 127.0.0.1:PORT</c> for the launcher to read. Bound to
///         loopback only: this exposes an app's entire UI state, so it must never be reachable off the
///         machine.
///     </para>
///     <list type="bullet">
///         <item>
///             <c>widgets</c> — the widget tree, from <see cref="WidgetDebug" />, as the inspector
///             sees it.
///         </item>
///         <item><c>semantics</c> — the accessibility tree, freshly built.</item>
///         <item><c>targets</c> — what <see cref="WidgetPreview" /> could show.</item>
///         <item>
///             <c>previews</c> — the same, with each target's <see cref="PreviewAttribute" /> and the
///             properties it takes.
///         </item>
///         <item>
///             <c>preview &lt;Type&gt;[?prop=value&amp;…]</c> — swap the shown widget, with its
///             properties set, without restarting.
///         </item>
///         <item><c>shot [scale]</c> — the current frame as a base64 BMP.</item>
///         <item><c>stream [scale] [fps]</c> — frames pushed as they change, length-prefixed binary.</item>
///         <item>
///             <c>input …</c> — synthetic pointer/scroll/key/text events; see
///             <see cref="ParseInput" />.
///         </item>
///         <item><c>size WxH</c> | <c>size window</c> — lay the tree out at a device size.</item>
///         <item><c>theme dark|light</c> — swap the app theme.</item>
///         <item><c>locales</c> — the active locale and the ones the app supports.</item>
///         <item>
///             <c>locale &lt;tag&gt;</c> — switch the app's locale (needs a
///             <c>LocalizationsScope</c>).
///         </item>
///         <item><c>windows</c> — every open OS window: id, title, layout size, which is main.</item>
///         <item>
///             <c>@ID &lt;command&gt;</c> — run <c>widgets</c>, <c>semantics</c>, <c>shot</c>,
///             <c>input</c>, <c>props</c>, <c>hit</c> or <c>info</c> against that window instead
///             of the main one. Other commands are app-wide and ignore the prefix.
///         </item>
///         <item><c>window hide|show</c> — the app's own window, which a preview does not want.</item>
///         <item><c>window size WxH</c> — really resize the OS window (and its framebuffer).</item>
///         <item><c>props ID</c> — one widget's properties, by the id the tree reports.</item>
///         <item>
///             <c>hit X Y</c> — what a click at that point would land on, and the chain above it.
///         </item>
///         <item>
///             <c>info</c> — layout size, theme, locale, focus, hover, overlays, tree size and
///             whether anything is animating, as one JSON object.
///         </item>
///         <item>
///             <c>stats</c> — the frame/CPU/memory sample <c>DebugStats</c> keeps, as one JSON
///             object.
///         </item>
///     </list>
///     <para>
///         Every command is answered on the UI thread through <see cref="App.Post" /> and the socket
///         thread waits for the result: the widget tree may only be walked while layout is not
///         running,
///         and a snapshot taken from another thread would be a torn one.
///     </para>
/// </summary>
public static class InspectServer
{
    // A tree big enough to hit these is a runaway, not a UI; the caps keep one bad frame from turning
    // into a multi-megabyte response the panel then has to parse.
    private const int MaxNodes = 20_000;

    // Depth is widget nesting, not JSON nesting (each widget is an object plus a children array,
    // so a reader needs twice this). Composition-heavy pages pass 64 without being runaways —
    // a gallery page under a navigator, a split view and a scroller is ~90 deep.
    private const int MaxDepth = 256;

    // Long enough to cover a slow frame under a debugger, short enough that a wedged app fails the
    // request instead of hanging the panel.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Start listening if <c>ZIGOTE_INSPECT</c> asks for it. <paramref name="setPreview" /> is invoked
    ///     on the UI thread to swap the shown widget; null disables the <c>preview</c> command.
    /// </summary>
    public static void Start(App app, Action<string>? setPreview = null,
        Func<string, bool>? setTheme = null)
    {
        string? setting = Environment.GetEnvironmentVariable("ZIGOTE_INSPECT");
        if (setting is not { Length: > 0 } || !int.TryParse(s: setting, result: out int port) ||
            port is < 0 or > 65535)
            return;

        // A requested port can already be taken — the launcher picked it a moment earlier and something
        // else got in first. Falling back to an OS-chosen one and announcing it is always better than
        // running with no socket at all: the caller polling the port it asked for will time out, but a
        // caller reading this line still finds the app. Running portless looks identical to a hung app.
        TcpListener listener;
        try
        {
            listener = new TcpListener(localaddr: IPAddress.Loopback, port: port);
            listener.Start();
        }
        catch (SocketException first) when (port != 0)
        {
            DebugLog.Error(
                $"zigote inspect: port {port} is taken ({first.Message}); picking another"
            );
            try
            {
                listener = new TcpListener(localaddr: IPAddress.Loopback, port: 0);
                listener.Start();
            }
            catch (SocketException e)
            {
                DebugLog.Error($"zigote inspect: cannot listen at all — {e.Message}");
                return;
            }
        }
        catch (SocketException e)
        {
            DebugLog.Error($"zigote inspect: cannot listen — {e.Message}");
            return;
        }

        int actual = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.Out.WriteLine($"zigote inspect: 127.0.0.1:{actual}");
        Console.Out.Flush();

        // Background so the process still exits when the window closes, with the listener along with it.
        new Thread(() => Serve(
                listener: listener,
                app: app,
                setPreview: setPreview,
                setTheme: setTheme
            )
        ) {
            IsBackground = true,
            Name = "zigote-inspect",
        }.Start();
    }

    private static void Serve(TcpListener listener, App app, Action<string>? setPreview,
        Func<string, bool>? setTheme)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return; // listener closed — the app is going away
            }

            // One connection, one thread: `stream` holds its connection open for as long as the
            // panel watches, and a single serial loop would let one stream starve every other
            // command. Plain threads rather than the pool — a stream blocks for minutes, which is
            // exactly what pool threads are not for, and there are at most a few clients ever.
            new Thread(() =>
                {
                    using (client)
                    {
                        try
                        {
                            Handle(
                                client: client,
                                app: app,
                                setPreview: setPreview,
                                setTheme: setTheme
                            );
                        }
                        catch (IOException)
                        {
                            // The panel closed the socket mid-answer; nothing to do and nothing worth logging.
                        }
                    }
                }
            ) {
                IsBackground = true,
                Name = "zigote-inspect-client",
            }.Start();
        }
    }

    private static void Handle(TcpClient client, App app, Action<string>? setPreview,
        Func<string, bool>? setTheme)
    {
        using var stream = client.GetStream();
        stream.ReadTimeout = (int)Timeout.TotalMilliseconds;
        using var reader = new StreamReader(
            stream: stream,
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true
        );
        using var writer =
            new StreamWriter(
                stream: stream,
                encoding: new UTF8Encoding(false)
            ) { AutoFlush = true };

        string? line = reader.ReadLine();
        if (line is null) return;

        // `@ID command …` addresses one OS window by the id `windows` reports; without it, the
        // main window. Only the verbs that look at or poke a window's own tree honour it (see
        // PerWindow) — theme, locale, preview and the rest are app-wide whichever window is named.
        uint windowId = 0;
        line = line.TrimStart();
        if (line.StartsWith('@'))
        {
            int end = line.IndexOf(' ');
            if (!uint.TryParse(s: end < 0 ? line[1..] : line[1..end], result: out windowId))
            {
                writer.Write(Error("a window is addressed as @ID, with an id from `windows`"));
                writer.Write('\n');
                return;
            }

            line = end < 0 ? "" : line[(end + 1)..];
        }

        int space = line.IndexOf(' ');
        string command = (space < 0 ? line : line[..space]).Trim();
        string argument = space < 0 ? "" : line[(space + 1)..].Trim();

        // `stream` takes the connection over and never returns until the client hangs up.
        if (command == "stream")
        {
            StreamFrames(
                stream: stream,
                writer: writer,
                app: app,
                argument: argument
            );
            return;
        }

        // `profile` spans many frames, so it cannot be answered inside one UI-thread hop: it starts
        // a Profiler capture there, then waits here on the socket thread for the flush.
        if (command == "profile")
        {
            writer.Write(ProfileCommand(app: app, argument: argument));
            writer.Write('\n');
            return;
        }

        // `shot` and `input` run at the END of a frame, everything else at the top. A shot answered
        // at the top would show the previous frame — and one sent right after an `input` click could
        // drain in the same batch as the click, before it even dispatched, missing the dialog it
        // opened. `input` rides the same quiet queue because its post must not force a repaint: the
        // injected events go through the real dispatch pipeline next frame, which repaints exactly as
        // much as the same OS input would — a move flood over nothing repaints nothing.
        // Always posted to the MAIN app, whichever window is addressed: its frame is the one that
        // pumps every other window, so "top of frame" and "end of frame" mean the same for all of
        // them, and the window list is only safe to read on that thread.
        string Work()
        {
            var target = app;
            if (windowId != 0 && PerWindow(command) &&
                (target = FindWindow(app: app, id: windowId)) is null)
                return Error($"no window @{windowId} — `windows` lists the open ones");
            return Answer(
                app: target,
                command: command,
                argument: argument,
                setPreview: setPreview,
                setTheme: setTheme
            );
        }

        string answer = OnUiThread(app: app, work: Work, afterFrame: command is "shot" or "input");
        writer.Write(answer);
        writer.Write('\n');
    }

    /// <summary>The verbs that act on one window's tree, and so honour an <c>@ID</c> prefix.</summary>
    private static bool PerWindow(string command) =>
        command is "widgets" or "semantics" or "shot" or "input" or "props" or "hit" or "info";

    private static App? FindWindow(App app, uint id)
    {
        if (app.WindowId == id) return app;
        foreach (var w in app.SecondaryWindows)
            if (w.WindowId == id && w.IsOpen)
                return w;

        return null;
    }

    /// <summary>The <c>windows</c> reply: the main window first, then every open secondary one.</summary>
    private static string WindowsJson(App app)
    {
        var json = new StringBuilder("{\"windows\":[");
        WriteWindow(json: json, w: app, main: true);
        foreach (var w in app.SecondaryWindows)
        {
            if (!w.IsOpen) continue;
            json.Append(',');
            WriteWindow(json: json, w: w, main: false);
        }

        return json.Append("]}").ToString();

        static void WriteWindow(StringBuilder json, App w, bool main)
        {
            json.Append("{\"id\":").Append(w.WindowId).Append(",\"title\":");
            Quote(json: json, text: w.Title);
            json.Append(",\"main\":").Append(main ? "true" : "false");
            json.Append(",\"w\":").Append((uint)w.LayoutWidth);
            json.Append(",\"h\":").Append((uint)w.LayoutHeight).Append('}');
        }
    }

    private static string Answer(App app, string command, string argument,
        Action<string>? setPreview,
        Func<string, bool>? setTheme)
    {
        switch (command)
        {
            case "widgets":
                return WidgetTreeJson(app.Root);

            case "semantics":
                return SemanticsTreeJson(app.BuildSemantics());

            case "targets":
            {
                var json = new StringBuilder("{\"targets\":[");
                bool first = true;
                foreach (string name in WidgetPreview.Candidates())
                {
                    if (!first) json.Append(',');
                    Quote(json: json, text: name);
                    first = false;
                }

                return json.Append("]}").ToString();
            }

            case "previews":
                return PreviewsJson();

            case "preview" when setPreview is not null && argument.Length > 0:
                setPreview(argument);
                return "{\"ok\":true}";

            case "shot":
                return Shot(app: app, argument: argument);

            case "windows":
                return WindowsJson(app);

            case "size":
                return Size(app: app, argument: argument);

            case "theme" when setTheme is not null:
                return setTheme(argument) ? Ok(app) : Error($"unknown theme '{argument}'");

            case "locales":
                return LocalesJson(app.LocaleInfo?.Invoke());

            case "locale" when argument.Length > 0:
                if (app.SetLocale is null)
                {
                    return Error(
                        "this app has no LocalizationsScope, so there is no locale to switch"
                    );
                }

                return app.SetLocale(argument)
                    ? Ok(app)
                    : Error($"'{argument}' is not a locale tag");

            case "input":
            {
                if (ParseInput(argument) is not { } evt)
                {
                    return Error(
                        $"input wants down|up|move|scroll|touchdown|touchmove|touchup|keydown|keyup|text …, got '{argument}'"
                    );
                }

                app.InjectEvent(evt);
                return "{\"ok\":true}";
            }

            case "window" when argument.StartsWith("size "):
            {
                // The OS window itself, unlike `size`: the framebuffer and every GPU target
                // sized to it are rebuilt, through the same resize event a dragged edge sends.
                // The new size lands over the next frames, so the answer is only "requested".
                string[] parts = argument[5..].Split('x', 'X');
                if (parts.Length != 2 ||
                    !uint.TryParse(s: parts[0], result: out uint w) ||
                    !uint.TryParse(s: parts[1], result: out uint h) ||
                    w is < 1 or > 8192 || h is < 1 or > 8192)
                    return Error($"window size wants WIDTHxHEIGHT in 1..8192, got '{argument}'");

                // A device-size override would hide the very change being asked for.
                app.PreviewSize = null;
                return app.Engine.MainWindowSetSize(width: w, height: h)
                    ? "{\"ok\":true}"
                    : Error("the platform refused to resize the window");
            }

            case "window":
                // Preview mode hides the app's own window: two windows showing the same thing, one of
                // them laid out for a phone, is worse than none. The Dock tile goes with it on macOS,
                // where hiding a window leaves the app in the Dock and the ⌘-Tab switcher regardless.
                app.Engine.MainWindowSetVisible(argument != "hide");
                app.Engine.AppSetDockVisible(argument != "hide");
                return "{\"ok\":true}";

            case "props":
                return Props(app: app, argument: argument);

            case "stats":
                return StatsJson();

            case "hit":
                return Hit(app: app, argument: argument);

            case "text":
                return TextRuns(app: app, argument: argument);

            case "info":
                return InfoJson(app);

            default:
                return Error($"unknown command '{command}'");
        }
    }

    /// <summary>
    ///     The same targets as <c>targets</c>, with what <see cref="PreviewAttribute" /> said about them
    ///     and the properties each one takes — everything a panel needs to draw a list and a property
    ///     editor without knowing anything about C#.
    ///     <para>
    ///         A second command rather than a richer <c>targets</c>: that one is a list of strings in
    ///         every consumer that already exists, and breaking them to avoid one command name is a poor
    ///         trade. A client that gets <c>{"error":…}</c> here is talking to an older app and can fall
    ///         back to <c>targets</c>.
    ///     </para>
    /// </summary>
    internal static string PreviewsJson()
    {
        var json = new StringBuilder("{\"previews\":[");
        bool first = true;
        foreach (var target in WidgetPreview.Descriptors())
        {
            if (!first) json.Append(',');
            first = false;

            json.Append("{\"target\":");
            Quote(json: json, text: target.Target);
            json.Append(",\"label\":");
            Quote(json: json, text: target.Label);
            json.Append(",\"group\":");
            Quote(json: json, text: target.Group);
            json.Append(",\"annotated\":").Append(target.Annotated ? "true" : "false");
            json.Append(",\"w\":").Append(Round(target.Width));
            json.Append(",\"h\":").Append(Round(target.Height));
            json.Append(",\"theme\":");
            Quote(json: json, text: target.Theme);

            json.Append(",\"params\":[");
            bool firstParam = true;
            foreach (var parameter in target.Parameters)
            {
                if (!firstParam) json.Append(',');
                firstParam = false;

                json.Append("{\"name\":");
                Quote(json: json, text: parameter.Name);
                json.Append(",\"kind\":");
                Quote(json: json, text: parameter.Kind);
                json.Append(",\"value\":");
                Quote(json: json, text: parameter.Value);
                json.Append(",\"options\":[");
                for (int i = 0; i < parameter.Options.Count; i++)
                {
                    if (i > 0) json.Append(',');
                    Quote(json: json, text: parameter.Options[i]);
                }

                json.Append("]}");
            }

            json.Append("]}");
        }

        return json.Append("]}").ToString();
    }

    /// <summary>
    ///     <c>profile [frames] [path]</c> — capture the next N frames (default 120, max 600) of
    ///     <see cref="Profiler" /> scopes, write the Chrome-Trace JSON to <c>path</c> (default: a
    ///     file in the temp dir), and answer with the per-scope self/total table averaged per frame
    ///     — the from-outside version of the devtools Profiler panel's "hottest scopes". Blocks the
    ///     socket (not the app) until the capture flushes; frames keep turning even when the app is
    ///     idle, since the loop wakes at its event-wait cadence.
    /// </summary>
    private static string ProfileCommand(App app, string argument)
    {
        string[] parts = argument.Split(
            separator: ' ',
            options: StringSplitOptions.RemoveEmptyEntries
        );
        int frames = 120;
        if (parts.Length > 0 &&
            (!int.TryParse(s: parts[0], result: out frames) || frames is < 1 or > 600))
            return Error($"profile wants a frame count in 1..600, got '{argument}'");
        string path = parts.Length > 1
            ? parts[1]
            : Path.Combine(
                path1: Path.GetTempPath(),
                path2: $"zigote_profile_{Environment.ProcessId}.json"
            );

        var done = new TaskCompletionSource<(int Frames, List<DebugProfiler.ScopeAggregate>)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        app.Post(() => Profiler.Capture(
                frames: frames,
                outputPath: path,
                onComplete: captured =>
                {
                    // Merge the frames and aggregate once — per-frame averages come out below.
                    var merged = new List<Profiler.Event>();
                    foreach (var f in captured) merged.AddRange(f);
                    done.TrySetResult((captured.Count, DebugProfiler.Aggregate(merged)));
                }
            )
        );

        // A visible ~60 fps app finishes 120 frames in ~2 s; a hidden window turns at a tenth of
        // the rate, hence the generous per-frame allowance.
        if (!done.Task.Wait(TimeSpan.FromSeconds(5 + (frames / 5.0))))
            return Error("capture did not complete — is the app's frame loop running?");

        (int got, var scopes) = done.Task.Result;
        if (got == 0) return Error("no frames captured");

        var json = new StringBuilder(4096);
        json.Append("{\"ok\":true,\"frames\":").Append(got).Append(",\"path\":");
        Quote(json: json, text: path);
        json.Append(",\"scopes\":[");
        int shown = Math.Min(val1: scopes.Count, val2: 32);
        for (int i = 0; i < shown; i++)
        {
            var s = scopes[i];
            if (i > 0) json.Append(',');
            json.Append("{\"name\":");
            Quote(json: json, text: s.Name);
            json.Append(",\"avg_total_ms\":").Append(
                (s.TotalMs / got).ToString(format: "F3", provider: CultureInfo.InvariantCulture)
            );
            json.Append(",\"avg_self_ms\":").Append(
                (s.SelfMs / got).ToString(format: "F3", provider: CultureInfo.InvariantCulture)
            );
            json.Append(",\"calls\":").Append(s.Calls).Append('}');
        }

        return json.Append("]}").ToString();
    }

    /// <summary>
    ///     The frame/CPU/memory sample <see cref="DebugStats" /> keeps every frame anyway, as one
    ///     JSON line — the from-outside readout a perf run needs (drive the app over this socket,
    ///     then ask what it cost). Frame times only mean render pace while the loop actually
    ///     renders every iteration, i.e. under <c>ZIGOTE_CONTINUOUS=1</c> or while animating; an
    ///     idle app's dt is just its event-wait timeout.
    /// </summary>
    private static string StatsJson()
    {
        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler:
            $"{{\"fps\":{DebugStats.Fps:0.#},\"fps_min\":{DebugStats.FpsMin:0.#},\"fps_max\":{DebugStats.FpsMax:0.#},\"frame_ms\":{DebugStats.FrameMs:0.###},\"cpu_pct\":{DebugStats.CpuPct:0.#},\"mem_mb\":{DebugStats.MemMb:0.#},\"gc_mb\":{DebugStats.GcMb:0.#},\"alloc_kb_per_frame\":{DebugStats.AllocKbPerFrame:0.##},\"gen0\":{DebugStats.Gen0Collections},\"gen1\":{DebugStats.Gen1Collections},\"gen2\":{DebugStats.Gen2Collections},\"jank_frames\":{DebugStats.JankFrames},\"total_frames\":{DebugStats.TotalFrames},\"ui_paint_commands\":{DebugStats.UiPaintCommands},\"overlay_paint_commands\":{DebugStats.OverlayPaintCommands}}}"
        );
    }

    /// <summary>
    ///     <c>size WIDTHxHEIGHT</c>, or <c>size window</c> to go back to the window's own size.
    ///     <para>
    ///         Sets <see cref="App.PreviewSize" />, so the live tree is measured at that size and
    ///         everything reading MediaQuery or a breakpoint adapts — a phone preview is a phone-sized
    ///         layout, not a desktop layout in a phone-shaped box.
    ///     </para>
    /// </summary>
    private static string Size(App app, string argument)
    {
        if (argument is "window" or "")
            app.PreviewSize = null;
        else
        {
            string[] parts = argument.Split('x', 'X');
            if (parts.Length != 2 ||
                !float.TryParse(
                    s: parts[0],
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out float w
                ) ||
                !float.TryParse(
                    s: parts[1],
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out float h
                ) ||
                w is < 1 or > 8192 || h is < 1 or > 8192)
                return Error($"size wants WIDTHxHEIGHT in 1..8192, got '{argument}'");

            app.PreviewSize = new Size(width: w, height: h);
        }

        // The tree has to be re-measured before the next capture, or the frame is the old size.
        app.RequestLayout();
        return Ok(app);
    }

    /// <summary>Everything the inspector knows about one widget, found by the id the tree reports.</summary>
    private static string Props(App app, string argument)
    {
        if (!int.TryParse(s: argument, result: out int id)) return Error("props wants a widget id");

        var widget = Find(widget: app.Root, id: id, depth: 0);
        if (widget is null)
            return Error($"no widget with id {id} — the tree may have been rebuilt");

        var json = new StringBuilder("{\"id\":").Append(id);
        json.Append(",\"type\":");
        Quote(json: json, text: widget.GetType().Name);
        json.Append(",\"props\":{");
        bool first = true;
        foreach ((string name, string value) in WidgetDebug.Properties(widget))
        {
            if (!first) json.Append(',');
            Quote(json: json, text: name);
            json.Append(':');
            Quote(json: json, text: value);
            first = false;
        }

        json.Append("}");
        Bounds(json: json, bounds: widget.Bounds);
        return json.Append('}').ToString();
    }

    private static Widget? Find(Widget? widget, int id, int depth)
    {
        if (widget is null || depth > MaxDepth) return null;
        if (widget.GetHashCode() == id) return widget;
        foreach (var child in WidgetDebug.Children(widget))
        {
            if (Find(widget: child, id: id, depth: depth + 1) is { } hit)
                return hit;
        }

        return null;
    }

    /// <summary>
    ///     <c>hit X Y</c> — the widget the dispatch pipeline would hand a click at that point to
    ///     (overlays first, then the root), with up to eight ancestors. Answers "why did my tap do
    ///     nothing" without a tree dump: the hit is a scrim, a disabled wrapper, or nothing at all.
    /// </summary>
    private static string Hit(App app, string argument)
    {
        string[] parts = argument.Split(
            separator: ' ',
            options: StringSplitOptions.RemoveEmptyEntries
        );
        if (parts.Length != 2 ||
            !float.TryParse(
                s: parts[0],
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out float x
            ) ||
            !float.TryParse(
                s: parts[1],
                style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture,
                result: out float y
            ))
            return Error($"hit wants X Y, got '{argument}'");

        var json = new StringBuilder("{\"hit\":");
        var hit = app.HitTestAt(new Offset(x: x, y: y));
        WidgetRef(json: json, widget: hit);
        json.Append(",\"path\":[");
        // Parent is only set by Attach, so the chain can stop early; it is still the cheapest
        // answer to "inside what".
        var node = hit?.Parent;
        for (int i = 0; i < 8 && node is not null; i++, node = node.Parent)
        {
            if (i > 0) json.Append(',');
            Quote(json: json, text: node.GetType().Name);
        }

        return json.Append("]}").ToString();
    }

    /// <summary>
    ///     <c>text [X Y W H]</c> — every text run the last frame drew (optionally only those whose
    ///     baseline origin falls in the region): its string, baseline origin, size, weight, family,
    ///     shaped advance width and the clip it is visible through. Runs clipped out entirely are
    ///     dropped. This is the ground truth for typographic measurement — baselines, left edges,
    ///     optical centering — which widget bounds alone cannot answer.
    /// </summary>
    private static string TextRuns(App app, string argument)
    {
        Rect? region = null;
        if (argument.Length > 0)
        {
            float[] n = [.. argument.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN)];
            if (n.Length != 4 || n.Any(float.IsNaN)) return Error($"text wants nothing or X Y W H, got '{argument}'");
            region = new Rect(x: n[0], y: n[1], width: n[2], height: n[3]);
        }

        var window = new Rect(x: 0, y: 0, width: app.LayoutWidth, height: app.LayoutHeight);
        var json = new StringBuilder("{\"runs\":[");
        bool first = true;
        foreach (var run in app.DebugTextRuns())
        {
            if (string.IsNullOrWhiteSpace(run.Text)) continue;
            var visible = run.Clip is { } c ? Rect.Intersect(a: c, b: window) : window;
            var width = app.Engine.MeasureText(text: run.Text, fontSize: run.FontSize, weight: run.Weight,
                letterSpacing: run.LetterSpacing, fontFamily: run.Family).Width;
            var box = new Rect(x: run.BaselineX, y: run.BaselineY - run.FontSize, width: width, height: run.FontSize);
            if (!visible.Overlaps(box)) continue;
            if (region is { } r && !r.Contains(px: run.BaselineX, py: run.BaselineY)) continue;

            if (!first) json.Append(',');
            first = false;
            json.Append("{\"t\":");
            Quote(json: json, text: run.Family == Theme.Icons.Family ? "icon" : run.Text);
            json.Append(",\"x\":").Append(Round(run.BaselineX));
            json.Append(",\"baseline\":").Append(Round(run.BaselineY));
            json.Append(",\"size\":").Append(Round(run.FontSize));
            json.Append(",\"w\":").Append(Round(width));
            json.Append(",\"weight\":").Append((int)run.Weight);
            json.Append(",\"family\":");
            Quote(json: json, text: run.Family);
            json.Append(",\"color\":\"#").Append(Hex(run.Color.R)).Append(Hex(run.Color.G)).Append(Hex(run.Color.B));
            if (run.Color.A < 1f) json.Append(Hex(run.Color.A));
            json.Append('"');
            if (run.Clip is { } clip)
            {
                json.Append(",\"clip\":[").Append(Round(clip.X)).Append(',').Append(Round(clip.Y)).Append(',')
                    .Append(Round(clip.Width)).Append(',').Append(Round(clip.Height)).Append(']');
            }

            json.Append('}');
        }

        return json.Append("]}").ToString();

        static string Hex(float v) => ((int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f)).ToString("x2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Everything a remote driver otherwise has to infer from a screenshot: the layout it is
    ///     aiming at, what holds focus and hover, whether a dialog is up, how big the tree is, and
    ///     whether the frame it is about to capture is still mid-animation.
    /// </summary>
    private static string InfoJson(App app)
    {
        int count = 0, deepest = 0;
        Count(widget: app.Root, depth: 0);

        var json = new StringBuilder("{\"w\":").Append(Round(app.LayoutWidth));
        json.Append(",\"h\":").Append(Round(app.LayoutHeight));
        json.Append(",\"preview_size\":").Append(app.PreviewSize is null ? "false" : "true");
        json.Append(",\"theme\":\"").Append(app.Theme.IsDark ? "dark" : "light").Append('"');
        json.Append(",\"locale\":");
        Quote(json: json, text: app.LocaleInfo?.Invoke().Current);
        json.Append(",\"animating\":").Append(Ticker.AnyActive ? "true" : "false");
        json.Append(",\"paint_version\":").Append(app.PaintVersion);
        json.Append(",\"widgets\":").Append(count);
        json.Append(",\"depth\":").Append(deepest);
        json.Append(",\"pointer\":[").Append(Round(app.PointerPosition.X)).Append(',')
            .Append(Round(app.PointerPosition.Y)).Append(']');
        json.Append(",\"focused\":");
        WidgetRef(json: json, widget: app.FocusedWidget);
        json.Append(",\"hovered\":");
        WidgetRef(json: json, widget: app.HoveredWidget);
        json.Append(",\"overlays\":[");
        for (int i = 0; i < app.Overlays.Count; i++)
        {
            if (i > 0) json.Append(',');
            Quote(json: json, text: app.Overlays[i].GetType().Name);
        }

        return json.Append("]}").ToString();

        void Count(Widget? widget, int depth)
        {
            if (widget is null || depth > MaxDepth || count >= MaxNodes) return;
            count++;
            if (depth > deepest) deepest = depth;
            foreach (var child in WidgetDebug.Children(widget))
                Count(widget: child, depth: depth + 1);
        }
    }

    /// <summary>One widget as the tree would name it — id, type, description, bounds — or null.</summary>
    private static void WidgetRef(StringBuilder json, Widget? widget)
    {
        if (widget is null)
        {
            json.Append("null");
            return;
        }

        json.Append("{\"id\":").Append(widget.GetHashCode());
        json.Append(",\"type\":");
        Quote(json: json, text: widget.GetType().Name);
        json.Append(",\"desc\":");
        Quote(json: json, text: WidgetDebug.Describe(widget));
        Bounds(json: json, bounds: widget.Bounds);
        json.Append('}');
    }

    /// <summary>The state a panel needs to keep its controls honest after any command that changes it.</summary>
    private static string Ok(App app)
    {
        var json = new StringBuilder("{\"ok\":true,\"w\":").Append(Round(app.LayoutWidth));
        json.Append(",\"h\":").Append(Round(app.LayoutHeight));
        return json.Append('}').ToString();
    }

    /// <summary>
    ///     A picture of the current frame, so a panel can show the UI instead of just describing it.
    ///     <para>
    ///         Re-renders the last submitted paint list into an offscreen target
    ///         (<see cref="Core.Engine.ZigoteEngine.CaptureUiBmp" />) rather than reading the swapchain
    ///         back, so asking for a frame never disturbs the one on screen.
    ///     </para>
    ///     <para>
    ///         BMP, base64, through the same one-line-of-JSON channel as everything else: a frame is
    ///         megabytes and base64 costs a third more, but every consumer already has a JSON parser and
    ///         a BMP decoder, and inventing a binary framing for one command would be the more expensive
    ///         mistake. ponytail: if a live-refreshing panel ever makes the copies hurt, add a length-
    ///         prefixed binary reply for <c>shot</c> alone and leave the rest of the protocol alone.
    ///     </para>
    /// </summary>
    private static string Shot(App app, string argument)
    {
        // `shot [scale]` — 2 renders at twice the density for a HiDPI panel.
        float scale =
            float.TryParse(s: argument, provider: CultureInfo.InvariantCulture, result: out float s)
                ? Math.Clamp(value: s, min: 0.1f, max: 4f)
                : 1f;

        string path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"zigote-shot-{Environment.ProcessId}.bmp"
        );
        try
        {
            if (!app.CaptureUi(path: path, scale: scale, density: out float density))
                return Error("the engine could not capture a frame — nothing has been painted yet");

            // The layout size, not the window's: with a device preview set they differ, and the panel
            // scales the picture by what it is told. The picture itself is w×h × `scale` pixels, so a
            // viewer needs the density that was actually used — not the one that was asked for.
            var json =
                new StringBuilder("{\"format\":\"bmp\",\"w\":").Append((uint)app.LayoutWidth);
            json.Append(",\"h\":").Append((uint)app.LayoutHeight);
            json.Append(",\"scale\":").Append(Round(density));
            json.Append(",\"data\":\"").Append(Convert.ToBase64String(File.ReadAllBytes(path)));
            return json.Append("\"}").ToString();
        }
        catch (IOException e)
        {
            return Error($"capture failed: {e.Message}");
        }
        finally
        {
            // Best effort: a leftover file in temp is harmless, a throw here would lose the frame.
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    ///     One synthetic input event, parsed from the <c>input</c> command's argument, or null when it
    ///     does not parse. Coordinates are layout points — the same space <c>shot</c> reports — and key
    ///     names are <see cref="KeyCode" /> members. Internal so the grammar is testable without a window.
    ///     <list type="bullet">
    ///         <item><c>down X Y [left|right|middle]</c> / <c>up X Y [button]</c> — press / release.</item>
    ///         <item><c>move X Y</c> — pointer move.</item>
    ///         <item><c>scroll X Y DX DY</c> — wheel ticks at a position.</item>
    ///         <item><c>touchdown X Y [finger]</c> / <c>touchmove</c> / <c>touchup</c> — one finger.</item>
    ///         <item><c>keydown NAME [shift+ctrl+alt+cmd]</c> / <c>keyup NAME [mods]</c> — a physical key.</item>
    ///         <item>
    ///             <c>text …</c> — commit text to the focused widget (everything after the space,
    ///             verbatim).
    ///         </item>
    ///     </list>
    /// </summary>
    internal static InputEvent? ParseInput(string argument)
    {
        int space = argument.IndexOf(' ');
        string kind = space < 0 ? argument : argument[..space];
        string rest = space < 0 ? "" : argument[(space + 1)..];

        // Text is verbatim — splitting it on spaces would eat the user's own spaces.
        if (kind == "text") return rest.Length > 0 ? new TextInputEvent(rest) : null;

        string[] parts = rest.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);
        switch (kind)
        {
            case "down" or "up" when parts.Length is 2 or 3 && Xy(
                parts: parts,
                x: out float x,
                y: out float y
            ):
            {
                var button = parts.Length < 3
                    ? MouseButton.Left
                    : parts[2] switch {
                        "right" => MouseButton.Right,
                        "middle" => MouseButton.Middle,
                        _ => MouseButton.Left,
                    };
                return kind == "down"
                    ? new MouseDownEvent(x: x, y: y, button: button)
                    : new MouseUpEvent(x: x, y: y, button: button);
            }

            case "move" when parts.Length == 2 && Xy(parts: parts, x: out float x, y: out float y):
                return new MouseMoveEvent(x: x, y: y);

            // A finger, not a mouse: touch drags scroll lists and fling, which a mouse drag never
            // does — the only way to drive a mobile gesture from outside.
            case "touchdown" or "touchmove" or "touchup" when parts.Length is 2 or 3 && Xy(
                parts: parts,
                x: out float x,
                y: out float y
            ):
            {
                int finger = 0;
                if (parts.Length == 3 && !int.TryParse(s: parts[2], result: out finger))
                    return null;
                return kind switch {
                    "touchdown" => new TouchDownEvent(
                        x: x,
                        y: y,
                        finger: finger,
                        pressure: 1f
                    ),
                    "touchmove" => new TouchMoveEvent(
                        x: x,
                        y: y,
                        finger: finger,
                        pressure: 1f
                    ),
                    _ => new TouchUpEvent(
                        x: x,
                        y: y,
                        finger: finger,
                        pressure: 1f
                    ),
                };
            }

            case "scroll" when parts.Length == 4 &&
                               Xy(parts: parts, x: out float x, y: out float y) &&
                               Num(s: parts[2], v: out float dx) && Num(
                                   s: parts[3],
                                   v: out float dy
                               ):
                return new ScrollEvent(
                    x: x,
                    y: y,
                    scrollX: dx,
                    scrollY: dy
                );

            case "keydown" or "keyup" when parts.Length is 1 or 2 &&
                                           Enum.TryParse<KeyCode>(
                                               value: parts[0],
                                               ignoreCase: true,
                                               result: out var key
                                           ) &&
                                           key != KeyCode.Unknown:
            {
                var mods = Modifiers.None;
                if (parts.Length == 2)
                {
                    foreach (string m in parts[1].Split('+'))
                    {
                        mods |= m switch {
                            "shift" => Modifiers.Shift,
                            "ctrl" => Modifiers.Ctrl,
                            "alt" => Modifiers.Alt,
                            "cmd" => Modifiers.Cmd,
                            _ => Modifiers.None,
                        };
                    }
                }

                return new KeyEvent(
                    down: kind == "keydown",
                    // Letters carry their character, as a real key event does — widgets that
                    // switch on keyChar (single-key tool shortcuts) are otherwise undrivable.
                    keyChar: key is >= KeyCode.A and <= KeyCode.Z
                        ? (char)('a' + (key - KeyCode.A))
                        : '\0',
                    scancode: (uint)key,
                    modifiers: mods
                );
            }

            default:
                return null;
        }

        static bool Xy(string[] parts, out float x, out float y)
        {
            y = 0;
            return Num(s: parts[0], v: out x) && Num(s: parts[1], v: out y);
        }

        static bool Num(string s, out float v)
        {
            return float.TryParse(
                       s: s,
                       style: NumberStyles.Float,
                       provider: CultureInfo.InvariantCulture,
                       result: out v
                   ) &&
                   float.IsFinite(v);
        }
    }

    /// <summary>
    ///     Push frames down this connection until the client hangs up: one JSON header line, then each
    ///     frame as a 4-byte big-endian length + BMP bytes. Unchanged frames are not sent, so an idle
    ///     app costs the client nothing and a blocking read doubles as "wait for the next change".
    ///     <para>
    ///         <c>stream [scale] [fps]</c>. This is the animation-rate channel the polled <c>shot</c>
    ///         cannot be: no per-frame connection, no base64, no JSON envelope.
    ///         ponytail: each frame still round-trips a BMP file and a full-frame byte compare; if a
    ///         4K preview at 60 fps ever matters, add an in-memory capture to the engine first.
    ///     </para>
    /// </summary>
    private static void StreamFrames(NetworkStream stream, StreamWriter writer, App app,
        string argument)
    {
        string[] parts = argument.Split(
            separator: ' ',
            options: StringSplitOptions.RemoveEmptyEntries
        );
        float scale = parts.Length > 0 &&
                      float.TryParse(
                          s: parts[0],
                          style: NumberStyles.Float,
                          provider: CultureInfo.InvariantCulture,
                          result: out float s
                      )
            ? Math.Clamp(value: s, min: 0.1f, max: 4f)
            : 1f;
        int fps = parts.Length > 1 && int.TryParse(s: parts[1], result: out int f)
            ? Math.Clamp(value: f, min: 1, max: 60)
            : 30;

        // The handshake: a client of an older server gets {"error":…} here and knows to fall back.
        // It carries the density because a raw frame has no envelope to put it in, and a viewer that
        // guesses 1× draws a 2× picture at twice the size.
        writer.Write(
            $"{{\"format\":\"bmp\",\"stream\":true,\"scale\":{Round(app.CaptureDensity(scale))}}}\n"
        );

        stream.WriteTimeout = (int)Timeout.TotalMilliseconds; // a wedged client fails, not hangs
        string path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"zigote-stream-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}.bmp"
        );
        byte[]? last = null;
        byte[] lengthPrefix = new byte[4];
        int seen = -1;
        try
        {
            while (true)
            {
                // Version-gated: when no paint walk ran since the last capture, skip the capture
                // entirely (no offscreen render, no BMP round-trip, no byte compare). The byte
                // compare below stays as the backstop for walks that repainted identical pixels.
                (byte[]? frame, int version) = CaptureFrame(
                    app: app,
                    path: path,
                    scale: scale,
                    sinceVersion: seen
                );
                seen = version;
                if (frame is not null && (last is null || !frame.AsSpan().SequenceEqual(last)))
                {
                    BinaryPrimitives.WriteInt32BigEndian(
                        destination: lengthPrefix,
                        value: frame.Length
                    );
                    stream.Write(lengthPrefix);
                    stream.Write(frame);
                    last = frame;
                }

                Thread.Sleep(1000 / fps);
            }
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
        }
    }

    /// <summary>
    ///     The current frame's BMP bytes plus the paint version they belong to, captured on the UI
    ///     thread at the end of a frame (so a frame that dispatched an injected click yields the
    ///     click's result, not the frame before it). A null frame means nothing new to show —
    ///     unpainted since <paramref name="sinceVersion" />, nothing painted yet, or a failed capture.
    /// </summary>
    private static (byte[]? Frame, int Version) CaptureFrame(App app, string path, float scale,
        int sinceVersion)
    {
        var done =
            new TaskCompletionSource<(byte[]?, int)>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        app.PostAfterFrame(() =>
            {
                try
                {
                    int version = app.PaintVersion;
                    done.TrySetResult(
                        version != sinceVersion && app.CaptureUi(path: path, scale: scale)
                            ? (File.ReadAllBytes(path), version)
                            : (null, version)
                    );
                }
                catch (Exception)
                {
                    done.TrySetResult((null, sinceVersion));
                }
            }
        );
        return done.Task.Wait(Timeout) ? done.Task.Result : (null, sinceVersion);
    }

    /// <summary>
    ///     Run <paramref name="work" /> on the UI thread and wait for its result. The wait is what makes
    ///     the answer a coherent snapshot rather than a read racing the next layout pass.
    /// </summary>
    private static string OnUiThread(App app, Func<string> work, bool afterFrame = false)
    {
        // A TaskCompletionSource rather than an event to wait on: on timeout this method returns while
        // the posted action is still queued, and there is nothing left for that action to signal into
        // that could have been disposed underneath it.
        var done =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        Action<Action> post = afterFrame ? app.PostAfterFrame : app.Post;
        post(() =>
            {
                try
                {
                    done.TrySetResult(work());
                }
                catch (Exception e)
                {
                    done.TrySetResult(Error(e.Message));
                }
            }
        );

        return done.Task.Wait(Timeout)
            ? done.Task.Result
            : Error("timed out waiting for a frame");
    }

    // ── serialisation ─────────────────────────────────────────────────────────
    //
    // Hand-written rather than System.Text.Json: the shapes are two small trees, and staying off
    // reflection keeps the AOT/trimmed builds honest without a serializer context to maintain.

    /// <summary>The <c>widgets</c> reply for a tree. Internal so it can be checked without a window.</summary>
    internal static string WidgetTreeJson(Widget? root)
    {
        var json = new StringBuilder("{\"tree\":");
        int budget = MaxNodes;
        WriteWidget(
            json: json,
            widget: root,
            depth: 0,
            budget: ref budget
        );
        return json.Append('}').ToString();
    }

    /// <summary>
    ///     The <c>locales</c> reply. An app without a <see cref="App.LocaleInfo" /> hook answers with an
    ///     empty list rather than an error, so a panel can tell "no localization" from "broken".
    /// </summary>
    internal static string LocalesJson((string Current, IReadOnlyList<string> Supported)? info)
    {
        var json = new StringBuilder("{\"current\":");
        Quote(json: json, text: info?.Current);
        json.Append(",\"locales\":[");
        var supported = info?.Supported ?? [];
        for (int i = 0; i < supported.Count; i++)
        {
            if (i > 0) json.Append(',');
            Quote(json: json, text: supported[i]);
        }

        return json.Append("]}").ToString();
    }

    /// <summary>The <c>semantics</c> reply for a tree.</summary>
    internal static string SemanticsTreeJson(SemanticsNode? root)
    {
        var json = new StringBuilder("{\"tree\":");
        int budget = MaxNodes;
        WriteSemantics(
            json: json,
            node: root,
            depth: 0,
            budget: ref budget
        );
        return json.Append('}').ToString();
    }

    private static void WriteWidget(StringBuilder json, Widget? widget, int depth, ref int budget)
    {
        if (widget is null || depth > MaxDepth || budget-- <= 0)
        {
            json.Append("null");
            return;
        }

        json.Append("{\"id\":").Append(widget.GetHashCode());
        json.Append(",\"type\":");
        Quote(json: json, text: widget.GetType().Name);
        json.Append(",\"desc\":");
        Quote(json: json, text: WidgetDebug.Describe(widget));
        Bounds(json: json, bounds: widget.Bounds);

        json.Append(",\"children\":[");
        bool first = true;
        foreach (var child in WidgetDebug.Children(widget))
        {
            if (budget <= 0) break;
            if (!first) json.Append(',');
            WriteWidget(
                json: json,
                widget: child,
                depth: depth + 1,
                budget: ref budget
            );
            first = false;
        }

        json.Append("]}");
    }

    private static void WriteSemantics(StringBuilder json, SemanticsNode? node, int depth,
        ref int budget)
    {
        if (node is null || depth > MaxDepth || budget-- <= 0)
        {
            json.Append("null");
            return;
        }

        json.Append("{\"id\":").Append(node.Id);
        json.Append(",\"role\":");
        Quote(json: json, text: node.Role.ToString());
        json.Append(",\"label\":");
        Quote(json: json, text: node.Label);
        json.Append(",\"value\":");
        Quote(json: json, text: node.Value);
        json.Append(",\"hint\":");
        Quote(json: json, text: node.Hint);
        json.Append(",\"flags\":");
        Quote(json: json, text: node.Flags.ToString());
        json.Append(",\"actions\":");
        Quote(json: json, text: node.Actions.ToString());
        Bounds(json: json, bounds: node.Bounds);

        json.Append(",\"children\":[");
        for (int i = 0; i < node.Children.Count; i++)
        {
            if (budget <= 0) break;
            if (i > 0) json.Append(',');
            WriteSemantics(
                json: json,
                node: node.Children[i],
                depth: depth + 1,
                budget: ref budget
            );
        }

        json.Append("]}");
    }

    private static void Bounds(StringBuilder json, Rect bounds)
    {
        json.Append(",\"x\":").Append(Round(bounds.X));
        json.Append(",\"y\":").Append(Round(bounds.Y));
        json.Append(",\"w\":").Append(Round(bounds.Width));
        json.Append(",\"h\":").Append(Round(bounds.Height));
    }

    // Invariant culture, or a machine with a comma decimal separator emits JSON no parser accepts.
    private static string Round(float v)
    {
        return float.IsFinite(v)
            ? Math.Round(value: v, digits: 2).ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private static string Error(string message)
    {
        var json = new StringBuilder("{\"error\":");
        Quote(json: json, text: message);
        return json.Append('}').ToString();
    }

    private static void Quote(StringBuilder json, string? text)
    {
        if (text is null)
        {
            json.Append("null");
            return;
        }

        json.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    // Control characters are illegal raw in JSON; widget descriptions can carry them.
                    if (c < ' ')
                    {
                        json.Append("\\u").Append(
                            ((int)c).ToString(format: "x4", provider: CultureInfo.InvariantCulture)
                        );
                    }
                    else json.Append(c);

                    break;
            }
        }

        json.Append('"');
    }
}
