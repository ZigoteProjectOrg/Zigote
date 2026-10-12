using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zigote.Mcp;

/// <summary>
///     The MCP side of the bridge: JSON-RPC 2.0, one message per line, requests on stdin and
///     answers on stdout. Anything human-readable goes to stderr — a stray line on stdout is a
///     protocol error to the client, which is why the launch commands in the README all pass
///     <c>-v q --nologo</c> to keep the build out of the stream.
///     <para>
///         Hand-rolled rather than an SDK dependency for the same reason the Rider plugin
///         hand-rolls its JSON reader: the surface actually used is five methods
///         (<c>initialize</c>, <c>ping</c>, <c>tools/list</c>, <c>tools/call</c> and the
///         <c>initialized</c> notification), which is smaller than any package that covers it.
///     </para>
/// </summary>
public static class Program
{
    private static readonly string[] KnownProtocolVersions =
        ["2025-06-18", "2025-03-26", "2024-11-05"];

    public static int Main()
    {
        // UTF-8 without a BOM on both ends: a BOM ahead of the first '{' is a parse error to the
        // client, and Windows consoles default to neither.
        Console.InputEncoding = Encoding.UTF8;
        var stdout = new StreamWriter(
            stream: Console.OpenStandardOutput(),
            encoding: new UTF8Encoding(false)
        ) { AutoFlush = true };

        while (Console.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                Write(
                    stdout: stdout,
                    message: RpcError(id: null, code: -32700, message: "parse error")
                );
                continue;
            }

            if (message is not JsonObject request) continue;

            string? method = request["method"]?.GetValue<string>();
            var id = request["id"];

            // A response to a server-initiated request — this server never sends any — or garbage.
            if (method is null) continue;

            // Notifications get no reply, whatever they are; `initialized` and `cancelled` are the
            // ones that actually arrive.
            if (id is null) continue;

            var reply = Dispatch(method: method, @params: request["params"] as JsonObject, id: id);
            Write(stdout: stdout, message: reply);
        }

        AppHost.StopAll();
        return 0;
    }

    private static JsonObject Dispatch(string method, JsonObject? @params, JsonNode id)
    {
        try
        {
            return method switch {
                "initialize" => Result(id: id, result: Initialize(@params)),
                "ping" => Result(id: id, result: new JsonObject()),
                "tools/list" => Result(id: id, result: new JsonObject { ["tools"] = Tools.List() }),
                "tools/call" => Result(id: id, result: Tools.Call(@params)),
                _ => RpcError(id: id, code: -32601, message: $"method '{method}' not found"),
            };
        }
        catch (ToolError e)
        {
            // A tool that fails is a *successful* tools/call whose result says isError — the model
            // reads the message and adjusts. Only malformed requests become JSON-RPC errors.
            return Result(id: id, result: Tools.ErrorResult(e.Message));
        }
        catch (Exception e)
        {
            return RpcError(id: id, code: -32603, message: e.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? @params)
    {
        // Echo the client's version when it is one we know; otherwise answer with our newest and
        // let the client decide whether to proceed (the spec's negotiation rule).
        string? asked = @params?["protocolVersion"]?.GetValue<string>();
        string version = asked is not null && KnownProtocolVersions.Contains(asked)
            ? asked
            : KnownProtocolVersions[0];

        return new JsonObject {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject {
                ["name"] = "zigote",
                ["version"] = "0.2.0",
            },
            ["instructions"] =
                "Drives a running Zigote app. Start one with `launch` (watch=true adds hot reload " +
                "on save), or start it yourself with ZIGOTE_INSPECT=0 and pass the port it prints " +
                "(`zigote inspect: 127.0.0.1:PORT`) to any tool. Cheapest first: `info` for " +
                "state, `find`/`semantics_tree` for what is on screen, a cropped `screenshot` " +
                "only when pixels matter. Prefer labels over coordinates (`tap_widget`, " +
                "`wait_for`). Save round trips: mutating tools take screenshot:true, and `batch` " +
                "runs a whole sequence in one call. Coordinates are layout points. When a tap " +
                "does nothing, `widget_at` says what took it; after anything unexpected, `logs` " +
                "(filter: 'error|exception') has the app's own output.",
        };
    }

    // ── framing ───────────────────────────────────────────────────────────────

    private static void Write(TextWriter stdout, JsonObject message)
    {
        // One line per message; the serializer never emits raw newlines inside strings.
        stdout.Write(message.ToJsonString());
        stdout.Write('\n');
        stdout.Flush();
    }

    private static JsonObject Result(JsonNode id, JsonNode result) => new() {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject RpcError(JsonNode? id, int code, string message)
    {
        return new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject {
                ["code"] = code,
                ["message"] = message,
            },
        };
    }
}

/// <summary>A message meant for the model to read and act on, not a protocol failure.</summary>
public sealed class ToolError(string message) : Exception(message);
