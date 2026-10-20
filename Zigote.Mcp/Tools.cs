using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zigote.Mcp;

/// <summary>
///     The MCP tools, each a thin typed wrapper over one or two inspect commands. The
///     descriptions are written for the model that will call them: what the tool answers with,
///     what space coordinates live in, and what to do when there is nothing to talk to.
///     <para>
///         Everything here is priced in the caller's context window, so three conventions keep
///         sessions short. Every mutating tool takes <c>screenshot: true</c> to return the
///         resulting frame in the same call, and <c>batch</c> runs a whole act-wait-observe
///         sequence in one. <c>find</c> / <c>tap_widget</c> / <c>wait_for</c> work by semantic
///         label, so the model can click "Save" without ever dumping a tree. And what does get
///         dumped is an indented outline, not the wire JSON — the same tree at a fraction of the
///         tokens (<c>raw_command</c> still returns the JSON when a script wants it).
///     </para>
/// </summary>
public static partial class Tools
{
    private const int MaxMatches = 30;

    // An outline longer than this is a dump nobody reads; the cut-off line says how to narrow.
    private const int MaxOutlineLines = 400;

    // Widget nesting is two JSON levels per widget (object + children array), and the app
    // serialises up to 256 widgets deep — the parser's default of 64 failed on any real page.
    private static readonly JsonDocumentOptions Deep = new() { MaxDepth = 1024 };

    private static readonly (string Name, Tool Tool)[] All = [
        ("launch", new Tool(
            Description:
            "Build and run a Zigote app with its inspect socket on and make it the default " +
            "target of every other tool. watch=true runs under `dotnet watch`: save a source " +
            "file and the UI hot-reloads in place. A cold build with the native engine can take " +
            "minutes — raise wait_seconds rather than concluding the launch failed.",
            Schema: Schema(
                Req(
                    name: "project",
                    prop: Prop(type: "string", description: ".csproj or its directory")
                ),
                Opt(name: "watch", prop: Prop(type: "boolean", description: "hot reload on save")),
                Opt(
                    name: "preview",
                    prop: Prop(
                        type: "string",
                        description: "widget type to show on its own, e.g. 'MyApp.SettingsPage'"
                    )
                ),
                Opt(
                    name: "hide_window",
                    prop: Prop(type: "boolean", description: "no OS window — screenshots only")
                ),
                Opt(
                    name: "env",
                    prop: Prop(
                        type: "object",
                        description:
                        "extra environment variables, e.g. {\"ZIGOTE_CONTINUOUS\":\"1\"} for " +
                        "meaningful `stats` frame times"
                    )
                ),
                Opt(
                    name: "wait_seconds",
                    prop: Prop(type: "integer", description: "wait for the port (default 120)")
                )
            ),
            Run: Launch
        )),

        ("stop", new Tool(
            Description: "Stop an app `launch` started — by pid, or all of them.",
            Schema: Schema(
                Opt(name: "pid", prop: Prop(type: "integer", description: "omit for all"))
            ),
            Run: args => Text(AppHost.Stop(Int(args: args, name: "pid")))
        )),

        ("build", new Tool(
            Description:
            "dotnet build a project and return only its diagnostics: errors as `file:line code " +
            "message`, warnings counted by code (warnings=true lists them), each once. Cheaper " +
            "than reading build output, and the way to check a change compiles before `launch`.",
            Schema: Schema(
                Opt(name: "project", prop: Prop(type: "string", description: ".csproj, .sln/.slnf or directory (default '.')")),
                Opt(name: "configuration", prop: Prop(type: "string", description: "Debug (default) or Release")),
                Opt(name: "warnings", prop: Prop(type: "boolean", description: "list warnings instead of counting them")),
                Opt(
                    name: "args",
                    prop: Prop(type: "array", description: "extra dotnet build args, e.g. [\"-p:ZigoteContainer=true\"]", items: "string")
                ),
                Opt(name: "timeout_seconds", prop: Prop(type: "integer", description: "default 600"))
            ),
            Run: Build
        )),

        ("doctor", new Tool(
            Description:
            "Check this machine for what Zigote development needs (.NET SDK, native engine, zig, " +
            "Android workload, JDK, Android SDK…) — the passing checks in one line, every problem " +
            "with its fix. Run it first when a build or launch fails for a reason the code does not explain.",
            Schema: Schema(
                Opt(name: "dir", prop: Prop(type: "string", description: "app directory (default: working directory)"))
            ),
            Run: Doctor
        )),

        ("logs", new Tool(
            Description:
            "What a launched app printed — build errors, log output, unhandled exceptions. Works " +
            "after the app exits, which is how a crash is diagnosed. Repeated lines are folded.",
            Schema: Schema(
                Opt(
                    name: "lines",
                    prop: Prop(type: "integer", description: "trailing lines (default 50, max 400)")
                ),
                Opt(
                    name: "filter",
                    prop: Prop(
                        type: "string",
                        description:
                        "keep only lines matching this case-insensitive regex, e.g. 'error|exception'"
                    )
                ),
                Opt(
                    name: "new_only",
                    prop: Prop(
                        type: "boolean",
                        description: "only lines printed since the previous `logs` call"
                    )
                ),
                Opt(
                    name: "pid",
                    prop: Prop(type: "integer", description: "default: most recent launch")
                )
            ),
            Run: args => Text(
                AppHost.Logs(
                    pid: Int(args: args, name: "pid"),
                    lines: Int(args: args, name: "lines") ?? 50,
                    filter: StrOpt(args: args, name: "filter"),
                    newOnly: Flag(args: args, name: "new_only")
                )
            )
        )),

        ("info", new Tool(
            Description:
            "The app's state in one line: layout size, theme, locale, focused and hovered " +
            "widget, open overlays (dialogs, menus), widget count and tree depth, and whether " +
            "anything is animating. The cheap first look, before any tree or screenshot.",
            Schema: Schema(PortProp()),
            Run: args => Text(Send(port: Port(args), command: "info"))
        )),

        ("windows", new Tool(
            Description:
            "The app's open OS windows (id, title, layout size, which is main) and which one the " +
            "other tools currently address. Pass `select` with an id to point screenshot, the " +
            "trees, find, tap, type, scroll and the other per-window tools at that window; " +
            "select 0 goes back to the main window. Theme, locale, stats and resize stay app-wide.",
            Schema: Schema(
                Opt(
                    name: "select",
                    prop: Prop(
                        type: "integer",
                        description: "window id to address from now on; 0 = main"
                    )
                ),
                PortProp()
            ),
            Run: Windows
        )),

        ("screenshot", new Tool(
            Description:
            "The current frame as a PNG plus its size in layout points — the space every " +
            "coordinate tool uses. Waits for running animations to finish (up to 1 s). Crop with " +
            "`label` or `region` when only one part matters: a smaller image costs fewer tokens.",
            Schema: Schema(
                Opt(
                    name: "label",
                    prop: Prop(
                        type: "string",
                        description: "crop to the widget with this semantic label"
                    )
                ),
                Opt(
                    name: "region",
                    prop: Prop(
                        type: "array",
                        description: "crop to [x, y, w, h] in layout points",
                        items: "number"
                    )
                ),
                Opt(
                    name: "scale",
                    prop: Prop(
                        type: "number",
                        description: "pixel density 0.1–4 (default 1; 2 to read small text)"
                    )
                ),
                PortProp()
            ),
            Run: Screenshot
        )),

        ("find", new Tool(
            Description:
            "Find widgets without dumping a tree. label/role search the semantics tree (label " +
            "matches labels and values, case-insensitive substring); type searches the widget " +
            "tree by type name or description and returns ids for `widget_props`. One line per " +
            "match: what it is, its tappable center, its size.",
            Schema: Schema(
                Opt(
                    name: "label",
                    prop: Prop(type: "string", description: "substring of a label or value")
                ),
                Opt(
                    name: "role",
                    prop: Prop(type: "string", description: "e.g. Button, Text, Slider, TextField")
                ),
                Opt(
                    name: "type",
                    prop: Prop(
                        type: "string",
                        description: "widget type substring, e.g. 'TextField'"
                    )
                ),
                PortProp()
            ),
            Run: Find
        )),

        ("tap_widget", new Tool(
            Description:
            "Click a widget by its semantic label — no coordinate math. Scrolls it into view " +
            "first when it is offscreen. An ambiguous label is an error listing the matches; " +
            "narrow with role or index.",
            Schema: Schema(
                Req(
                    name: "label",
                    prop: Prop(type: "string", description: "substring of the label or value")
                ),
                Opt(name: "role", prop: Prop(type: "string", description: "e.g. 'Button'")),
                Opt(
                    name: "index",
                    prop: Prop(type: "integer", description: "which match, 0-based")
                ),
                ShotProp(),
                PortProp()
            ),
            Run: TapWidget
        )),

        ("wait_for", new Tool(
            Description:
            "Wait until a label appears (or, with gone=true, disappears) — async dialogs, " +
            "loading pages, toasts. With no label, waits until animations have finished.",
            Schema: Schema(
                Opt(
                    name: "label",
                    prop: Prop(type: "string", description: "substring of the label or value")
                ),
                Opt(name: "role", prop: Prop(type: "string", description: "narrow by role")),
                Opt(
                    name: "gone",
                    prop: Prop(type: "boolean", description: "wait for it to disappear")
                ),
                Opt(name: "timeout_seconds", prop: Prop(type: "number", description: "default 10")),
                ShotProp(),
                PortProp()
            ),
            Run: WaitFor
        )),

        ("semantics_tree", new Tool(
            Description:
            "What the UI means, as an indented outline: role, 'label', =value, [state] and the " +
            "tappable center of every meaningful node. The best overview of a screen; prefer " +
            "`find` when looking for one thing.",
            Schema: Schema(
                Opt(
                    name: "visible_only",
                    prop: Prop(type: "boolean", description: "skip nodes scrolled out of view")
                ),
                PortProp()
            ),
            Run: args => Text(
                SemanticsOutline(
                    tree: Tree(port: Port(args), command: "semantics"),
                    visibleOnly: Flag(args: args, name: "visible_only")
                )
            )
        )),

        ("widget_tree", new Tool(
            Description:
            "The live widget tree as an indented outline: Type description [x,y WxH]. " +
            "Single-child wrappers share a line (A > B > C) and bounds are printed only where " +
            "they change. For layout debugging; scope it with root and max_depth.",
            Schema: Schema(
                Opt(
                    name: "root",
                    prop: Prop(
                        type: "integer",
                        description: "only the subtree under this widget id"
                    )
                ),
                Opt(
                    name: "max_depth",
                    prop: Prop(type: "integer", description: "outline levels to show")
                ),
                Opt(
                    name: "ids",
                    prop: Prop(
                        type: "boolean",
                        description: "print #id per widget, for `widget_props`"
                    )
                ),
                PortProp()
            ),
            Run: WidgetTree
        )),

        ("widget_props", new Tool(
            Description: "Every property the inspector knows about one widget.",
            Schema: Schema(
                Req(
                    name: "id",
                    prop: Prop(
                        type: "integer",
                        description: "from `find type=…`, `widget_at` or `widget_tree ids=true`"
                    )
                ),
                PortProp()
            ),
            Run: WidgetProps
        )),

        ("widget_at", new Tool(
            Description:
            "What a click at a point would land on, and the widgets around it — the answer to " +
            "\"why did my tap do nothing\" (a scrim, a wrapper, or nothing at all).",
            Schema: Schema(
                Req(name: "x", prop: Prop(type: "number", description: "layout-point x")),
                Req(name: "y", prop: Prop(type: "number", description: "layout-point y")),
                PortProp()
            ),
            Run: args =>
            {
                string hit = HitText(
                    port: Port(args),
                    x: Num(args: args, name: "x"),
                    y: Num(args: args, name: "y"),
                    withPath: true
                );
                return Text(
                    hit.Length > 0 ? hit : "this app predates the `hit` command — rebuild it"
                );
            }
        )),

        ("measure", new Tool(
            Description:
            "Typographic and spacing measurements for design review, from the frame's own text " +
            "runs (exact baselines, not pixels). With `a` and `b`: both boxes, the gap between " +
            "them, every edge/center delta, and — when one sits inside the other — its insets " +
            "and how far off-center it is (text by cap height, icons by em box). Without them: " +
            "an audit of every visible text run (or those in `region`) — position, baseline, " +
            "size/weight, the box it sits in and its centering error — followed by near-misses: " +
            "neighbours whose baselines differ, left edges a pixel or three apart.",
            Schema: Schema(
                Opt(
                    name: "a",
                    prop: Prop(
                        type: "string",
                        description:
                        "semantic label, 'text:Run' for a text run, '#id' for a widget, or 'x,y,w,h'"
                    )
                ),
                Opt(name: "b", prop: Prop(type: "string", description: "same forms as a")),
                Opt(
                    name: "region",
                    prop: Prop(
                        type: "array",
                        description: "audit only runs whose baseline origin is in [x, y, w, h]",
                        items: "number"
                    )
                ),
                PortProp()
            ),
            Run: Measure
        )),

        ("audit", new Tool(
            Description:
            "Accessibility and polish lint of what is on screen, one line per finding: text and " +
            "icon contrast below WCAG AA (measured against the pixels actually behind each run), " +
            "controls smaller than min_target, interactive widgets with no label for a screen " +
            "reader, and text clipped, ellipsized or running off the window. Run per theme.",
            Schema: Schema(
                Opt(
                    name: "min_target",
                    prop: Prop(type: "number", description: "smallest hit size in points (default 24, WCAG AA; 44 for touch)")
                ),
                PortProp()
            ),
            Run: AuditUi
        )),

        ("tap", new Tool(
            Description:
            "Click at a point in layout points; the reply names the widget that took it. " +
            "Prefer `tap_widget` when the target has a label.",
            Schema: Schema(
                Req(name: "x", prop: Prop(type: "number", description: "layout-point x")),
                Req(name: "y", prop: Prop(type: "number", description: "layout-point y")),
                Opt(
                    name: "button",
                    prop: Prop(type: "string", description: "left (default), right, middle")
                ),
                ShotProp(),
                PortProp()
            ),
            Run: Tap
        )),

        ("drag", new Tool(
            Description:
            "Press, move over several frames, release — sliders, split dividers, " +
            "drag-and-drop. A mouse drag does not scroll lists; touch=true drags a finger " +
            "instead, which scrolls and flings them like on a phone.",
            Schema: Schema(
                Req(name: "from_x", prop: Prop(type: "number", description: "start x")),
                Req(name: "from_y", prop: Prop(type: "number", description: "start y")),
                Req(name: "to_x", prop: Prop(type: "number", description: "end x")),
                Req(name: "to_y", prop: Prop(type: "number", description: "end y")),
                Opt(
                    name: "touch",
                    prop: Prop(type: "boolean", description: "a finger instead of the mouse")
                ),
                Opt(
                    name: "steps",
                    prop: Prop(type: "integer", description: "move events (default 8)")
                ),
                ShotProp(),
                PortProp()
            ),
            Run: Drag
        )),

        ("scroll", new Tool(
            Description:
            "Wheel-scroll whatever is under a point. Positive dy scrolls DOWN (reveals content " +
            "below), negative up; positive dx scrolls right. One tick is about 40 points.",
            Schema: Schema(
                Req(
                    name: "x",
                    prop: Prop(type: "number", description: "a point over the scrollable")
                ),
                Req(
                    name: "y",
                    prop: Prop(type: "number", description: "a point over the scrollable")
                ),
                Opt(
                    name: "dy",
                    prop: Prop(type: "number", description: "vertical ticks, + is down")
                ),
                Opt(
                    name: "dx",
                    prop: Prop(type: "number", description: "horizontal ticks, + is right")
                ),
                ShotProp(),
                PortProp()
            ),
            Run: Scroll
        )),

        ("type_text", new Tool(
            Description: "Commit text to the focused widget, verbatim. Tap the field first.",
            Schema: Schema(
                Req(name: "text", prop: Prop(type: "string", description: "one line of text")),
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
            "Press and release one key with optional modifiers — shortcuts (cmd+S), navigation " +
            "(Tab, Down, Enter), dismissal (Escape).",
            Schema: Schema(
                Req(
                    name: "key",
                    prop: Prop(type: "string", description: "e.g. Enter, Escape, Tab, Down, A, F5")
                ),
                Opt(
                    name: "modifiers",
                    prop: Prop(type: "string", description: "'+'-joined: shift, ctrl, alt, cmd")
                ),
                ShotProp(),
                PortProp()
            ),
            Run: PressKey
        )),

        ("batch", new Tool(
            Description:
            "Run several tools in one call, in order, stopping at the first failure — e.g. " +
            "tap_widget, type_text, press_key, wait_for, screenshot. Each step's result comes " +
            "back numbered. Use it whenever the next steps do not depend on reading a result.",
            Schema: Schema(
                Req(
                    name: "steps",
                    prop: Prop(
                        type: "array",
                        description:
                        "[{\"tool\": \"tap_widget\", \"args\": {\"label\": \"Save\"}}, …]",
                        items: "object"
                    )
                )
            ),
            Run: Batch
        )),

        ("preview_targets", new Tool(
            Description:
            "The widget types this app can show on their own via `set_preview`, with the device " +
            "size their [Preview] attribute gave them and the properties each takes.",
            Schema: Schema(PortProp()),
            Run: args => Text(Send(port: Port(args), command: "previews"))
        )),

        ("set_preview", new Tool(
            Description:
            "Swap the previewed widget without restarting. Needs an app launched with `preview`.",
            Schema: Schema(
                Req(
                    name: "type",
                    prop: Prop(
                        type: "string",
                        description:
                        "a name from preview_targets, optionally with properties: " +
                        "'My.App.Card?title=Espresso&sale=true' (URL-encoded)"
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
            "Lay the app out at a device size in layout points — breakpoints adapt, so 390x844 " +
            "is a real phone layout. Omit both to return to the window's size. By default only " +
            "the layout changes; window=true resizes the OS window itself, framebuffer and all " +
            "— the way to reproduce bugs that only a real window resize triggers.",
            Schema: Schema(
                Opt(name: "width", prop: Prop(type: "number", description: "1–8192")),
                Opt(name: "height", prop: Prop(type: "number", description: "1–8192")),
                Opt(
                    name: "window",
                    prop: Prop(
                        type: "boolean",
                        description: "resize the OS window, not just the layout"
                    )
                ),
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
            Run: args => Text(Send(port: Port(args), command: "locales"))
        )),

        ("set_locale", new Tool(
            Description: "Switch the app's locale. Needs the app to use a LocalizationsScope.",
            Schema: Schema(
                Req(
                    name: "locale",
                    prop: Prop(type: "string", description: "a tag from `locales`, e.g. 'pt-BR'")
                ),
                ShotProp(),
                PortProp()
            ),
            Run: args => Done(
                args: args,
                reply: Send(port: Port(args), command: $"locale {Str(args: args, name: "locale")}")
            )
        )),

        ("stats", new Tool(
            Description:
            "Frame health in one line: fps, frame ms, CPU%, memory, GC counts, UI-thread alloc " +
            "KB/frame (near zero on a healthy steady-state app), jank frames and paint-command " +
            "counts. Frame times only mean render pace while the app animates or runs with env " +
            "ZIGOTE_CONTINUOUS=1.",
            Schema: Schema(PortProp()),
            Run: args => Text(Send(port: Port(args), command: "stats"))
        )),

        ("profile", new Tool(
            Description:
            "CPU-profile N frames and return the hottest engine scopes as per-frame average " +
            "self/total ms, plus the path of a Chrome-Trace JSON (Perfetto). `stats` says " +
            "whether frames are healthy; this says where the time goes. Blocks ~2 s by default.",
            Schema: Schema(
                Opt(
                    name: "frames",
                    prop: Prop(type: "integer", description: "1–600 (default 120)")
                ),
                PortProp()
            ),
            Run: Profile
        )),

        ("raw_command", new Tool(
            Description:
            "Escape hatch: one raw inspect-protocol line, raw JSON reply — e.g. 'window hide', " +
            "or 'semantics' / 'widgets' when a script needs the full tree as JSON.",
            Schema: Schema(
                Req(name: "command", prop: Prop(type: "string", description: "one protocol line")),
                PortProp()
            ),
            Run: args => Text(
                AppHost.Query(port: Port(args), command: OneLine(Str(args: args, name: "command")))
            )
        )),
    ];

    // The window each app's per-window commands go to, chosen with the `windows` tool. Absent = main.
    private static readonly Dictionary<int, uint> SelectedWindow = [];

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
            if (candidate != name) continue;
            try
            {
                return tool.Run(arguments);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
                // A string where a number belongs, and the like: the model can fix that, so it
                // reads as a tool error rather than an opaque protocol failure.
                throw new ToolError($"bad argument for {name}: {e.Message}");
            }
        }

        throw new ToolError(
            $"unknown tool '{name}' — there are: {string.Join(separator: ", ", values: All.Select(t => t.Name))}"
        );
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
        bool watch = Flag(args: args, name: "watch");
        var env = new Dictionary<string, string>();
        if (args["env"] is JsonObject extra)
        {
            foreach ((string key, var value) in extra)
                env[key] = value?.ToString() ?? "";
        }

        (int port, int pid) = AppHost.Launch(
            project: project,
            preview: StrOpt(args: args, name: "preview"),
            watch: watch,
            waitSeconds: Int(args: args, name: "wait_seconds") ?? 120,
            env: env
        );
        if (Flag(args: args, name: "hide_window")) Send(port: port, command: "window hide");
        string reload = watch ? "; hot reload on — edit, save, look again" : "";
        return Text($"launched pid {pid}, port {port} (now the default){reload}");
    }

    private static JsonObject Screenshot(JsonObject args)
    {
        int port = Port(args);
        (double X, double Y, double W, double H)? crop = null;
        if (args["region"] is JsonArray { Count: 4 } r)
        {
            crop = (r[0]!.GetValue<double>(), r[1]!.GetValue<double>(),
                r[2]!.GetValue<double>(), r[3]!.GetValue<double>());
        }
        else if (args["region"] is not null)
            throw new ToolError("region wants four numbers: [x, y, w, h]");
        else if (StrOpt(args: args, name: "label") is { } label)
        {
            (var hits, double w, double h) = SemanticsMatches(port: port, label: label, role: null);
            var target = hits.FirstOrDefault(n => !Offscreen(node: n, w: w, h: h))
                         ?? throw new ToolError(
                             hits.Count == 0
                                 ? $"nothing matches '{label}' to crop to"
                                 : $"'{label}' is scrolled out of view — nothing to crop to"
                         );
            // A little air around the widget: a focus ring or shadow is part of how it looks.
            crop = (N(node: target, field: "x") - 8, N(node: target, field: "y") - 8,
                N(node: target, field: "w") + 16, N(node: target, field: "h") + 16);
        }

        return new JsonObject {
            ["content"] = new JsonArray(
                ShotContent(
                    port: port,
                    scale: Num(args: args, name: "scale", fallback: 1),
                    crop: crop
                )
            ),
        };
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

        var lines = new StringBuilder();
        int total = 0;
        if (type is not null)
        {
            foreach (var node in Nodes(Tree(port: port, command: "widgets")))
            {
                if (!Has(node: node, field: "type", needle: type) &&
                    !Has(node: node, field: "desc", needle: type)) continue;
                if (total++ >= MaxMatches) continue;
                (double cx, double cy) = Center(node);
                lines.Append('\n').Append(S(node: node, field: "type")).Append('#')
                    .Append(node["id"]);
                if (S(node: node, field: "desc") is { } desc) lines.Append(' ').Append(desc);
                lines.Append($" @{F(cx)},{F(cy)} {Dim(node)}");
            }
        }
        else
        {
            (var hits, double w, double h) = SemanticsMatches(port: port, label: label, role: role);
            total = hits.Count;
            for (int i = 0; i < hits.Count && i < MaxMatches; i++)
            {
                lines.Append($"\n[{i}] {Describe(hits[i])} {Dim(hits[i])}");
                if (Offscreen(node: hits[i], w: w, h: h)) lines.Append(" offscreen");
            }
        }

        string shown = total > MaxMatches ? $", first {MaxMatches} shown" : "";
        return Text($"{total} match(es){shown}{lines}");
    }

    private static JsonObject TapWidget(JsonObject args)
    {
        int port = Port(args);
        string label = Str(args: args, name: "label");
        string? role = StrOpt(args: args, name: "role");
        (var hits, double w, double h) = Matches();

        int index = (hits.Count, Int(args: args, name: "index")) switch {
            (0, _) => throw new ToolError(
                $"nothing in the semantics tree matches '{label}' — `find` with a shorter " +
                "substring, or `wait_for` if it appears asynchronously (rows of a virtualized " +
                "list only exist once scrolled to)"
            ),
            (_, { } i) when i >= 0 && i < hits.Count => i,
            (_, { } i) => throw new ToolError(
                $"index {i} is out of range — {hits.Count} match(es)"
            ),
            (1, null) => 0,
            (_, null) => throw new ToolError(
                $"'{label}' is ambiguous — {hits.Count} matches: " +
                string.Join(
                    separator: "; ",
                    values: hits.Take(6).Select((h, i) => $"[{i}] {Describe(h)}")
                ) +
                ". Narrow with role, or pick one with index."
            ),
        };

        var chosen = hits[index];
        // ponytail: wheels the scroller under the target's own column/row and trusts match
        // order to survive the scroll — true for plain scroll views; a target inside nested
        // scrollers on both axes needs a real scroll-into-view command on the app side.
        for (int attempt = 0; attempt < 12 && Offscreen(node: chosen, w: w, h: h); attempt++)
        {
            (double cx, double cy) = Center(chosen);
            double px = cx >= 0 && cx <= w
                ? cx
                : Math.Clamp(value: cx, min: w * 0.25, max: w * 0.75);
            double py = cy >= 0 && cy <= h
                ? cy
                : Math.Clamp(value: cy, min: h * 0.25, max: h * 0.75);
            WheelAt(
                port: port,
                x: px,
                y: py,
                right: Ticks(cx - px),
                down: Ticks(cy - py)
            );
            Settle(port);

            (hits, w, h) = Matches();
            if (index >= hits.Count) break;
            var moved = hits[index];
            bool stuck = Center(moved) == (cx, cy);
            chosen = moved;
            if (stuck) break;
        }

        if (Offscreen(node: chosen, w: w, h: h))
        {
            throw new ToolError(
                $"{Describe(chosen)} is outside the {F(w)}x{F(h)} layout and scrolling did not " +
                "bring it in — `scroll` the right container yourself, then tap again."
            );
        }

        (double x, double y) = Center(chosen);
        Send(port: port, command: $"input down {F(x)} {F(y)} left");
        Send(port: port, command: $"input up {F(x)} {F(y)} left");
        return Done(args: args, reply: $"tapped {Describe(chosen)}");

        // "Menu" should not be ambiguous with "Menus & Popovers": when exactly one match is the
        // whole label, that is the one meant. An explicit index always counts substring matches.
        (List<JsonObject> Hits, double W, double H) Matches()
        {
            var all = SemanticsMatches(port: port, label: label, role: role);
            if (args["index"] is not null || all.Hits.Count < 2) return all;
            var exact = all.Hits.Where(n => string.Equals(
                    a: S(node: n, field: "label"),
                    b: label,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
            ).ToList();
            return exact.Count == 1 ? (exact, all.W, all.H) : all;
        }

        static double Ticks(double points) =>
            points == 0 ? 0 : Math.Clamp(value: points / 40, min: -30, max: 30);
    }

    private static JsonObject WaitFor(JsonObject args)
    {
        int port = Port(args);
        string? label = StrOpt(args: args, name: "label");
        double timeout = Num(args: args, name: "timeout_seconds", fallback: 10);
        if (label is null)
        {
            return Done(
                args: args,
                reply: Settle(port: port, maxSeconds: timeout)
                    ? "idle"
                    : $"still animating after {F(timeout)}s"
            );
        }

        string? role = StrOpt(args: args, name: "role");
        bool gone = Flag(args: args, name: "gone");
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (true)
        {
            var (hits, _, _) = SemanticsMatches(port: port, label: label, role: role);
            if (gone ? hits.Count == 0 : hits.Count > 0)
            {
                return Done(
                    args: args,
                    reply: gone
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

            Thread.Sleep(100);
        }
    }

    private static JsonObject WidgetTree(JsonObject args)
    {
        var tree = Tree(port: Port(args), command: "widgets");
        if (Int(args: args, name: "root") is { } rootId)
        {
            tree = Nodes(tree).FirstOrDefault(n => n["id"]?.GetValue<long>() == rootId)
                   ?? throw new ToolError(
                       $"no widget with id {rootId} — the tree may have been rebuilt"
                   );
        }

        if (tree is not JsonObject root) return Text("(empty tree)");

        var outline = new StringBuilder();
        int lines = 0;
        Write(
            node: root,
            depth: 0,
            lastBounds: ""
        );
        return Text(outline.ToString().TrimEnd());

        void Write(JsonObject node, int depth, string lastBounds)
        {
            if (lines++ >= MaxOutlineLines)
            {
                if (lines == MaxOutlineLines + 1)
                {
                    outline.Append(
                        $"… cut at {MaxOutlineLines} lines — narrow with root or max_depth\n"
                    );
                }

                return;
            }

            outline.Append(value: ' ', repeatCount: depth);
            // A wrapper with one child says nothing a new line would add: the chain reads as
            // one row, and bounds appear only where a wrapper actually changed them.
            while (true)
            {
                outline.Append(S(node: node, field: "type"));
                if (Flag(args: args, name: "ids")) outline.Append('#').Append(node["id"]);
                if (S(node: node, field: "desc") is { } desc) outline.Append(' ').Append(desc);
                string bounds =
                    $"[{F(N(node: node, field: "x"))},{F(N(node: node, field: "y"))} {Dim(node)}]";
                if (bounds != lastBounds) outline.Append(' ').Append(bounds);
                lastBounds = bounds;

                var kids = Children(node);
                if (kids.Count != 1) break;
                outline.Append(" > ");
                node = kids[0];
            }

            var children = Children(node);
            if (children.Count > 0 && Int(args: args, name: "max_depth") is { } max &&
                depth + 1 >= max)
            {
                outline.Append($" …+{Nodes(node).Count() - 1} below\n");
                return;
            }

            outline.Append('\n');
            foreach (var child in children)
                Write(node: child, depth: depth + 1, lastBounds: lastBounds);
        }
    }

    private static JsonObject WidgetProps(JsonObject args)
    {
        var reply = Parse(
            Send(port: Port(args), command: $"props {(long)Num(args: args, name: "id")}")
        ) as JsonObject ?? throw new ToolError("unexpected props reply");
        var text = new StringBuilder(
            $"{S(node: reply, field: "type")} [{F(N(node: reply, field: "x"))},{F(N(node: reply, field: "y"))} {Dim(reply)}]"
        );
        if (reply["props"] is JsonObject props)
        {
            foreach ((string name, var value) in props)
                text.Append('\n').Append(name).Append(": ").Append(value);
        }

        return Text(text.ToString());
    }

    private static JsonObject Tap(JsonObject args)
    {
        int port = Port(args);
        double x = Num(args: args, name: "x"), y = Num(args: args, name: "y");
        string at = $"{F(x)} {F(y)} {StrOpt(args: args, name: "button") ?? "left"}";
        // Asked before the press, so the answer is what the press is about to land on — the
        // click itself may well remove it.
        string hit = HitText(
            port: port,
            x: x,
            y: y,
            withPath: false
        );
        // The hit is often an anonymous Pressable; the smallest labelled semantics node around
        // the point is what a person would call it.
        var named = Nodes(Tree(port: port, command: "semantics"))
            .Where(n => S(node: n, field: "label") is not null &&
                        x >= N(node: n, field: "x") && y >= N(node: n, field: "y") &&
                        x <= N(node: n, field: "x") + N(node: n, field: "w") &&
                        y <= N(node: n, field: "y") + N(node: n, field: "h")
            )
            .MinBy(n => N(node: n, field: "w") * N(node: n, field: "h"));
        if (named is not null && hit.Length > 0)
            hit += $" ({S(node: named, field: "role")} '{S(node: named, field: "label")}')";
        Send(port: port, command: $"input down {at}");
        Send(port: port, command: $"input up {at}");
        return Done(args: args, reply: hit.Length > 0 ? $"tapped {hit}" : "tapped");
    }

    private static JsonObject Scroll(JsonObject args)
    {
        int port = Port(args);
        double x = Num(args: args, name: "x"), y = Num(args: args, name: "y");
        // A wheel at a scroller's edge is silently a no-op, and the caller cannot see that
        // without a frame — so compare where things are before and after.
        Settle(port); // or a transition still running reads as the scroll having moved things
        string before = Send(port: port, command: "semantics");
        WheelAt(
            port: port,
            x: x,
            y: y,
            right: Num(args: args, name: "dx", fallback: 0),
            down: Num(args: args, name: "dy", fallback: 0)
        );
        Settle(port);
        return Done(
            args: args,
            reply: Send(port: port, command: "semantics") == before
                ? "nothing moved — already at the edge, or no scrollable under that point (`widget_at` shows what is)"
                : "scrolled"
        );
    }

    /// <summary>
    ///     Wheel ticks at a point, in the tool's directions (down/right positive) rather than
    ///     the wire's, where a positive vertical tick scrolls <i>up</i>.
    /// </summary>
    private static void WheelAt(int port, double x, double y, double right, double down)
    {
        // Wheel dispatch targets the pointer's position, not the event's own coordinates — the
        // OS always moves the mouse somewhere before it scrolls there, so injected scrolls must
        // do the same or they land wherever the pointer last was (nowhere, in a hidden window).
        Send(port: port, command: $"input move {F(x)} {F(y)}");
        Send(port: port, command: $"input scroll {F(x)} {F(y)} {F(right)} {F(-down)}");
    }

    private static JsonObject Drag(JsonObject args)
    {
        int port = Port(args);
        double fromX = Num(args: args, name: "from_x"), fromY = Num(args: args, name: "from_y");
        double toX = Num(args: args, name: "to_x"), toY = Num(args: args, name: "to_y");
        int steps = Math.Clamp(value: Int(args: args, name: "steps") ?? 8, min: 1, max: 60);
        (string down, string move, string up) = Flag(args: args, name: "touch")
            ? ("touchdown", "touchmove", "touchup")
            : ("down", "move", "up");

        Send(port: port, command: $"input {down} {F(fromX)} {F(fromY)}");
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            Send(
                port: port,
                command:
                $"input {move} {F(fromX + ((toX - fromX) * t))} {F(fromY + ((toY - fromY) * t))}"
            );
            Thread.Sleep(16); // one frame apart, so the gesture arbitrates like a real one
        }

        Send(port: port, command: $"input {up} {F(toX)} {F(toY)}");
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

        if (!Flag(args: args, name: "window"))
        {
            return Done(
                args: args,
                reply: Send(
                    port: port,
                    command: width < 0 ? "size window" : $"size {F(width)}x{F(height)}"
                )
            );
        }

        if (width < 0) throw new ToolError("window=true needs a width and height");
        Send(port: port, command: $"window size {F(Math.Round(width))}x{F(Math.Round(height))}");
        // The resize arrives as an event over the next frames, and the window manager may clamp
        // it — so report the size the app actually ended up laying out at.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        JsonObject? info;
        do
        {
            Thread.Sleep(50);
            info = Parse(Send(port: port, command: "info")) as JsonObject;
        } while (info is not null && DateTime.UtcNow < deadline &&
                 (Math.Abs(N(node: info, field: "w") - Math.Round(width)) > 0.5 ||
                  Math.Abs(N(node: info, field: "h") - Math.Round(height)) > 0.5));

        string now = info is null ? "?" : Dim(info);
        return Done(
            args: args,
            reply: now == $"{F(Math.Round(width))}x{F(Math.Round(height))}"
                ? $"window is now {now}"
                : $"asked for {F(Math.Round(width))}x{F(Math.Round(height))}, window is {now} — the window manager clamped it, or the resize has not landed yet"
        );
    }

    private static JsonObject Profile(JsonObject args)
    {
        int frames = Math.Clamp(value: Int(args: args, name: "frames") ?? 120, min: 1, max: 600);
        var reply = Parse(
            Send(port: Port(args), command: $"profile {frames}", timeoutSeconds: 10 + (frames / 5))
        ) as JsonObject ?? throw new ToolError("unexpected profile reply");

        var table = new StringBuilder(
            $"{reply["frames"]} frames, trace: {S(node: reply, field: "path")}\nself_ms total_ms calls scope"
        );
        foreach (var scope in reply["scopes"] as JsonArray ?? [])
        {
            if (scope is not JsonObject s || N(node: s, field: "avg_total_ms") < 0.001) continue;
            table.Append(
                $"\n{F3(N(node: s, field: "avg_self_ms"))} {F3(N(node: s, field: "avg_total_ms"))} {s["calls"]} {S(node: s, field: "name")}"
            );
        }

        return Text(table.ToString());
    }

    private static JsonObject Batch(JsonObject args)
    {
        if (args["steps"] is not JsonArray { Count: > 0 } steps)
            throw new ToolError("batch wants a non-empty steps array of {tool, args}");

        var content = new JsonArray();
        for (int i = 0; i < steps.Count; i++)
        {
            string name = (steps[i] as JsonObject)?["tool"]?.GetValue<string>() ?? "?";
            JsonObject result;
            try
            {
                if (name == "batch") throw new ToolError("batch does not nest");
                result = Call(
                    new JsonObject {
                        ["name"] = name,
                        ["arguments"] =
                            ((steps[i] as JsonObject)?["args"] as JsonObject)?.DeepClone(),
                    }
                );
            }
            catch (ToolError e)
            {
                result = ErrorResult(e.Message);
            }

            bool failed = result["isError"] is not null;
            bool labelled = false;
            foreach (var item in result["content"]!.AsArray())
            {
                var copy = item!.DeepClone().AsObject();
                if (!labelled && copy["type"]?.GetValue<string>() == "text")
                {
                    copy["text"] = $"{i + 1} {name}: {(failed ? "FAILED — " : "")}{copy["text"]}";
                    labelled = true;
                }

                content.Add(copy);
            }

            if (!failed) continue;
            int skipped = steps.Count - i - 1;
            if (skipped > 0) content.Add(TextItem($"{skipped} later step(s) not run"));
            return new JsonObject {
                ["content"] = content,
                ["isError"] = true,
            };
        }

        return new JsonObject { ["content"] = content };
    }

    // ── outlines ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The semantics tree as indented lines. Unlabelled groups carry no meaning of their
    ///     own, so they are dropped and their children promoted; capability flags (Focusable,
    ///     Checkable…) follow from the role, so only states (Selected, Disabled, Checked…) show.
    /// </summary>
    private static string SemanticsOutline(JsonNode? tree, bool visibleOnly)
    {
        if (tree is not JsonObject root) return "(empty tree)";
        double w = N(node: root, field: "w"), h = N(node: root, field: "h");
        var outline = new StringBuilder($"{F(w)}x{F(h)} layout\n");
        int lines = 0;
        foreach (var child in Children(root)) Write(node: child, depth: 0);
        return outline.ToString().TrimEnd();

        void Write(JsonObject node, int depth)
        {
            bool offscreen = Offscreen(node: node, w: w, h: h);
            string? role = S(node: node, field: "role");
            bool silent = role is "Group" or "None" && S(node: node, field: "label") is null &&
                          S(node: node, field: "value") is null;
            if (!silent && !(visibleOnly && offscreen))
            {
                if (lines++ >= MaxOutlineLines)
                {
                    if (lines == MaxOutlineLines + 1)
                    {
                        outline.Append(
                            $"… cut at {MaxOutlineLines} lines — use visible_only or `find`\n"
                        );
                    }

                    return;
                }

                outline.Append(value: ' ', repeatCount: depth).Append(Describe(node));
                if (S(node: node, field: "hint") is { } hint)
                    outline.Append(" (").Append(hint).Append(')');
                string[] states = [
                    .. (S(node: node, field: "flags") ?? "")
                    .Split(separator: ", ", options: StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f != "None" && !f.EndsWith("able")),
                ];
                if (states.Length > 0)
                    outline.Append(" [").AppendJoin(separator: ',', values: states).Append(']');
                if (offscreen) outline.Append(" offscreen");
                outline.Append('\n');
                depth++;
            }

            foreach (var child in Children(node)) Write(node: child, depth: depth);
        }
    }

    /// <summary>
    ///     What a click at a point would hit, as "Type desc [bounds]" — plus the enclosing
    ///     widget types when <paramref name="withPath" />. Empty when the app is too old to
    ///     know the <c>hit</c> command: a tap must still work against it.
    /// </summary>
    private static string HitText(int port, double x, double y, bool withPath)
    {
        JsonObject? reply;
        try
        {
            reply = Parse(Send(port: port, command: $"hit {F(x)} {F(y)}")) as JsonObject;
        }
        catch (ToolError e) when (e.Message.Contains("unknown command"))
        {
            return "";
        }

        if (reply?["hit"] is not JsonObject hit)
            return $"nothing — no widget takes input at ({F(x)},{F(y)})";
        var text = new StringBuilder($"{S(node: hit, field: "type")}#{hit["id"]}");
        if (S(node: hit, field: "desc") is { } desc) text.Append(' ').Append(desc);
        if (!withPath) return text.ToString();

        text.Append($" [{F(N(node: hit, field: "x"))},{F(N(node: hit, field: "y"))} {Dim(hit)}]");
        if (reply["path"] is JsonArray { Count: > 0 } path)
        {
            text.Append(" in ").AppendJoin(
                separator: " < ",
                values: path.Select(p => p?.ToString())
            );
        }

        return text.ToString();
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
        double w = tree is JsonObject root ? N(node: root, field: "w") : 0;
        double h = tree is JsonObject r ? N(node: r, field: "h") : 0;

        var hits = new List<JsonObject>();
        foreach (var node in Nodes(tree))
        {
            if (label is not null && !Has(node: node, field: "label", needle: label) &&
                !Has(node: node, field: "value", needle: label)) continue;
            if (role is not null && !string.Equals(
                    a: S(node: node, field: "role"),
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

    private static JsonNode? Parse(string json) =>
        JsonNode.Parse(json: json, documentOptions: Deep);

    private static JsonNode? Tree(int port, string command) =>
        Parse(Send(port: port, command: command))?["tree"];

    private static List<JsonObject> Children(JsonObject node) =>
        node["children"] is JsonArray children ? [.. children.OfType<JsonObject>()] : [];

    private static IEnumerable<JsonObject> Nodes(JsonNode? node)
    {
        // An explicit stack: a 256-deep tree through nested iterators is quadratic.
        if (node is not JsonObject root) yield break;
        var pending = new Stack<JsonObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            yield return next;
            var children = Children(next);
            for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
        }
    }

    private static bool Has(JsonObject node, string field, string needle)
    {
        return S(node: node, field: field) is { } text &&
               text.Contains(value: needle, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    private static string? S(JsonObject node, string field) => node[field]?.GetValue<string>();

    private static double N(JsonObject node, string field) => node[field]?.GetValue<double>() ?? 0;

    private static string Dim(JsonObject node) =>
        $"{F(N(node: node, field: "w"))}x{F(N(node: node, field: "h"))}";

    private static (double X, double Y) Center(JsonObject node)
    {
        return (N(node: node, field: "x") + (N(node: node, field: "w") / 2),
            N(node: node, field: "y") + (N(node: node, field: "h") / 2));
    }

    /// <summary>A semantics node in a line: <c>Button 'Save' =value @x,y</c> — the point is its center.</summary>
    private static string Describe(JsonObject node)
    {
        (double cx, double cy) = Center(node);
        var text = new StringBuilder(S(node: node, field: "role"));
        if (S(node: node, field: "label") is { } label)
            text.Append(" '").Append(label).Append('\'');
        if (S(node: node, field: "value") is { } value) text.Append(" =").Append(value);
        return text.Append($" @{F(cx)},{F(cy)}").ToString();
    }

    // ── the inspect wire ──────────────────────────────────────────────────────

    private static int Port(JsonObject args) => AppHost.Resolve(Int(args: args, name: "port"));

    private static JsonObject Windows(JsonObject args)
    {
        int port = Port(args);
        if (Int(args: args, name: "select") is { } id)
        {
            if (id <= 0) SelectedWindow.Remove(port);
            else SelectedWindow[port] = (uint)id;
        }

        // Unprefixed, so a selection pointing at a window that has since closed can still be seen
        // and replaced.
        string reply = AppHost.Query(port: port, command: "windows", timeoutSeconds: null);
        uint selected = SelectedWindow.GetValueOrDefault(port);
        return Text(
            $"{reply}\naddressing: {(selected == 0 ? "main window" : $"window {selected}")}"
        );
    }

    /// <summary>One command, one reply — with the app's own error surfaced as a tool error.</summary>
    private static string Send(int port, string command, int? timeoutSeconds = null)
    {
        // The app applies the prefix to per-window verbs only and ignores it on app-wide ones.
        if (SelectedWindow.TryGetValue(key: port, value: out uint window))
            command = $"@{window} {command}";
        string reply = AppHost.Query(
            port: port,
            command: command,
            timeoutSeconds: timeoutSeconds
        );
        // Checked by prefix, not by parsing: a screenshot reply is megabytes of base64, and
        // every error the app writes starts exactly like this.
        if (reply.StartsWith("{\"error\":") &&
            Parse(reply)?["error"]?.GetValue<string>() is { } error)
            throw new ToolError($"the app said: {error}");
        return reply;
    }

    /// <summary>
    ///     Wait until no animation is running, so the frame about to be captured is the result
    ///     and not the transition into it. False when the wait ran out — a spinner or a blinking
    ///     caret animates forever, which is why the default cap is short.
    /// </summary>
    private static bool Settle(int port, double maxSeconds = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        try
        {
            while (Send(port: port, command: "info").Contains("\"animating\":true"))
            {
                if (DateTime.UtcNow > deadline) return false;
                Thread.Sleep(40);
            }
        }
        catch (ToolError e) when (e.Message.Contains("unknown command"))
        {
            // An app built before `info` existed: capture right away, as this always did.
        }

        return true;
    }

    // ── result and argument shapes ────────────────────────────────────────────

    private static JsonObject TextItem(string text) => new() {
        ["type"] = "text",
        ["text"] = text,
    };

    private static JsonObject Text(string text) =>
        new() { ["content"] = new JsonArray(TextItem(text)) };

    /// <summary>
    ///     The result of a mutating tool: the app's answer condensed to a word, and — when the
    ///     call asked with <c>screenshot: true</c> — the resulting frame, in the same round trip.
    /// </summary>
    private static JsonObject Done(JsonObject args, string reply)
    {
        var result = Text(reply.StartsWith("{\"ok\"") ? "ok" : reply);
        if (Flag(args: args, name: "screenshot"))
        {
            var content = result["content"]!.AsArray();
            foreach (var item in ShotContent(port: Port(args), scale: 1, crop: null))
                content.Add(item);
        }

        return result;
    }

    /// <summary>The settled frame as MCP content items: the PNG, then its size in layout points.</summary>
    private static JsonObject[] ShotContent(int port, double scale,
        (double X, double Y, double W, double H)? crop)
    {
        (byte[] bmp, double density, var parsed) = Capture(port: port, scale: scale);
        string size = $"{parsed["w"]}x{parsed["h"]} layout points";
        (int X, int Y, int W, int H)? pixels = null;
        if (crop is { } c)
        {
            pixels = ((int)(c.X * density), (int)(c.Y * density),
                (int)Math.Ceiling(c.W * density), (int)Math.Ceiling(c.H * density));
            size +=
                $", cropped to [{F(c.X)},{F(c.Y)} {F(c.W)}x{F(c.H)}] — image pixels are offset by that origin";
        }

        if (density != 1) size += $", {F(density)}x density";
        return [
            new JsonObject {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(Png.FromBmp(bmp: bmp, crop: pixels)),
                ["mimeType"] = "image/png",
            },
            TextItem(size),
        ];
    }

    /// <summary>The settled frame as the engine's BMP, its pixel density, and the reply's sizes.</summary>
    private static (byte[] Bmp, double Density, JsonObject Reply) Capture(int port, double scale)
    {
        Settle(port);
        string reply = Send(port: port, command: $"shot {F(scale)}");
        var parsed = Parse(reply) as JsonObject
                     ?? throw new ToolError(
                         $"unexpected shot reply: {reply[..Math.Min(val1: reply.Length, val2: 120)]}"
                     );
        byte[] bmp = Convert.FromBase64String(
            S(node: parsed, field: "data") ?? throw new ToolError("shot reply had no image data")
        );
        parsed.Remove("data"); // megabytes nobody reads again
        return (bmp, N(node: parsed, field: "scale"), parsed);
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

    private static string Str(JsonObject args, string name) =>
        StrOpt(args: args, name: name) ??
        throw new ToolError($"missing required argument '{name}'");

    private static string? StrOpt(JsonObject args, string name) => args[name]?.GetValue<string>();

    private static bool Flag(JsonObject args, string name) => args[name]?.GetValue<bool>() == true;

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

    private static string F3(double v) => v.ToString(
        format: "0.000",
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

    // Repeated on almost every tool, so these two are as short as they can be and still read.
    private static (string, JsonObject, bool) PortProp() => Opt(
        name: "port",
        prop: Prop(type: "integer", description: "inspect port (default: last launch)")
    );

    private static (string, JsonObject, bool) ShotProp() => Opt(
        name: "screenshot",
        prop: Prop(type: "boolean", description: "also return the resulting frame")
    );

    private static JsonObject Prop(string type, string description, string? items = null)
    {
        var prop = new JsonObject {
            ["type"] = type,
            ["description"] = description,
        };
        if (items is not null) prop["items"] = new JsonObject { ["type"] = items };
        return prop;
    }

    private sealed record Tool(
        string Description,
        JsonObject Schema,
        Func<JsonObject, JsonObject> Run);
}
