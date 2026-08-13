using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Zigote.Core.Diagnostics;
using Zigote.Core.Events;
using Zigote.UI.Debug;
using Zigote.UI.Semantics;
using Zigote.UI.Widgets;

namespace Zigote.UI.Host;

/// <summary>
///     Hands the live widget tree, the accessibility tree and the preview targets to whatever asks, over
///     a loopback socket.
///     <para>
///         This is what the IDE panels read, and — like <see cref="WidgetPreview" /> — it is deliberately
///         not an IDE feature. It is a socket and four one-word commands, so a Rider tool window, a
///         terminal (<c>echo widgets | nc 127.0.0.1 $port</c>), a test or a script all get the same data
///         with nothing installed. Nothing here knows an editor exists.
///     </para>
///     <para>
///         Off unless <c>ZIGOTE_INSPECT</c> is set to a port — <c>0</c> asks the OS for a free one, which
///         is then printed as <c>zigote inspect: 127.0.0.1:PORT</c> for the launcher to read. Bound to
///         loopback only: this exposes an app's entire UI state, so it must never be reachable off the
///         machine.
///     </para>
///     <list type="bullet">
///         <item><c>widgets</c> — the widget tree, from <see cref="WidgetDebug" />, as the inspector sees it.</item>
///         <item><c>semantics</c> — the accessibility tree, freshly built.</item>
///         <item><c>targets</c> — what <see cref="WidgetPreview" /> could show.</item>
///         <item><c>preview &lt;Type&gt;</c> — swap the shown widget without restarting.</item>
///         <item><c>shot [scale]</c> — the current frame as a base64 BMP.</item>
///         <item><c>stream [scale] [fps]</c> — frames pushed as they change, length-prefixed binary.</item>
///         <item><c>input …</c> — synthetic pointer/scroll/key/text events; see <see cref="ParseInput" />.</item>
///         <item><c>size WxH</c> | <c>size window</c> — lay the tree out at a device size.</item>
///         <item><c>theme dark|light</c> — swap the app theme.</item>
///         <item><c>locales</c> — the active locale and the ones the app supports.</item>
///         <item><c>locale &lt;tag&gt;</c> — switch the app's locale (needs a <c>LocalizationsScope</c>).</item>
///         <item><c>window hide|show</c> — the app's own window, which a preview does not want.</item>
///         <item><c>props ID</c> — one widget's properties, by the id the tree reports.</item>
///     </list>
///     <para>
///         Every command is answered on the UI thread through <see cref="App.Post" /> and the socket
///         thread waits for the result: the widget tree may only be walked while layout is not running,
///         and a snapshot taken from another thread would be a torn one.
///     </para>
/// </summary>
public static class InspectServer
{
    // A tree big enough to hit these is a runaway, not a UI; the caps keep one bad frame from turning
    // into a multi-megabyte response the panel then has to parse.
    private const int MaxNodes = 20_000;
    private const int MaxDepth = 64;

    // Long enough to cover a slow frame under a debugger, short enough that a wedged app fails the
    // request instead of hanging the panel.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Start listening if <c>ZIGOTE_INSPECT</c> asks for it. <paramref name="setPreview" /> is invoked
    ///     on the UI thread to swap the shown widget; null disables the <c>preview</c> command.
    /// </summary>
    public static void Start(App app, Action<string>? setPreview = null, Func<string, bool>? setTheme = null)
    {
        var setting = Environment.GetEnvironmentVariable("ZIGOTE_INSPECT");
        if (setting is not { Length: > 0 } || !int.TryParse(setting, out var port) || port is < 0 or > 65535)
            return;

        // A requested port can already be taken — the launcher picked it a moment earlier and something
        // else got in first. Falling back to an OS-chosen one and announcing it is always better than
        // running with no socket at all: the caller polling the port it asked for will time out, but a
        // caller reading this line still finds the app. Running portless looks identical to a hung app.
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException first) when (port != 0)
        {
            DebugLog.Error($"zigote inspect: port {port} is taken ({first.Message}); picking another");
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
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

        var actual = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.Out.WriteLine($"zigote inspect: 127.0.0.1:{actual}");
        Console.Out.Flush();

        // Background so the process still exits when the window closes, with the listener along with it.
        new Thread(() => Serve(listener, app, setPreview, setTheme)) {
            IsBackground = true,
            Name = "zigote-inspect",
        }.Start();
    }

    private static void Serve(TcpListener listener, App app, Action<string>? setPreview, Func<string, bool>? setTheme)
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
                        Handle(client, app, setPreview, setTheme);
                    }
                    catch (IOException)
                    {
                        // The panel closed the socket mid-answer; nothing to do and nothing worth logging.
                    }
                }
            }) { IsBackground = true, Name = "zigote-inspect-client" }.Start();
        }
    }

    private static void Handle(TcpClient client, App app, Action<string>? setPreview, Func<string, bool>? setTheme)
    {
        using var stream = client.GetStream();
        stream.ReadTimeout = (int)Timeout.TotalMilliseconds;
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        var line = reader.ReadLine();
        if (line is null) return;

        var space = line.IndexOf(' ');
        var command = (space < 0 ? line : line[..space]).Trim();
        var argument = space < 0 ? "" : line[(space + 1)..].Trim();

        // `stream` takes the connection over and never returns until the client hangs up.
        if (command == "stream")
        {
            StreamFrames(stream, writer, app, argument);
            return;
        }

        // `shot` and `input` run at the END of a frame, everything else at the top. A shot answered
        // at the top would show the previous frame — and one sent right after an `input` click could
        // drain in the same batch as the click, before it even dispatched, missing the dialog it
        // opened. `input` rides the same quiet queue because its post must not force a repaint: the
        // injected events go through the real dispatch pipeline next frame, which repaints exactly as
        // much as the same OS input would — a move flood over nothing repaints nothing.
        var answer = command is "shot" or "input"
            ? OnUiThread(app, () => Answer(app, command, argument, setPreview, setTheme), afterFrame: true)
            : OnUiThread(app, () => Answer(app, command, argument, setPreview, setTheme));
        writer.Write(answer);
        writer.Write('\n');
    }

    private static string Answer(App app, string command, string argument, Action<string>? setPreview,
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
                var first = true;
                foreach (var name in WidgetPreview.Candidates())
                {
                    if (!first) json.Append(',');
                    Quote(json, name);
                    first = false;
                }

                return json.Append("]}").ToString();
            }

            case "preview" when setPreview is not null && argument.Length > 0:
                setPreview(argument);
                return "{\"ok\":true}";

            case "shot":
                return Shot(app, argument);

            case "size":
                return Size(app, argument);

            case "theme" when setTheme is not null:
                return setTheme(argument) ? Ok(app) : Error($"unknown theme '{argument}'");

            case "locales":
                return LocalesJson(app.LocaleInfo?.Invoke());

            case "locale" when argument.Length > 0:
                if (app.SetLocale is null)
                    return Error("this app has no LocalizationsScope, so there is no locale to switch");
                return app.SetLocale(argument) ? Ok(app) : Error($"'{argument}' is not a locale tag");

            case "input":
            {
                if (ParseInput(argument) is not { } evt)
                    return Error($"input wants down|up|move|scroll|keydown|keyup|text …, got '{argument}'");
                app.InjectEvent(evt);
                return "{\"ok\":true}";
            }

            case "window":
                // Preview mode hides the app's own window: two windows showing the same thing, one of
                // them laid out for a phone, is worse than none. The Dock tile goes with it on macOS,
                // where hiding a window leaves the app in the Dock and the ⌘-Tab switcher regardless.
                app.Engine.MainWindowSetVisible(argument != "hide");
                app.Engine.AppSetDockVisible(argument != "hide");
                return "{\"ok\":true}";

            case "props":
                return Props(app, argument);

            default:
                return Error($"unknown command '{command}'");
        }
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
        {
            app.PreviewSize = null;
        }
        else
        {
            var parts = argument.Split('x', 'X');
            if (parts.Length != 2 ||
                !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
                w is < 1 or > 8192 || h is < 1 or > 8192)
                return Error($"size wants WIDTHxHEIGHT in 1..8192, got '{argument}'");

            app.PreviewSize = new Core.Size(w, h);
        }

        // The tree has to be re-measured before the next capture, or the frame is the old size.
        app.RequestLayout();
        return Ok(app);
    }

    /// <summary>Everything the inspector knows about one widget, found by the id the tree reports.</summary>
    private static string Props(App app, string argument)
    {
        if (!int.TryParse(argument, out var id)) return Error("props wants a widget id");

        var widget = Find(app.Root, id, 0);
        if (widget is null) return Error($"no widget with id {id} — the tree may have been rebuilt");

        var json = new StringBuilder("{\"id\":").Append(id);
        json.Append(",\"type\":");
        Quote(json, widget.GetType().Name);
        json.Append(",\"props\":{");
        var first = true;
        foreach (var (name, value) in WidgetDebug.Properties(widget))
        {
            if (!first) json.Append(',');
            Quote(json, name);
            json.Append(':');
            Quote(json, value);
            first = false;
        }

        json.Append("}");
        Bounds(json, widget.Bounds);
        return json.Append('}').ToString();
    }

    private static Widget? Find(Widget? widget, int id, int depth)
    {
        if (widget is null || depth > MaxDepth) return null;
        if (widget.GetHashCode() == id) return widget;
        foreach (var child in WidgetDebug.Children(widget))
            if (Find(child, id, depth + 1) is { } hit)
                return hit;
        return null;
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
        var scale = float.TryParse(argument, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 0.1f, 4f) : 1f;

        var path = Path.Combine(Path.GetTempPath(), $"zigote-shot-{Environment.ProcessId}.bmp");
        try
        {
            if (!app.CaptureUi(path, scale, out var density))
                return Error("the engine could not capture a frame — nothing has been painted yet");

            // The layout size, not the window's: with a device preview set they differ, and the panel
            // scales the picture by what it is told. The picture itself is w×h × `scale` pixels, so a
            // viewer needs the density that was actually used — not the one that was asked for.
            var json = new StringBuilder("{\"format\":\"bmp\",\"w\":").Append((uint)app.LayoutWidth);
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
            catch (IOException)
            {
            }
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
    ///         <item><c>keydown NAME [shift+ctrl+alt+cmd]</c> / <c>keyup NAME [mods]</c> — a physical key.</item>
    ///         <item><c>text …</c> — commit text to the focused widget (everything after the space, verbatim).</item>
    ///     </list>
    /// </summary>
    internal static InputEvent? ParseInput(string argument)
    {
        var space = argument.IndexOf(' ');
        var kind = space < 0 ? argument : argument[..space];
        var rest = space < 0 ? "" : argument[(space + 1)..];

        // Text is verbatim — splitting it on spaces would eat the user's own spaces.
        if (kind == "text") return rest.Length > 0 ? new TextInputEvent(rest) : null;

        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (kind)
        {
            case "down" or "up" when parts.Length is 2 or 3 && Xy(parts, out var x, out var y):
            {
                var button = parts.Length < 3 ? MouseButton.Left : parts[2] switch {
                    "right" => MouseButton.Right,
                    "middle" => MouseButton.Middle,
                    _ => MouseButton.Left,
                };
                return kind == "down"
                    ? new MouseDownEvent(x, y, button)
                    : new MouseUpEvent(x, y, button);
            }

            case "move" when parts.Length == 2 && Xy(parts, out var x, out var y):
                return new MouseMoveEvent(x, y);

            case "scroll" when parts.Length == 4 && Xy(parts, out var x, out var y) &&
                               Num(parts[2], out var dx) && Num(parts[3], out var dy):
                return new ScrollEvent(x, y, dx, dy);

            case "keydown" or "keyup" when parts.Length is 1 or 2 &&
                                           Enum.TryParse<KeyCode>(parts[0], true, out var key) &&
                                           key != KeyCode.Unknown:
            {
                var mods = Modifiers.None;
                if (parts.Length == 2)
                    foreach (var m in parts[1].Split('+'))
                        mods |= m switch {
                            "shift" => Modifiers.Shift,
                            "ctrl" => Modifiers.Ctrl,
                            "alt" => Modifiers.Alt,
                            "cmd" => Modifiers.Cmd,
                            _ => Modifiers.None,
                        };
                return new KeyEvent(kind == "keydown", '\0', (uint)key, mods);
            }

            default:
                return null;
        }

        static bool Xy(string[] parts, out float x, out float y)
        {
            y = 0;
            return Num(parts[0], out x) && Num(parts[1], out y);
        }

        static bool Num(string s, out float v)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) &&
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
    private static void StreamFrames(NetworkStream stream, StreamWriter writer, App app, string argument)
    {
        var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scale = parts.Length > 0 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? Math.Clamp(s, 0.1f, 4f)
            : 1f;
        var fps = parts.Length > 1 && int.TryParse(parts[1], out var f) ? Math.Clamp(f, 1, 60) : 30;

        // The handshake: a client of an older server gets {"error":…} here and knows to fall back.
        // It carries the density because a raw frame has no envelope to put it in, and a viewer that
        // guesses 1× draws a 2× picture at twice the size.
        writer.Write($"{{\"format\":\"bmp\",\"stream\":true,\"scale\":{Round(app.CaptureDensity(scale))}}}\n");

        stream.WriteTimeout = (int)Timeout.TotalMilliseconds; // a wedged client fails, not hangs
        var path = Path.Combine(Path.GetTempPath(), $"zigote-stream-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}.bmp");
        byte[]? last = null;
        var lengthPrefix = new byte[4];
        var seen = -1;
        try
        {
            while (true)
            {
                // Version-gated: when no paint walk ran since the last capture, skip the capture
                // entirely (no offscreen render, no BMP round-trip, no byte compare). The byte
                // compare below stays as the backstop for walks that repainted identical pixels.
                var (frame, version) = CaptureFrame(app, path, scale, seen);
                seen = version;
                if (frame is not null && (last is null || !frame.AsSpan().SequenceEqual(last)))
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, frame.Length);
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
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    ///     The current frame's BMP bytes plus the paint version they belong to, captured on the UI
    ///     thread at the end of a frame (so a frame that dispatched an injected click yields the
    ///     click's result, not the frame before it). A null frame means nothing new to show —
    ///     unpainted since <paramref name="sinceVersion" />, nothing painted yet, or a failed capture.
    /// </summary>
    private static (byte[]? Frame, int Version) CaptureFrame(App app, string path, float scale, int sinceVersion)
    {
        var done = new TaskCompletionSource<(byte[]?, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.PostAfterFrame(() =>
        {
            try
            {
                var version = app.PaintVersion;
                done.TrySetResult(version != sinceVersion && app.CaptureUi(path, scale)
                    ? (File.ReadAllBytes(path), version)
                    : (null, version));
            }
            catch (Exception)
            {
                done.TrySetResult((null, sinceVersion));
            }
        });
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
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

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
        });

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
        var budget = MaxNodes;
        WriteWidget(json, root, 0, ref budget);
        return json.Append('}').ToString();
    }

    /// <summary>
    ///     The <c>locales</c> reply. An app without a <see cref="App.LocaleInfo" /> hook answers with an
    ///     empty list rather than an error, so a panel can tell "no localization" from "broken".
    /// </summary>
    internal static string LocalesJson((string Current, IReadOnlyList<string> Supported)? info)
    {
        var json = new StringBuilder("{\"current\":");
        Quote(json, info?.Current);
        json.Append(",\"locales\":[");
        var supported = info?.Supported ?? [];
        for (var i = 0; i < supported.Count; i++)
        {
            if (i > 0) json.Append(',');
            Quote(json, supported[i]);
        }

        return json.Append("]}").ToString();
    }

    /// <summary>The <c>semantics</c> reply for a tree.</summary>
    internal static string SemanticsTreeJson(SemanticsNode? root)
    {
        var json = new StringBuilder("{\"tree\":");
        var budget = MaxNodes;
        WriteSemantics(json, root, 0, ref budget);
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
        Quote(json, widget.GetType().Name);
        json.Append(",\"desc\":");
        Quote(json, WidgetDebug.Describe(widget));
        Bounds(json, widget.Bounds);

        json.Append(",\"children\":[");
        var first = true;
        foreach (var child in WidgetDebug.Children(widget))
        {
            if (budget <= 0) break;
            if (!first) json.Append(',');
            WriteWidget(json, child, depth + 1, ref budget);
            first = false;
        }

        json.Append("]}");
    }

    private static void WriteSemantics(StringBuilder json, SemanticsNode? node, int depth, ref int budget)
    {
        if (node is null || depth > MaxDepth || budget-- <= 0)
        {
            json.Append("null");
            return;
        }

        json.Append("{\"id\":").Append(node.Id);
        json.Append(",\"role\":");
        Quote(json, node.Role.ToString());
        json.Append(",\"label\":");
        Quote(json, node.Label);
        json.Append(",\"value\":");
        Quote(json, node.Value);
        json.Append(",\"hint\":");
        Quote(json, node.Hint);
        json.Append(",\"flags\":");
        Quote(json, node.Flags.ToString());
        json.Append(",\"actions\":");
        Quote(json, node.Actions.ToString());
        Bounds(json, node.Bounds);

        json.Append(",\"children\":[");
        for (var i = 0; i < node.Children.Count; i++)
        {
            if (budget <= 0) break;
            if (i > 0) json.Append(',');
            WriteSemantics(json, node.Children[i], depth + 1, ref budget);
        }

        json.Append("]}");
    }

    private static void Bounds(StringBuilder json, Core.Rect bounds)
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
            ? Math.Round(v, 2).ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private static string Error(string message)
    {
        var json = new StringBuilder("{\"error\":");
        Quote(json, message);
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
        foreach (var c in text)
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    // Control characters are illegal raw in JSON; widget descriptions can carry them.
                    if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else json.Append(c);
                    break;
            }

        json.Append('"');
    }
}
