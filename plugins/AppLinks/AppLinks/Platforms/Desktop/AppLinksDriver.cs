using System.IO.Pipes;
using System.Text;

namespace AppLinks;

/// <summary>
///     Desktop implementation — one named pipe per app id is both the lock and the letterbox.
///     The first instance to start listening owns the app; every later launch connects, posts
///     the links from its command line and exits. .NET named pipes are Windows named pipes and
///     Unix domain sockets, so the same twenty lines cover all three desktops with no
///     single-instance library and no lock-file races.
///     <para>
///         Registering the URL scheme itself is packaging, not runtime: a <c>.desktop</c> file
///         with <c>MimeType=x-scheme-handler/…</c>, a registry key, or a <c>CFBundleURLTypes</c>
///         entry. The shell then launches the app with the URL in argv, which is what this
///         reads.
///     </para>
/// </summary>
internal static class AppLinksDriver
{
    private static NamedPipeServerStream? _listener;

    /// <summary>Desktop launch links come from argv, which the shared layer already read.</summary>
    public static Uri? LaunchLink() => null;

    public static async Task<bool> StartAsync(string appId, string[] links, Action<string> deliver)
    {
        string pipe = "zigote-applinks-" + appId;

        // Someone already listening? Then they own the app: hand over and report back.
        if (await TryHandOff(pipe, links)) return false;

        try
        {
            Listen(pipe, deliver);
            return true;
        }
        catch (IOException)
        {
            // Lost the race to another instance that started listening a moment ago — hand the
            // links over to it instead of racing again.
            await TryHandOff(pipe, links);
            return false;
        }
    }

    private static async Task<bool> TryHandOff(string pipe, string[] links)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".", pipe, PipeDirection.Out, PipeOptions.Asynchronous);
            // Short: a live owner accepts immediately, and nothing else should be answering.
            await client.ConnectAsync(300);
            await client.WriteAsync(Encoding.UTF8.GetBytes(string.Join('\n', links)));
            await client.FlushAsync();
            return true;
        }
        catch (Exception)
        {
            // Nobody home (TimeoutException), or a stale socket file: this instance is the owner.
            return false;
        }
    }

    /// <summary>Accept one client at a time, forever, on a background task.</summary>
    private static void Listen(string pipe, Action<string> deliver)
    {
        _listener = new NamedPipeServerStream(
            pipe, PipeDirection.In, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        _ = Task.Run(async () =>
        {
            var server = _listener;
            while (server is not null)
            {
                try
                {
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    foreach (string line in (await reader.ReadToEndAsync()).Split('\n'))
                        if (line.Length > 0)
                            deliver(line);
                    server.Disconnect();
                }
                catch (Exception)
                {
                    // A client that died mid-write costs one link, not the listener.
                    try
                    {
                        if (server.IsConnected) server.Disconnect();
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }
        });
    }
}
