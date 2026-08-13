using System.Globalization;
using System.Text.Json.Nodes;

namespace Zigote.Mcp;

/// <summary>
///     The MCP tools, each a thin typed wrapper over one or two inspect commands. The
///     descriptions are written for the model that will call them: what the tool answers with,
///     what space coordinates live in, and what to do when there is nothing to talk to.
///     <para>
///         Two conventions keep agent sessions short. Every mutating tool takes
///         <c>screenshot: true</c> to return the resulting frame in the same call — act and
///         observe in one round trip. And <c>find</c> / <c>tap_widget</c> / <c>wait_for</c> work
///         by semantic label, so the model can click "Save" without ever dumping a full tree.
///     </para>
/// </summary>
public static class Tools
{
    private const int MaxMatches = 30;

    private sealed record Tool(string Description, JsonObject Schema, Func<JsonObject, JsonObject> Run);

    private static readonly (string Name, Tool Tool)[] All =
    [
        ("launch", new Tool(
            "Build and run a Zigote app with its inspect socket on, wait for it to come up, and " +
            "remember it as the default target for every other tool. Returns the port and pid. " +
            "With watch=true the app runs under `dotnet watch`: edit a source file, save, and the " +
            "UI hot-reloads in place — no relaunch. A cold build that includes the native engine " +
            "can take minutes; raise wait_seconds rather than concluding the launch failed.",
            Schema(
                Req("project", Prop("string", "path to the app's .csproj, or its directory")),
                Opt("watch", Prop("boolean", "run under `dotnet watch` for hot reload on save")),
                Opt("preview", Prop("string", "widget type to preview on its own (sets ZIGOTE_PREVIEW), e.g. 'MyApp.SettingsPage'")),
                Opt("hide_window", Prop("boolean", "hide the app's OS window and Dock tile — use when only screenshots matter")),
                Opt("wait_seconds", Prop("integer", "how long to wait for the app to announce its port (default 120)"))),
            Launch)),

        ("stop", new Tool(
            "Stop an app previously started with `launch` — one by pid, or all of them when pid is omitted.",
            Schema(Opt("pid", Prop("integer", "pid returned by `launch`; omit to stop everything launched here"))),
            args => Text(AppHost.Stop(Int(args, "pid"))))),

        ("logs", new Tool(
            "The last lines a launched app printed — build errors, log output, unhandled " +
            "exceptions. Works after the app exits too, which is how a crash is diagnosed. Only " +
            "covers apps `launch` started.",
            Schema(
                Opt("pid", Prop("integer", "pid from `launch`; defaults to the most recent launch")),
                Opt("lines", Prop("integer", "how many trailing lines (default 50, max 400)"))),
            args => Text(AppHost.Logs(Int(args, "pid"), Int(args, "lines") ?? 50)))),

        ("screenshot", new Tool(
            "The app's current frame as a PNG image, plus its size in layout points. Taken at the " +
            "end of a frame, so a screenshot after `tap` or `type_text` shows their effect. All " +
            "coordinate tools use the layout-point space this reports.",
            Schema(PortProp(), Opt("scale", Prop("number", "pixel density, 0.1–4 (default 1; use 2 to read small text)"))),
            args => new JsonObject { ["content"] = new JsonArray(ShotContent(Port(args), Num(args, "scale", 1))) })),

        ("find", new Tool(
            "Find widgets without dumping a whole tree. label/role search the semantics tree " +
            "(label matches labels and values, case-insensitive substring); type searches the " +
            "widget tree by type name or description. Matches come back with bounds and a " +
            "tappable center point.",
            Schema(
                Opt("label", Prop("string", "substring of a semantic label or value, e.g. 'Save'")),
                Opt("role", Prop("string", "semantic role, e.g. 'Button', 'Text', 'Slider'")),
                Opt("type", Prop("string", "widget type name substring, e.g. 'TextField' — searches the widget tree instead")),
                PortProp()),
            Find)),

        ("tap_widget", new Tool(
            "Find a widget by its semantic label and click its center — the way to press a button " +
            "without doing coordinate math. Errors helpfully when the label is ambiguous " +
            "(disambiguate with role or index) or missing (check spelling with `find`).",
            Schema(
                Req("label", Prop("string", "substring of the widget's semantic label or value")),
                Opt("role", Prop("string", "narrow by semantic role, e.g. 'Button'")),
                Opt("index", Prop("integer", "which match to tap when several share the label (0-based, tree order)")),
                ShotProp(), PortProp()),
            TapWidget)),

        ("wait_for", new Tool(
            "Wait until a widget with a matching label appears (or, with gone=true, disappears) — " +
            "for dialogs that open async, pages that load, and toasts that clear. Polls the " +
            "semantics tree until the condition holds or the timeout runs out.",
            Schema(
                Req("label", Prop("string", "substring of the semantic label or value to wait for")),
                Opt("role", Prop("string", "narrow by semantic role")),
                Opt("gone", Prop("boolean", "wait for it to disappear instead")),
                Opt("timeout_seconds", Prop("number", "how long to keep polling (default 10)")),
                PortProp()),
            WaitFor)),

        ("widget_tree", new Tool(
            "The live widget tree as JSON: id, type, description, bounds (x/y/w/h in layout " +
            "points) and children per node. Ids are valid for `widget_props` until the tree is " +
            "rebuilt. Prefer `find` for locating one thing; use max_depth to keep full dumps readable.",
            Schema(PortProp(), Opt("max_depth", Prop("integer", "prune below this depth; pruned nodes report how many children were cut"))),
            WidgetTree)),

        ("semantics_tree", new Tool(
            "The accessibility tree as JSON — role, label, value, hint, flags, actions, bounds per " +
            "node. The best view of what the UI *means*; prefer `find` when looking for one thing.",
            Schema(PortProp()),
            args => Json(Send(Port(args), "semantics")))),

        ("widget_props", new Tool(
            "Everything the inspector knows about one widget, by the id widget_tree reported.",
            Schema(Req("id", Prop("integer", "widget id from widget_tree")), PortProp()),
            args => Json(Send(Port(args), $"props {(long)Num(args, "id")}")))),

        ("tap", new Tool(
            "Click at a point: a press and a release through the app's real input pipeline. " +
            "Coordinates are layout points, matching screenshot and widget bounds. Prefer " +
            "`tap_widget` when the target has a label.",
            Schema(
                Req("x", Prop("number", "layout-point x")),
                Req("y", Prop("number", "layout-point y")),
                Opt("button", Prop("string", "left (default), right or middle")),
                ShotProp(), PortProp()),
            Tap)),

        ("drag", new Tool(
            "Press at one point, move to another over several frames, release. Use for sliders, " +
            "scrolling by drag, and drag-and-drop. Note: which widget claims a drag is decided " +
            "once, at the first few points of movement.",
            Schema(
                Req("from_x", Prop("number", "start x")), Req("from_y", Prop("number", "start y")),
                Req("to_x", Prop("number", "end x")), Req("to_y", Prop("number", "end y")),
                Opt("steps", Prop("integer", "intermediate move events (default 8)")),
                ShotProp(), PortProp()),
            Drag)),

        ("scroll", new Tool(
            "Wheel ticks at a position (the pointer is moved there first, like a real wheel). " +
            "Positive dy scrolls content up (wheel rolled away from you); most lists want dy " +
            "around ±3. Use it to bring offscreen widgets into view before tapping them.",
            Schema(
                Req("x", Prop("number", "pointer x")), Req("y", Prop("number", "pointer y")),
                Req("dx", Prop("number", "horizontal ticks")), Req("dy", Prop("number", "vertical ticks")),
                ShotProp(), PortProp()),
            Scroll)),

        ("type_text", new Tool(
            "Commit text to the focused widget, verbatim. Focus a field first (tap it), then type.",
            Schema(Req("text", Prop("string", "the text to type")), ShotProp(), PortProp()),
            args => Done(args, Send(Port(args), $"input text {OneLine(Str(args, "text"))}")))),

        ("press_key", new Tool(
            "Press and release one physical key, with optional modifiers — for shortcuts (cmd+S), " +
            "navigation (Tab, Down, Enter) and dismissal (Escape). Use type_text for writing text.",
            Schema(
                Req("key", Prop("string", "key name, e.g. Enter, Escape, Tab, Down, A, F5")),
                Opt("modifiers", Prop("string", "'+'-joined: shift, ctrl, alt, cmd — e.g. 'cmd+shift'")),
                ShotProp(), PortProp()),
            PressKey)),

        ("preview_targets", new Tool(
            "The widget types this app can show on their own via `set_preview` (or `launch`'s " +
            "preview parameter).",
            Schema(PortProp()),
            args => Json(Send(Port(args), "targets")))),

        ("set_preview", new Tool(
            "Swap the previewed widget without restarting the app. Only works when the app was " +
            "started in preview mode (launch with `preview`, or `zigote preview`).",
            Schema(Req("type", Prop("string", "a type name from preview_targets")), ShotProp(), PortProp()),
            args => Done(args, Send(Port(args), $"preview {Str(args, "type")}")))),

        ("resize", new Tool(
            "Lay the app out at a device size in layout points — MediaQuery and breakpoints adapt, " +
            "so 390x844 is a real phone layout, not a scaled desktop one. Omit both dimensions to " +
            "return to the window's own size.",
            Schema(
                Opt("width", Prop("number", "1–8192")),
                Opt("height", Prop("number", "1–8192")),
                ShotProp(), PortProp()),
            Resize)),

        ("set_theme", new Tool(
            "Switch the app between dark and light theme.",
            Schema(Req("theme", Prop("string", "'dark' or 'light'")), ShotProp(), PortProp()),
            args => Done(args, Send(Port(args), $"theme {Str(args, "theme")}")))),

        ("locales", new Tool(
            "The app's active locale and the locales it supports.",
            Schema(PortProp()),
            args => Json(Send(Port(args), "locales")))),

        ("set_locale", new Tool(
            "Switch the app's locale. Needs the app to use a LocalizationsScope.",
            Schema(Req("locale", Prop("string", "a locale tag from `locales`, e.g. 'de' or 'pt-BR'")), ShotProp(), PortProp()),
            args => Done(args, Send(Port(args), $"locale {Str(args, "locale")}")))),

        ("raw_command", new Tool(
            "Escape hatch: send one raw inspect-protocol command line and return the raw reply. " +
            "For commands the typed tools don't cover (e.g. 'window hide').",
            Schema(Req("command", Prop("string", "one protocol line, e.g. 'window hide'")), PortProp()),
            args => Text(AppHost.Query(Port(args), OneLine(Str(args, "command")))))),
    ];

    // ── MCP plumbing ──────────────────────────────────────────────────────────

    public static JsonArray List()
    {
        var tools = new JsonArray();
        foreach (var (name, tool) in All)
            tools.Add(new JsonObject {
                ["name"] = name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.Schema.DeepClone(),
            });
        return tools;
    }

    public static JsonObject Call(JsonObject? @params)
    {
        var name = @params?["name"]?.GetValue<string>()
                   ?? throw new ToolError("tools/call needs a tool name");
        var arguments = @params["arguments"] as JsonObject ?? new JsonObject();

        foreach (var (candidate, tool) in All)
            if (candidate == name)
                return tool.Run(arguments);

        throw new ToolError($"unknown tool '{name}'");
    }

    public static JsonObject ErrorResult(string message)
    {
        var result = Text(message);
        result["isError"] = true;
        return result;
    }

    // ── handlers with more than one line in them ──────────────────────────────

    private static JsonObject Launch(JsonObject args)
    {
        var project = Str(args, "project");
        var watch = args["watch"]?.GetValue<bool>() == true;
        var (port, pid) = AppHost.Launch(project, StrOpt(args, "preview"), watch, Int(args, "wait_seconds") ?? 120);
        if (args["hide_window"]?.GetValue<bool>() == true) Send(port, "window hide");
        var reload = watch ? "; hot reload is on — edit, save, and screenshot again" : "";
        return Text($"launched (pid {pid}), inspect port {port} — now the default for other tools{reload}");
    }

    private static JsonObject Find(JsonObject args)
    {
        var port = Port(args);
        var label = StrOpt(args, "label");
        var role = StrOpt(args, "role");
        var type = StrOpt(args, "type");
        if (label is null && role is null && type is null)
            throw new ToolError("find wants at least one of label, role or type");
        if (type is not null && (label is not null || role is not null))
            throw new ToolError("type searches the widget tree, label/role the semantics tree — use one or the other");

        var matches = new JsonArray();
        var total = 0;
        if (type is not null)
        {
            foreach (var node in Nodes(Tree(port, "widgets")))
            {
                if (!Has(node, "type", type) && !Has(node, "desc", type)) continue;
                total++;
                if (matches.Count < MaxMatches) matches.Add(Summary(node, "type", "desc"));
            }
        }
        else
        {
            var (hits, w, h) = SemanticsMatches(port, label, role);
            foreach (var node in hits)
            {
                total++;
                if (matches.Count >= MaxMatches) continue;
                var summary = Summary(node, "role", "label");
                if (Offscreen(node, w, h)) summary["offscreen"] = true; // scrolled out — tap needs a scroll first
                matches.Add(summary);
            }
        }

        return Json(new JsonObject { ["total"] = total, ["matches"] = matches }.ToJsonString());
    }

    private static JsonObject TapWidget(JsonObject args)
    {
        var port = Port(args);
        var label = Str(args, "label");
        var (hits, w, h) = SemanticsMatches(port, label, StrOpt(args, "role"));

        var chosen = (hits.Count, Int(args, "index")) switch {
            (0, _) => throw new ToolError(
                $"nothing in the semantics tree matches '{label}' — `find` with a shorter " +
                "substring, or `wait_for` if it appears asynchronously"),
            (_, { } i) when i >= 0 && i < hits.Count => hits[i],
            (_, { } i) => throw new ToolError($"index {i} is out of range — {hits.Count} match(es)"),
            (1, null) => hits[0],
            (_, null) => throw new ToolError(
                $"'{label}' is ambiguous — {hits.Count} matches: " +
                string.Join("; ", hits.Take(6).Select((h, i) => $"[{i}] {Describe(h)}")) +
                ". Narrow with role, or pick one with index."),
        };

        if (Offscreen(chosen, w, h))
            throw new ToolError(
                $"{Describe(chosen)} is scrolled out of the {F(w)}x{F(h)} layout — a tap there " +
                "would miss. `scroll` it into view first, then tap again.");

        var (cx, cy) = Center(chosen);
        Send(port, $"input down {F(cx)} {F(cy)} left");
        Send(port, $"input up {F(cx)} {F(cy)} left");
        return Done(args, $"tapped {Describe(chosen)}");
    }

    private static JsonObject WaitFor(JsonObject args)
    {
        var port = Port(args);
        var label = Str(args, "label");
        var role = StrOpt(args, "role");
        var gone = args["gone"]?.GetValue<bool>() == true;
        var timeout = Num(args, "timeout_seconds", 10);

        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (true)
        {
            var (hits, _, _) = SemanticsMatches(port, label, role);
            if (gone ? hits.Count == 0 : hits.Count > 0)
                return Text(gone
                    ? $"'{label}' is gone"
                    : $"appeared: {string.Join("; ", hits.Take(6).Select(Describe))}");

            if (DateTime.UtcNow > deadline)
                throw new ToolError(gone
                    ? $"'{label}' was still there after {F(timeout)}s"
                    : $"'{label}' did not appear within {F(timeout)}s — `logs` and `screenshot` show what did");

            Thread.Sleep(250);
        }
    }

    private static JsonObject WidgetTree(JsonObject args)
    {
        var reply = Send(Port(args), "widgets");
        if (Int(args, "max_depth") is not { } max || max < 1) return Json(reply);

        var parsed = JsonNode.Parse(reply)!.AsObject();
        Prune(parsed["tree"], 0, max);
        return Json(parsed.ToJsonString());

        static void Prune(JsonNode? node, int depth, int max)
        {
            if (node is not JsonObject widget || widget["children"] is not JsonArray children) return;
            if (depth >= max && children.Count > 0)
            {
                widget["pruned_children"] = children.Count;
                widget["children"] = new JsonArray();
                return;
            }

            foreach (var child in children) Prune(child, depth + 1, max);
        }
    }

    private static JsonObject Tap(JsonObject args)
    {
        var port = Port(args);
        var at = $"{F(Num(args, "x"))} {F(Num(args, "y"))} {StrOpt(args, "button") ?? "left"}";
        Send(port, $"input down {at}");
        Send(port, $"input up {at}");
        return Done(args, "tapped");
    }

    private static JsonObject Scroll(JsonObject args)
    {
        var port = Port(args);
        var x = F(Num(args, "x"));
        var y = F(Num(args, "y"));
        // Wheel dispatch targets the pointer's position, not the event's own coordinates — the
        // OS always moves the mouse somewhere before it scrolls there, so injected scrolls must
        // do the same or they land wherever the pointer last was (nowhere, in a hidden window).
        Send(port, $"input move {x} {y}");
        return Done(args, Send(port, $"input scroll {x} {y} {F(Num(args, "dx"))} {F(Num(args, "dy"))}"));
    }

    private static JsonObject Drag(JsonObject args)
    {
        var port = Port(args);
        double fromX = Num(args, "from_x"), fromY = Num(args, "from_y");
        double toX = Num(args, "to_x"), toY = Num(args, "to_y");
        var steps = Math.Clamp(Int(args, "steps") ?? 8, 1, 60);

        Send(port, $"input down {F(fromX)} {F(fromY)}");
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            Send(port, $"input move {F(fromX + (toX - fromX) * t)} {F(fromY + (toY - fromY) * t)}");
            Thread.Sleep(16); // one frame apart, so the gesture arbitrates like a real one
        }

        Send(port, $"input up {F(toX)} {F(toY)}");
        return Done(args, "dragged");
    }

    private static JsonObject PressKey(JsonObject args)
    {
        var port = Port(args);
        var key = Str(args, "key");
        var mods = StrOpt(args, "modifiers") is { Length: > 0 } m ? " " + m : "";
        Send(port, $"input keydown {key}{mods}");
        Send(port, $"input keyup {key}{mods}");
        return Done(args, $"pressed {key}{mods}");
    }

    private static JsonObject Resize(JsonObject args)
    {
        var port = Port(args);
        var width = Num(args, "width", -1);
        var height = Num(args, "height", -1);
        if (width < 0 != height < 0)
            throw new ToolError("resize wants both width and height, or neither (= back to window size)");

        return Done(args, Send(port, width < 0 ? "size window" : $"size {F(width)}x{F(height)}"));
    }

    // ── semantics search ──────────────────────────────────────────────────────

    /// <summary>
    ///     Semantics nodes whose label or value contains <paramref name="label" /> (and role
    ///     matches), in tree order — plus the layout size, because the tree reports scrolled-out
    ///     content at its virtual position and a tap there would silently miss.
    /// </summary>
    private static (List<JsonObject> Hits, double W, double H) SemanticsMatches(int port, string? label, string? role)
    {
        var tree = Tree(port, "semantics");
        var w = (tree as JsonObject)?["w"]?.GetValue<double>() ?? 0;
        var h = (tree as JsonObject)?["h"]?.GetValue<double>() ?? 0;

        var hits = new List<JsonObject>();
        foreach (var node in Nodes(tree))
        {
            if (label is not null && !Has(node, "label", label) && !Has(node, "value", label)) continue;
            if (role is not null && !string.Equals(node["role"]?.GetValue<string>(), role, StringComparison.OrdinalIgnoreCase)) continue;
            hits.Add(node);
        }

        return (hits, w, h);
    }

    private static bool Offscreen(JsonObject node, double w, double h)
    {
        var (cx, cy) = Center(node);
        return cx < 0 || cy < 0 || (w > 0 && cx > w) || (h > 0 && cy > h);
    }

    private static JsonNode? Tree(int port, string command)
    {
        return JsonNode.Parse(Send(port, command))?["tree"];
    }

    private static IEnumerable<JsonObject> Nodes(JsonNode? node)
    {
        if (node is not JsonObject o) yield break;
        yield return o;
        if (o["children"] is not JsonArray children) yield break;
        foreach (var child in children)
            foreach (var hit in Nodes(child))
                yield return hit;
    }

    private static bool Has(JsonObject node, string field, string needle)
    {
        return node[field]?.GetValue<string>() is { } text &&
               text.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A match flattened for the model: identity, bounds, and the point to tap.</summary>
    private static JsonObject Summary(JsonObject node, string kindField, string textField)
    {
        var (cx, cy) = Center(node);
        var summary = new JsonObject {
            ["id"] = node["id"]?.DeepClone(),
            [kindField] = node[kindField]?.DeepClone(),
            [textField] = node[textField]?.DeepClone(),
            ["x"] = node["x"]?.DeepClone(), ["y"] = node["y"]?.DeepClone(),
            ["w"] = node["w"]?.DeepClone(), ["h"] = node["h"]?.DeepClone(),
            ["center_x"] = Math.Round(cx, 1), ["center_y"] = Math.Round(cy, 1),
        };
        if (node["value"] is { } value) summary["value"] = value.DeepClone();
        return summary;
    }

    private static (double X, double Y) Center(JsonObject node)
    {
        double N(string f) => node[f]?.GetValue<double>() ?? 0;
        return (N("x") + N("w") / 2, N("y") + N("h") / 2);
    }

    private static string Describe(JsonObject node)
    {
        var (cx, cy) = Center(node);
        var text = node["label"]?.GetValue<string>() ?? node["value"]?.GetValue<string>() ?? "";
        return $"{node["role"]?.GetValue<string>()} '{text}' at ({F(cx)},{F(cy)})";
    }

    // ── the inspect wire ──────────────────────────────────────────────────────

    private static int Port(JsonObject args)
    {
        return AppHost.Resolve(Int(args, "port"));
    }

    /// <summary>One command, one reply — with the app's own error surfaced as a tool error.</summary>
    private static string Send(int port, string command)
    {
        var reply = AppHost.Query(port, command);
        if (JsonNode.Parse(reply) is JsonObject o && o["error"]?.GetValue<string>() is { } error)
            throw new ToolError($"the app said: {error}");
        return reply;
    }

    // ── result and argument shapes ────────────────────────────────────────────

    private static JsonObject Text(string text)
    {
        return new JsonObject {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
    }

    /// <summary>A reply that already is JSON, passed through as text for the model to read.</summary>
    private static JsonObject Json(string json)
    {
        return Text(json);
    }

    /// <summary>
    ///     The result of a mutating tool: the app's answer condensed to a word, and — when the
    ///     call asked with <c>screenshot: true</c> — the resulting frame, in the same round trip.
    /// </summary>
    private static JsonObject Done(JsonObject args, string reply)
    {
        var result = Text(reply.Contains("\"ok\"") ? "ok" : reply);
        if (args["screenshot"]?.GetValue<bool>() == true)
        {
            var content = result["content"]!.AsArray();
            foreach (var item in ShotContent(Port(args), 1)) content.Add(item);
        }

        return result;
    }

    /// <summary>The frame as MCP content items: the PNG, then its size in layout points.</summary>
    private static JsonObject[] ShotContent(int port, double scale)
    {
        var reply = Send(port, $"shot {F(scale)}");
        var parsed = JsonNode.Parse(reply) as JsonObject
                     ?? throw new ToolError($"unexpected shot reply: {reply[..Math.Min(reply.Length, 120)]}");

        var bmp = Convert.FromBase64String(parsed["data"]?.GetValue<string>()
                                           ?? throw new ToolError("shot reply had no image data"));
        return
        [
            new JsonObject {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(Png.FromBmp(bmp)),
                ["mimeType"] = "image/png",
            },
            new JsonObject {
                ["type"] = "text",
                ["text"] = $"{parsed["w"]}x{parsed["h"]} layout points, rendered at {parsed["scale"]}x density",
            },
        ];
    }

    /// <summary>The wire is one command per line, so text with a newline cannot be sent as-is.</summary>
    private static string OneLine(string text)
    {
        return text.IndexOfAny(['\n', '\r']) < 0
            ? text
            : throw new ToolError(
                "text is one line on the wire — send each line as its own type_text and use " +
                "press_key Enter between them");
    }

    private static string Str(JsonObject args, string name)
    {
        return StrOpt(args, name) ?? throw new ToolError($"missing required argument '{name}'");
    }

    private static string? StrOpt(JsonObject args, string name)
    {
        return args[name]?.GetValue<string>();
    }

    private static double Num(JsonObject args, string name, double? fallback = null)
    {
        if (args[name] is { } node) return node.GetValue<double>();
        return fallback ?? throw new ToolError($"missing required argument '{name}'");
    }

    private static int? Int(JsonObject args, string name)
    {
        return args[name] is { } node ? (int)node.GetValue<double>() : null;
    }

    /// <summary>Numbers on the wire: invariant, trailing zeros dropped, never scientific.</summary>
    private static string F(double v)
    {
        return v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static JsonObject Schema(params (string Name, JsonObject Prop, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, prop, isRequired) in props)
        {
            properties[name] = prop;
            if (isRequired) required.Add(name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static (string, JsonObject, bool) Req(string name, JsonObject prop)
    {
        return (name, prop, true);
    }

    private static (string, JsonObject, bool) Opt(string name, JsonObject prop)
    {
        return (name, prop, false);
    }

    private static (string, JsonObject, bool) PortProp()
    {
        return Opt("port", Prop("integer", "inspect port of the app to address; defaults to the last `launch`"));
    }

    private static (string, JsonObject, bool) ShotProp()
    {
        return Opt("screenshot", Prop("boolean", "also return a screenshot of the result — saves a round trip"));
    }

    private static JsonObject Prop(string type, string description)
    {
        return new JsonObject { ["type"] = type, ["description"] = description };
    }
}
