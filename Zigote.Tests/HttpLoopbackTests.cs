using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using Zigote.Http;

namespace Zigote.Tests;

/// <summary>
///     The transport features a stub cannot prove — cookies, the multipart wire format, upload
///     progress, redirect policy — against a real socket: an <see cref="HttpListener" /> on
///     loopback. Everything else in the HTTP suite stays socket-free on purpose; this file is the
///     one place the real <c>SocketsHttpHandler</c> path is exercised.
/// </summary>
public class HttpLoopbackTests
{
    /// <summary>A one-handler HTTP server on a free loopback port, torn down with the test.</summary>
    private sealed class Loopback : IDisposable
    {
        private readonly HttpListener _listener;

        public Loopback(Func<HttpListenerContext, Task> handler)
        {
            // Ask the OS for a free port, then listen on it. The gap between release and reuse is
            // a benign race on a loopback test box.
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}/");
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add(Uri.ToString());
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                    {
                        return; // shutdown
                    }

                    try
                    {
                        await handler(context);
                    }
                    finally
                    {
                        context.Response.Close();
                    }
                }
            });
        }

        public Uri Uri { get; }

        public void Dispose() => _listener.Stop();
    }

    private static HttpRunner Runner(Uri baseAddress, CookieContainer? cookies = null, int maxRedirects = 10) =>
        new(new HttpRunnerOptions
        {
            BaseAddress = baseAddress,
            Cookies = cookies,
            MaxRedirects = maxRedirects
        });

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        using var buffer = new MemoryStream();
        await request.InputStream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        return buffer.ToArray();
    }

    private static Task WriteAsync(HttpListenerContext context, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        return context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    [Fact]
    public async Task Multipart_encodes_fields_and_files_on_the_wire()
    {
        string? contentType = null;
        byte[] received = [];
        using var server = new Loopback(async context =>
        {
            contentType = context.Request.ContentType;
            received = await ReadBodyAsync(context.Request);
            await WriteAsync(context, "ok");
        });

        byte[] blob = [1, 2, 3, 4, 0xFF];
        using var runner = Runner(server.Uri);
        var result = await runner.TextAsync(
            HttpRequest.Post("upload").WithMultipart(
                MultipartPart.Field("meta", "hello multipart"),
                MultipartPart.File("blob", "a.bin", blob)),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected: "ok", actual: result.Value);
        Assert.StartsWith("multipart/form-data", contentType);

        string wire = Encoding.Latin1.GetString(received);
        Assert.Contains("name=meta", wire.Replace("\"", ""));
        Assert.Contains("hello multipart", wire);
        Assert.Contains("filename=a.bin", wire.Replace("\"", ""));
        Assert.Contains("Content-Type: application/octet-stream", wire);
        Assert.Contains(Encoding.Latin1.GetString(blob), wire);
    }

    [Fact]
    public void A_multipart_with_only_value_parts_is_replayable_and_one_stream_part_is_not()
    {
        var replayable = HttpBody.Multipart(
            MultipartPart.Field("a", "1"),
            MultipartPart.File("f", "x.bin", new byte[3]));
        var oneShot = HttpBody.Multipart(
            MultipartPart.Field("a", "1"),
            MultipartPart.File("f", "x.bin", new MemoryStream([1])));

        Assert.True(replayable.IsReplayable);
        Assert.False(oneShot.IsReplayable);
    }

    [Fact]
    public async Task Cookies_flow_only_when_the_app_supplies_a_jar()
    {
        var seen = new List<string>();
        using var server = new Loopback(async context =>
        {
            seen.Add(context.Request.Headers["Cookie"] ?? "");
            context.Response.Headers.Add("Set-Cookie", "session=abc; Path=/");
            await WriteAsync(context, "ok");
        });

        using (var withJar = Runner(server.Uri, cookies: new CookieContainer()))
        {
            await withJar.TextAsync(HttpRequest.Get("a"), TestContext.Current.CancellationToken);
            await withJar.TextAsync(HttpRequest.Get("b"), TestContext.Current.CancellationToken);
        }

        using (var without = Runner(server.Uri))
        {
            await without.TextAsync(HttpRequest.Get("c"), TestContext.Current.CancellationToken);
            await without.TextAsync(HttpRequest.Get("d"), TestContext.Current.CancellationToken);
        }

        Assert.Equal(expected: "", actual: seen[0]); // nothing to send yet
        Assert.Contains("session=abc", seen[1]); // the jar carried it back
        Assert.Equal(expected: "", actual: seen[2]); // no jar: the Set-Cookie was ignored
        Assert.Equal(expected: "", actual: seen[3]);
    }

    [Fact]
    public async Task Upload_progress_reports_bytes_as_they_leave()
    {
        using var server = new Loopback(async context =>
        {
            await ReadBodyAsync(context.Request);
            await WriteAsync(context, "ok");
        });

        byte[] payload = new byte[256 * 1024];
        var uploads = new List<HttpProgress>();
        var sink = new InlineProgress(p =>
        {
            if (p.Uploading)
                lock (uploads) uploads.Add(p);
        });

        using var runner = Runner(server.Uri);
        var result = await runner.TextAsync(
            HttpRequest.Post("up")
                .WithBody(HttpBody.Bytes(payload))
                .Progress(sink),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected: "ok", actual: result.Value);
        Assert.NotEmpty(uploads);
        Assert.Equal(expected: payload.Length, actual: uploads[^1].Transferred);
        Assert.Equal(expected: payload.Length, actual: uploads[^1].Total);
        Assert.Equal(expected: 1.0, actual: uploads[^1].Fraction, precision: 5);
    }

    [Fact]
    public async Task Redirects_are_followed_by_default_and_a_3xx_is_the_answer_when_following_is_off()
    {
        using var server = new Loopback(async context =>
        {
            if (context.Request.Url!.AbsolutePath == "/start")
            {
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = "/end";
            }
            else
            {
                await WriteAsync(context, "arrived");
            }
        });

        using (var following = Runner(server.Uri))
        {
            var result = await following.TextAsync(HttpRequest.Get("start"), TestContext.Current.CancellationToken);
            Assert.Equal(expected: "arrived", actual: result.Value);
        }

        using (var manual = Runner(server.Uri, maxRedirects: 0))
        {
            var raw = await manual.SendAsync(HttpRequest.Get("start"), TestContext.Current.CancellationToken);
            using var response = raw.Value;
            Assert.Equal(expected: HttpStatusCode.Found, actual: response.Status);
            Assert.Equal(expected: "/end", actual: response.Header("Location"));
        }
    }

    [Fact]
    public async Task The_observability_span_travels_as_a_traceparent_header()
    {
        // A listener makes the Zigote.Http source actually create activities; without one, spans
        // are free — and so is this test's premise.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Zigote.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        string? traceparent = null;
        using var server = new Loopback(async context =>
        {
            traceparent = context.Request.Headers["traceparent"];
            await WriteAsync(context, "ok");
        });

        using var runner = Runner(server.Uri);
        await runner.TextAsync(HttpRequest.Get("traced"), TestContext.Current.CancellationToken);

        Assert.NotNull(traceparent);
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", traceparent);
    }

    [Fact]
    public async Task Download_resume_starts_over_when_the_resource_changed_instead_of_splicing()
    {
        byte[] v2 = Encoding.UTF8.GetBytes(new string('B', 1000));
        using var server = new Loopback(async context =>
        {
            // If-Range with a stale validator: RFC 9110 says answer with the whole current body.
            bool sameVersion = context.Request.Headers["If-Range"] == "\"v2\"";
            context.Response.Headers.Add("ETag", "\"v2\"");
            if (sameVersion && context.Request.Headers["Range"] is { } range)
            {
                int from = int.Parse(range["bytes=".Length..].TrimEnd('-'));
                context.Response.StatusCode = 206;
                byte[] tail = v2[from..];
                context.Response.ContentLength64 = tail.Length;
                await context.Response.OutputStream.WriteAsync(tail);
            }
            else
            {
                context.Response.ContentLength64 = v2.Length;
                await context.Response.OutputStream.WriteAsync(v2);
            }
        });

        string path = Path.Combine(Path.GetTempPath(), $"zigote-splice-{Guid.NewGuid():N}.bin");
        try
        {
            // A previous attempt left 400 bytes of v1 behind, validator recorded as v1.
            await File.WriteAllTextAsync(path + ".part", new string('A', 400), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(path + ".part.validator", "\"v1\"", TestContext.Current.CancellationToken);

            using var runner = Runner(server.Uri);
            var length = await HttpFile.DownloadAsync(runner, HttpRequest.Get("file"), path,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(expected: 1000L, actual: length.Unwrap());
            // The whole point: pure v2, not 400 bytes of v1 welded onto 600 of v2.
            Assert.Equal(expected: v2,
                actual: await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(path + ".part.validator")); // cleaned up on success
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".part");
            File.Delete(path + ".part.validator");
        }
    }

    [Fact]
    public void Recover_turns_exactly_one_status_into_a_value()
    {
        var notFound = HttpResult<int>.Fail(new HttpError.Status(HttpStatusCode.NotFound, []));
        var serverError = HttpResult<int>.Fail(new HttpError.Status(HttpStatusCode.InternalServerError, []));

        Assert.Equal(expected: 0, actual: notFound.Recover(HttpStatusCode.NotFound, 0).Value);
        Assert.False(serverError.Recover(HttpStatusCode.NotFound, 0).IsOk); // a 500 stays an error
        Assert.Equal(expected: 7, actual: HttpResult<int>.Ok(7).Recover(HttpStatusCode.NotFound, 0).Value);
    }

    /// <summary>Synchronous IProgress: the BCL's <see cref="Progress{T}" /> posts through a sync context a test doesn't have.</summary>
    private sealed class InlineProgress(Action<HttpProgress> report) : IProgress<HttpProgress>
    {
        public void Report(HttpProgress value) => report(value);
    }
}
