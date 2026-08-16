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

    private static readonly (string Name, Tool Tool)[] All = [
        ("launch", new Tool(
            Description:
            "Build and run a Zigote app with its inspect socket on, wait for it to come up, and " +
            "remember it as the default target for every other tool. Returns the port and pid. " +
            "With watch=true the app runs under `dotnet watch`: edit a source file, save, and the " +
            "UI hot-reloads in place — no relaunch. A cold build that includes the native engine " +
            "can take minutes; raise wait_seconds rather than concluding the launch failed.",
            Schema: Schema(
                Req(
                    name: "project",
                    prop: Prop(
                        type: "string",
                        description: "path to the app's .csproj, or its directory"
                    )
                ),
                Opt(
                    name: "watch",
                    prop: Prop(
                        type: "boolean",
                        description: "run under `dotnet watch` for hot reload on save"
                    )
                ),
                Opt(
                    name: "preview",
                    prop: Prop(
                        type: "string",
                        description:
                        "widget type to preview on its own (sets ZIGOTE_PREVIEW), e.g. 'MyApp.SettingsPage'"
                    )
                ),
                Opt(
                    name: "hide_window",
                    prop: Prop(
                        type: "boolean",
                        description:
                        "hide the app's OS window and Dock tile — use when only screenshots matter"
                    )
                ),
                Opt(
                    name: "wait_seconds",
                    prop: Prop(
                        type: "integer",
                        description:
                        "how long to wait for the app to announce its port (default 120)"
                    )
                )
            ),
            Run: Launch
        )),

        ("stop", new Tool(
            Description:
            "Stop an app previously started with `launch` — one by pid, or all of them when pid is omitted.",
            Schema: Schema(
                Opt(
                    name: "pid",
                    prop: Prop(
                        type: "integer",
                        description:
                        "pid returned by `launch`; omit to stop everything launched here"
                    )
                )
            ),
            Run: args => Text(AppHost.Stop(Int(args: args, name: "pid")))
        )),

        ("logs", new Tool(
            Description:
            "The last lines a launched app printed — build errors, log output, unhandled " +
            "exceptions. Works after the app exits too, which is how a crash is diagnosed. Only " +
            "covers apps `launch` started.",
            Schema: Schema(
                Opt(
                    name: "pid",
                    prop: Prop(
                        type: "integer",
                        description: "pid from `launch`; defaults to the most recent launch"
                    )
                ),
                Opt(
                    name: "lines",
                    prop: Prop(
                        type: "integer",
                        description: "how many trailing lines (default 50, max 400)"
                    )
                )
            ),
            Run: args => Text(
                AppHost.Logs(
                    pid: Int(args: args, name: "pid"),
                    lines: Int(args: args, name: "lines") ?? 50
                )
            )
        )),

        ("screenshot", new Tool(
            Description:
            "The app's current frame as a PNG image, plus its size in layout points. Taken at the " +
            "end of a frame, so a screenshot after `tap` or `type_text` shows their effect. All " +
            "coordinate tools use the layout-point space this reports.",
            Schema: Schema(
                PortProp(),
                Opt(
                    name: "scale",
                    prop: Prop(
                        type: "number",
                        description: "pixel density, 0.1–4 (default 1; use 2 to read small text)"
                    )
                )
            ),
            Run: args => new JsonObject {
                ["content"] = new JsonArray(
                    ShotContent(
                        port: Port(args),
                        scale: Num(args: args, name: "scale", fallback: 1)
                    )
                ),
            }
        )),

        ("find", new Tool(
            Description:
            "Find widgets without dumping a whole tree. label/role search the semantics tree " +
            "(label matches labels and values, case-insensitive substring); type searches the " +
            "widget tree by type name or description. Matches come back with bounds and a " +
            "tappable center point.",
            Schema: Schema(
                Opt(
                    name: "label",
                    prop: Prop(
                        type: "string",
                        description: "substring of a semantic label or value, e.g. 'Save'"
                    )
                ),
                Opt(
                    name: "role",
                    prop: Prop(
                        type: "string",
                        description: "semantic role, e.g. 'Button', 'Text', 'Slider'"
                    )
                ),
                Opt(
                    name: "type",
                    prop: Prop(
                        type: "string",
                        description:
                        "widget type name substring, e.g. 'TextField' — searches the widget tree instead"
                    )
                ),
                PortProp()
            ),
            Run: Find
        )),

        ("tap_widget", new Tool(
            Description:
            "Find a widget by its semantic label and click its center — the way to press a button " +
            "without doing coordinate math. Errors helpfully when the label is ambiguous " +
            "(disambiguate with role or index) or missing (check spelling with `find`).",
            Schema: Schema(
                Req(
                    name: "label",
                    prop: Prop(
                        type: "string",
                        description: "substring of the widget's semantic label or value"
                    )
                ),
                Opt(
                    name: "role",
                    prop: Prop(
                        type: "string",
                        description: "narrow by semantic role, e.g. 'Button'"
                    )
                ),
                Opt(
                    name: "index",
                    prop: Prop(
                        type: "integer",
                        description:
                        "which match to tap when several share the label (0-based, tree order)"
                    )
                ),
                ShotProp(),
                PortProp()
            ),
            Run: TapWidget
        )),

        ("wait_for", new Tool(
            Description:
            "Wait until a widget with a matching label appears (or, with gone=true, disappears) — " +
            "for dialogs that open async, pages that load, and toasts that clear. Polls the " +
            "semantics tree until the condition holds or the timeout runs out.",
            Schema: Schema(
                Req(
                    name: "label",
                    prop: Prop(
                        type: "string",
                        description: "substring of the semantic label or value to wait for"
                    )
                ),
                Opt(
                    name: "role",
                    prop: Prop(type: "string", description: "narrow by semantic role")
                ),
                Opt(
                    name: "gone",
                    prop: Prop(type: "boolean", description: "wait for it to disappear instead")
                ),
                Opt(
                    name: "timeout_seconds",
                    prop: Prop(type: "number", description: "how long to keep polling (default 10)")
                ),
                PortProp()
            ),
            Run: WaitFor
        )),

        ("widget_tree", new Tool(
            Description:
            "The live widget tree as JSON: id, type, description, bounds (x/y/w/h in layout " +
            "points) and children per node. Ids are valid for `widget_props` until the tree is " +
            "rebuilt. Prefer `find` for locating one thing; use max_depth to keep full dumps readable.",
            Schema: Schema(
                PortProp(),
                Opt(
                    name: "max_depth",
                    prop: Prop(
                        type: "integer",
                        description:
                        "prune below this depth; pruned nodes report how many children were cut"
                    )
                )
            ),
            Run: WidgetTree
        )),

        ("semantics_tree", new Tool(
            Description:
            "The accessibility tree as JSON — role, label, value, hint, flags, actions, bounds per " +
            "node. The best view of what the UI *means*; prefer `find` when looking for one thing.",
            Schema: Schema(PortProp()),
            Run: args => Json(Send(port: Port(args), command: "semantics"))
        )),

        ("widget_props", new Tool(
            Description:
            "Everything the inspector knows about one widget, by the id widget_tree reported.",
            Schema: Schema(
                Req(
                    name: "id",
                    prop: Prop(type: "integer", description: "widget id from widget_tree")
                ),
                PortProp()
            ),
            Run: args => Json(
                Send(port: Port(args), command: $"props {(long)Num(args: args, name: "id")}")
            )
        )),

        ("tap", new Tool(
            Description:
            "Click at a point: a press and a release through the app's real input pipeline. " +
            "Coordinates are layout points, matching screenshot and widget bounds. Prefer " +
            "`tap_widget` when the target has a label.",
            Schema: Schema(
                Req(name: "x", prop: Prop(type: "number", description: "layout-point x")),
                Req(name: "y", prop: Prop(type: "number", description: "layout-point y")),
                Opt(
                    name: "button",
                    prop: Prop(type: "string", description: "left (default), right or middle")
                ),
                ShotProp(),
                PortProp()
            ),
            Run: Tap
        )),

        ("drag", new Tool(
            Description:
            "Press at one point, move to another over several frames, release. Use for sliders, " +
            "scrolling by drag, and drag-and-drop. Note: which widget claims a drag is decided " +
            "once, at the first few points of movement.",
            Schema: Schema(
                Req(name: "from_x", prop: Prop(type: "number", description: "start x")),
                Req(name: "from_y", prop: Prop(type: "number", description: "start y")),
                Req(name: "to_x", prop: Prop(type: "number", description: "end x")),
                Req(name: "to_y", prop: Prop(type: "number", description: "end y")),
                Opt(
                    name: "steps",
                    prop: Prop(type: "integer", description: "intermediate move events (default 8)")
                ),
                ShotProp(),
                PortProp()
            ),
            Run: Drag
        )),

        ("scroll", new Tool(
            Description:
            "Wheel ticks at a position (the pointer is moved there first, like a real wheel). " +
            "Positive dy scrolls content up (wheel rolled away from you); most lists want dy " +
            "around ±3. Use it to bring offscreen widgets into view before tapping them.",
            Schema: Schema(
                Req(name: "x", prop: Prop(type: "number", description: "pointer x")),
                Req(name: "y", prop: Prop(type: "number", description: "pointer y")),
                Req(name: "dx", prop: Prop(type: "number", description: "horizontal ticks")),
                Req(name: "dy", prop: Prop(type: "number", description: "vertical ticks")),
                ShotProp(),
                PortProp()
            ),
            Run: Scroll
        )),

        ("type_text", new Tool(
            Description:
            "Commit text to the focused widget, verbatim. Focus a field first (tap it), then type.",
            Schema: Schema(
                Req(name: "text", prop: Prop(type: "string", description: "the text to type")),
                ShotProp(),
                PortProp()
            ),
            Run: args => Done(
                args: args,
                reply: Send(
                    port: Port(args),
                    command: $"input text {OneLine(Str(args: args, name: "text"))}"
                )
            )
        )),

        ("press_key", new Tool(
            Description:
            "Press and release one physical key, with optional modifiers — for shortcuts (cmd+S), " +
            "navigation (Tab, Down, Enter) and dismissal (Escape). Use type_text for writing text.",
            Schema: Schema(
                Req(
                    name: "key",
                    prop: Prop(
                        type: "string",
                        description: "key name, e.g. Enter, Escape, Tab, Down, A, F5"
                    )
                ),
                Opt(
                    name: "modifiers",
                    prop: Prop(
                        type: "string",
                        description: "'+'-joined: shift, ctrl, alt, cmd — e.g. 'cmd+shift'"
                    )
                ),
                ShotProp(),
                PortProp()
            ),
            Run: PressKey
        )),

        ("preview_targets", new Tool(
            Description:
            "The widget types this app can show on their own via `set_preview` (or `launch`'s " +
            "preview parameter), each with the name and device size its [Preview] attribute gave " +
            "it and the properties it takes.",
            Schema: Schema(PortProp()),
            Run: args => Json(Send(port: Port(args), command: "previews"))
        )),

        ("set_preview", new Tool(
            Description:
            "Swap the previewed widget without restarting the app. Only works when the app was " +
            "started in preview mode (launch with `preview`, or `zigote preview`).",
            Schema: Schema(
                Req(
                    name: "type",
                    prop: Prop(
                        type: "string",
                        description:
                        "a target name from preview_targets, optionally with its properties set: " +
                        "'My.App.Card?title=Espresso&sale=true' (values URL-encoded)"
                    )
                ),
                ShotProp(),
                PortProp()
            ),
            Run: args => Done(
                args: args,
                reply: Send(port: Port(args), command: $"preview {Str(args: args, name: "type")}")
            )
        )),

        ("resize", new Tool(
            Description:
            "Lay the app out at a device size in layout points — MediaQuery and breakpoints adapt, " +
            "so 390x844 is a real phone layout, not a scaled desktop one. Omit both dimensions to " +
            "return to the window's own size.",
            Schema: Schema(
                Opt(name: "width", prop: Prop(type: "number", description: "1–8192")),
                Opt(name: "height", prop: Prop(type: "number", description: "1–8192")),
                ShotProp(),
                PortProp()
            ),
            Run: Resize
        )),

        ("set_theme", new Tool(
            Description: "Switch the app between dark and light theme.",
            Schema: Schema(
                Req(name: "theme", prop: Prop(type: "string", description: "'dark' or 'light'")),
                ShotProp(),
                PortProp()
            ),
            Run: args => Done(
                args: args,
                reply: Send(port: Port(args), command: $"theme {Str(args: args, name: "theme")}")
            )
        )),

        ("locales", new Tool(
            Description: "The app's active locale and the locales it supports.",
            Schema: Schema(PortProp()),
            Run: args => Json(Send(port: Port(args), command: "locales"))
        )),

        ("set_locale", new Tool(
            Description: "Switch the app's locale. Needs the app to use a LocalizationsScope.",
            Schema: Schema(
                Req(
                    name: "locale",
                    prop: Prop(
                        type: "string",
                        description: "a locale tag from `locales`, e.g. 'de' or 'pt-BR'"
                    )
                ),
                ShotProp(),
                PortProp()
            ),
            Run: args => Done(
                args: args,
                reply: Send(port: Port(args), command: $"locale {Str(args: args, name: "locale")}")
            )
        )),

        ("raw_command", new Tool(
            Description:
            "Escape hatch: send one raw inspect-protocol command line and return the raw reply. " +
            "For commands the typed tools don't cover (e.g. 'window hide').",
            Schema: Schema(
                Req(
                    name: "command",
                    prop: Prop(type: "string", description: "one protocol line, e.g. 'window hide'")
                ),
                PortProp()
            ),
            Run: args => Text(
                AppHost.Query(port: Port(args), command: OneLine(Str(args: args, name: "command")))
            )
        )),
    ];

    // ── MCP plumbing ──────────────────────────────────────────────────────────

    public static JsonArray List()
    {
        var tools = new JsonArray();
        foreach ((string name, var tool) in All)
        {
            tools.Add(
                new JsonObject {
                    ["name"] = name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = tool.Schema.DeepClone(),
                }
            );
        }

        return tools;
    }

    public static JsonObject Call(JsonObject? @params)
    {
        string name = @params?["name"]?.GetValue<string>()
                      ?? throw new ToolError("tools/call needs a tool name");
        var arguments = @params["arguments"] as JsonObject ?? new JsonObject();

        foreach ((string candidate, var tool) in All)
        {
            if (candidate == name)
                return tool.Run(arguments);
        }

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
        string project = Str(args: args, name: "project");
        bool watch = args["watch"]?.GetValue<bool>() == true;
        (int port, int pid) = AppHost.Launch(
            project: project,
            preview: StrOpt(args: args, name: "preview"),
            watch: watch,
            waitSeconds: Int(args: args, name: "wait_seconds") ?? 120
        );
        if (args["hide_window"]?.GetValue<bool>() == true) Send(port: port, command: "window hide");
        string reload = watch ? "; hot reload is on — edit, save, and screenshot again" : "";
        return Text(
            $"launched (pid {pid}), inspect port {port} — now the default for other tools{reload}"
        );
    }

    private static JsonObject Find(JsonObject args)
    {
        int port = Port(args);
        string? label = StrOpt(args: args, name: "label");
        string? role = StrOpt(args: args, name: "role");
        string? type = StrOpt(args: args, name: "type");
        if (label is null && role is null && type is null)
            throw new ToolError("find wants at least one of label, role or type");
        if (type is not null && (label is not null || role is not null))
        {
            throw new ToolError(
                "type searches the widget tree, label/role the semantics tree — use one or the other"
            );
        }

        var matches = new JsonArray();
        int total = 0;
        if (type is not null)
        {
            foreach (var node in Nodes(Tree(port: port, command: "widgets")))
            {
                if (!Has(node: node, field: "type", needle: type) && !Has(
                        node: node,
                        field: "desc",
                        needle: type
                    )) continue;
                total++;
                if (matches.Count < MaxMatches)
                    matches.Add(Summary(node: node, kindField: "type", textField: "desc"));
            }
        }
        else
        {
            (var hits, double w, double h) = SemanticsMatches(port: port, label: label, role: role);
            foreach (var node in hits)
            {
                total++;
                if (matches.Count >= MaxMatches) continue;
                var summary = Summary(node: node, kindField: "role", textField: "label");
                if (Offscreen(node: node, w: w, h: h))
                    summary["offscreen"] = true; // scrolled out — tap needs a scroll first
                matches.Add(summary);
            }
        }

        return Json(
            new JsonObject {
                ["total"] = total,
                ["matches"] = matches,
            }.ToJsonString()
        );
    }

    private static JsonObject TapWidget(JsonObject args)
    {
        int port = Port(args);
        string label = Str(args: args, name: "label");
        (var hits, double w, double h) = SemanticsMatches(
            port: port,
            label: label,
            role: StrOpt(args: args, name: "role")
        );

        var chosen = (hits.Count, Int(args: args, name: "index")) switch {
            (0, _) => throw new ToolError(
                $"nothing in the semantics tree matches '{label}' — `find` with a shorter " +
                "substring, or `wait_for` if it appears asynchronously"
            ),
            (_, { } i) when i >= 0 && i < hits.Count => hits[i],
            (_, { } i) => throw new ToolError(
                $"index {i} is out of range — {hits.Count} match(es)"
            ),
            (1, null) => hits[0],
            (_, null) => throw new ToolError(
                $"'{label}' is ambiguous — {hits.Count} matches: " +
                string.Join(
                    separator: "; ",
                    values: hits.Take(6).Select((h, i) => $"[{i}] {Describe(h)}")
                ) +
                ". Narrow with role, or pick one with index."
            ),
        };

        if (Offscreen(node: chosen, w: w, h: h))
        {
            throw new ToolError(
                $"{Describe(chosen)} is scrolled out of the {F(w)}x{F(h)} layout — a tap there " +
                "would miss. `scroll` it into view first, then tap again."
            );
        }

        (double cx, double cy) = Center(chosen);
        Send(port: port, command: $"input down {F(cx)} {F(cy)} left");
        Send(port: port, command: $"input up {F(cx)} {F(cy)} left");
        return Done(args: args, reply: $"tapped {Describe(chosen)}");
    }

    private static JsonObject WaitFor(JsonObject args)
    {
        int port = Port(args);
        string label = Str(args: args, name: "label");
        string? role = StrOpt(args: args, name: "role");
        bool gone = args["gone"]?.GetValue<bool>() == true;
        double timeout = Num(args: args, name: "timeout_seconds", fallback: 10);

        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (true)
        {
            var (hits, _, _) = SemanticsMatches(port: port, label: label, role: role);
            if (gone ? hits.Count == 0 : hits.Count > 0)
            {
                return Text(
                    gone
                        ? $"'{label}' is gone"
                        : $"appeared: {string.Join(separator: "; ", values: hits.Take(6).Select(Describe))}"
                );
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new ToolError(
                    gone
                        ? $"'{label}' was still there after {F(timeout)}s"
                        : $"'{label}' did not appear within {F(timeout)}s — `logs` and `screenshot` show what did"
                );
            }

            Thread.Sleep(250);
        }
    }

    private static JsonObject WidgetTree(JsonObject args)
    {
        string reply = Send(port: Port(args), command: "widgets");
        if (Int(args: args, name: "max_depth") is not { } max || max < 1) return Json(reply);

        var parsed = JsonNode.Parse(reply)!.AsObject();
        Prune(node: parsed["tree"], depth: 0, max: max);
        return Json(parsed.ToJsonString());

        static void Prune(JsonNode? node, int depth, int max)
        {
            if (node is not JsonObject widget ||
                widget["children"] is not JsonArray children) return;
            if (depth >= max && children.Count > 0)
            {
                widget["pruned_children"] = children.Count;
                widget["children"] = new JsonArray();
                return;
            }

            foreach (var child in children) Prune(node: child, depth: depth + 1, max: max);
        }
    }

    private static JsonObject Tap(JsonObject args)
    {
        int port = Port(args);
        string at =
            $"{F(Num(args: args, name: "x"))} {F(Num(args: args, name: "y"))} {StrOpt(args: args, name: "button") ?? "left"}";
        Send(port: port, command: $"input down {at}");
        Send(port: port, command: $"input up {at}");
        return Done(args: args, reply: "tapped");
    }

    private static JsonObject Scroll(JsonObject args)
    {
        int port = Port(args);
        string x = F(Num(args: args, name: "x"));
        string y = F(Num(args: args, name: "y"));
        // Wheel dispatch targets the pointer's position, not the event's own coordinates — the
        // OS always moves the mouse somewhere before it scrolls there, so injected scrolls must
        // do the same or they land wherever the pointer last was (nowhere, in a hidden window).
        Send(port: port, command: $"input move {x} {y}");
        return Done(
            args: args,
            reply: Send(
                port: port,
                command:
                $"input scroll {x} {y} {F(Num(args: args, name: "dx"))} {F(Num(args: args, name: "dy"))}"
            )
        );
    }

    private static JsonObject Drag(JsonObject args)
    {
        int port = Port(args);
        double fromX = Num(args: args, name: "from_x"), fromY = Num(args: args, name: "from_y");
        double toX = Num(args: args, name: "to_x"), toY = Num(args: args, name: "to_y");
        int steps = Math.Clamp(value: Int(args: args, name: "steps") ?? 8, min: 1, max: 60);

        Send(port: port, command: $"input down {F(fromX)} {F(fromY)}");
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            Send(
                port: port,
                command:
                $"input move {F(fromX + ((toX - fromX) * t))} {F(fromY + ((toY - fromY) * t))}"
            );
            Thread.Sleep(16); // one frame apart, so the gesture arbitrates like a real one
        }

        Send(port: port, command: $"input up {F(toX)} {F(toY)}");
        return Done(args: args, reply: "dragged");
    }

    private static JsonObject PressKey(JsonObject args)
    {
        int port = Port(args);
        string key = Str(args: args, name: "key");
        string mods = StrOpt(args: args, name: "modifiers") is { Length: > 0 } m ? " " + m : "";
        Send(port: port, command: $"input keydown {key}{mods}");
        Send(port: port, command: $"input keyup {key}{mods}");
        return Done(args: args, reply: $"pressed {key}{mods}");
    }

    private static JsonObject Resize(JsonObject args)
    {
        int port = Port(args);
        double width = Num(args: args, name: "width", fallback: -1);
        double height = Num(args: args, name: "height", fallback: -1);
        if ((width < 0) != (height < 0))
        {
            throw new ToolError(
                "resize wants both width and height, or neither (= back to window size)"
            );
        }

        return Done(
            args: args,
            reply: Send(
                port: port,
                command: width < 0 ? "size window" : $"size {F(width)}x{F(height)}"
            )
        );
    }

    // ── semantics search ──────────────────────────────────────────────────────

    /// <summary>
    ///     Semantics nodes whose label or value contains <paramref name="label" /> (and role
    ///     matches), in tree order — plus the layout size, because the tree reports scrolled-out
    ///     content at its virtual position and a tap there would silently miss.
    /// </summary>
    private static (List<JsonObject> Hits, double W, double H) SemanticsMatches(int port,
        string? label, string? role)
    {
        var tree = Tree(port: port, command: "semantics");
        double w = (tree as JsonObject)?["w"]?.GetValue<double>() ?? 0;
        double h = (tree as JsonObject)?["h"]?.GetValue<double>() ?? 0;

        var hits = new List<JsonObject>();
        foreach (var node in Nodes(tree))
        {
            if (label is not null && !Has(node: node, field: "label", needle: label) && !Has(
                    node: node,
                    field: "value",
                    needle: label
                )) continue;
            if (role is not null && !string.Equals(
                    a: node["role"]?.GetValue<string>(),
                    b: role,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) continue;
            hits.Add(node);
        }

        return (hits, w, h);
    }

    private static bool Offscreen(JsonObject node, double w, double h)
    {
        (double cx, double cy) = Center(node);
        return cx < 0 || cy < 0 || (w > 0 && cx > w) || (h > 0 && cy > h);
    }

    private static JsonNode? Tree(int port, string command) =>
        JsonNode.Parse(Send(port: port, command: command))?["tree"];

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
               text.Contains(value: needle, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A match flattened for the model: identity, bounds, and the point to tap.</summary>
    private static JsonObject Summary(JsonObject node, string kindField, string textField)
    {
        (double cx, double cy) = Center(node);
        var summary = new JsonObject {
            ["id"] = node["id"]?.DeepClone(),
            [kindField] = node[kindField]?.DeepClone(),
            [textField] = node[textField]?.DeepClone(),
            ["x"] = node["x"]?.DeepClone(),
            ["y"] = node["y"]?.DeepClone(),
            ["w"] = node["w"]?.DeepClone(),
            ["h"] = node["h"]?.DeepClone(),
            ["center_x"] = Math.Round(value: cx, digits: 1),
            ["center_y"] = Math.Round(value: cy, digits: 1),
        };
        if (node["value"] is { } value) summary["value"] = value.DeepClone();
        return summary;
    }

    private static (double X, double Y) Center(JsonObject node)
    {
        double N(string f) => node[f]?.GetValue<double>() ?? 0;
        return (N("x") + (N("w") / 2), N("y") + (N("h") / 2));
    }

    private static string Describe(JsonObject node)
    {
        (double cx, double cy) = Center(node);
        string text = node["label"]?.GetValue<string>() ?? node["value"]?.GetValue<string>() ?? "";
        return $"{node["role"]?.GetValue<string>()} '{text}' at ({F(cx)},{F(cy)})";
    }

    // ── the inspect wire ──────────────────────────────────────────────────────

    private static int Port(JsonObject args) => AppHost.Resolve(Int(args: args, name: "port"));

    /// <summary>One command, one reply — with the app's own error surfaced as a tool error.</summary>
    private static string Send(int port, string command)
    {
        string reply = AppHost.Query(port: port, command: command);
        if (JsonNode.Parse(reply) is JsonObject o && o["error"]?.GetValue<string>() is { } error)
            throw new ToolError($"the app said: {error}");
        return reply;
    }

    // ── result and argument shapes ────────────────────────────────────────────

    private static JsonObject Text(string text)
    {
        return new JsonObject {
            ["content"] = new JsonArray(
                new JsonObject {
                    ["type"] = "text",
                    ["text"] = text,
                }
            ),
        };
    }

    /// <summary>A reply that already is JSON, passed through as text for the model to read.</summary>
    private static JsonObject Json(string json) => Text(json);

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
            foreach (var item in ShotContent(port: Port(args), scale: 1)) content.Add(item);
        }

        return result;
    }

    /// <summary>The frame as MCP content items: the PNG, then its size in layout points.</summary>
    private static JsonObject[] ShotContent(int port, double scale)
    {
        string reply = Send(port: port, command: $"shot {F(scale)}");
        var parsed = JsonNode.Parse(reply) as JsonObject
                     ?? throw new ToolError(
                         $"unexpected shot reply: {reply[..Math.Min(val1: reply.Length, val2: 120)]}"
                     );

        byte[] bmp = Convert.FromBase64String(
            parsed["data"]?.GetValue<string>()
            ?? throw new ToolError("shot reply had no image data")
        );
        return [
            new JsonObject {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(Png.FromBmp(bmp)),
                ["mimeType"] = "image/png",
            },
            new JsonObject {
                ["type"] = "text",
                ["text"] =
                    $"{parsed["w"]}x{parsed["h"]} layout points, rendered at {parsed["scale"]}x density",
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
                "press_key Enter between them"
            );
    }

    private static string Str(JsonObject args, string name) => StrOpt(args: args, name: name) ??
                                                               throw new ToolError(
                                                                   $"missing required argument '{name}'"
                                                               );

    private static string? StrOpt(JsonObject args, string name) => args[name]?.GetValue<string>();

    private static double Num(JsonObject args, string name, double? fallback = null)
    {
        if (args[name] is { } node) return node.GetValue<double>();
        return fallback ?? throw new ToolError($"missing required argument '{name}'");
    }

    private static int? Int(JsonObject args, string name) =>
        args[name] is { } node ? (int)node.GetValue<double>() : null;

    /// <summary>Numbers on the wire: invariant, trailing zeros dropped, never scientific.</summary>
    private static string F(double v) => v.ToString(
        format: "0.##",
        provider: CultureInfo.InvariantCulture
    );

    private static JsonObject Schema(params (string Name, JsonObject Prop, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach ((string name, var prop, bool isRequired) in props)
        {
            properties[name] = prop;
            if (isRequired) required.Add(name);
        }

        var schema = new JsonObject {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static (string, JsonObject, bool) Req(string name, JsonObject prop) =>
        (name, prop, true);

    private static (string, JsonObject, bool) Opt(string name, JsonObject prop) =>
        (name, prop, false);

    private static (string, JsonObject, bool) PortProp() => Opt(
        name: "port",
        prop: Prop(
            type: "integer",
            description: "inspect port of the app to address; defaults to the last `launch`"
        )
    );

    private static (string, JsonObject, bool) ShotProp() => Opt(
        name: "screenshot",
        prop: Prop(
            type: "boolean",
            description: "also return a screenshot of the result — saves a round trip"
        )
    );

    private static JsonObject Prop(string type, string description) => new() {
        ["type"] = type,
        ["description"] = description,
    };

    private sealed record Tool(
        string Description,
        JsonObject Schema,
        Func<JsonObject, JsonObject> Run);
}
