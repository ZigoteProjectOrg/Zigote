using System.Net;
using System.Text;
using Xunit;
using Zigote.Http;

namespace Zigote.Tests;

/// <summary>
///     <see cref="HttpFile" /> — random access over range requests. The stub origin below is the
///     point of the test: it serves ranges, and can be told to change its validator mid-read so the
///     "never splice two versions together" guarantee is provable without a server.
/// </summary>
public class HttpFileTests
{
    private static readonly byte[] Content =
        Enumerable.Range(start: 0, count: 1000).Select(i => (byte)i).ToArray();

    private sealed class RangeOrigin
    {
        public int Requests;
        public string ETag = "\"v1\"";
        public bool ServeRanges = true;

        public ValueTask<HttpResult<HttpResponse>> Send(HttpSpec spec, CancellationToken ct)
        {
            Requests++;
            string? range = spec.Headers.FirstOrDefault(h => h.Name == "Range").Value;
            string? ifRange = spec.Headers.FirstOrDefault(h => h.Name == "If-Range").Value;

            // A validator that no longer matches means the resource moved on: RFC 9110 says answer
            // with the whole thing, and HttpFile is required to notice rather than splice.
            bool whole = !ServeRanges || range is null || (ifRange is not null && ifRange != ETag);
            if (whole)
            {
                return ValueTask.FromResult(
                    HttpResult<HttpResponse>.Ok(
                        HttpResponse.FromBytes(
                            status: HttpStatusCode.OK,
                            headers: [new HeaderPair(Name: "ETag", Value: ETag)],
                            body: Content
                        )
                    )
                );
            }

            string[] span = range!["bytes=".Length..].Split('-');
            int start = int.Parse(span[0]);
            int end = span[1].Length == 0 ? Content.Length - 1 : int.Parse(span[1]);
            end = Math.Min(val1: end, val2: Content.Length - 1);

            return ValueTask.FromResult(
                HttpResult<HttpResponse>.Ok(
                    HttpResponse.FromBytes(
                        status: HttpStatusCode.PartialContent,
                        headers: [
                            new HeaderPair(Name: "ETag", Value: ETag),
                            new HeaderPair(
                                Name: "Content-Range",
                                Value: $"bytes {start}-{end}/{Content.Length}"
                            ),
                            new HeaderPair(Name: "Accept-Ranges", Value: "bytes"),
                        ],
                        body: Content[start..(end + 1)]
                    )
                )
            );
        }
    }

    private static HttpRunner Runner(RangeOrigin origin) => new(
        new HttpRunnerOptions {
            BaseAddress = new Uri("https://assets.test/"),
            Transport = origin.Send,
        }
    );

    [Fact]
    public async Task Reads_the_footer_without_downloading_the_file()
    {
        var origin = new RangeOrigin();
        using var runner = Runner(origin);

        var opened = await HttpFile.OpenAsync(
            runner: runner,
            spec: HttpRequest.Get("big.bin"),
            blockSize: 100,
            ct: TestContext.Current.CancellationToken
        );
        await using var file = opened.Unwrap();

        Assert.Equal(expected: 1000, actual: file.Length);
        Assert.Equal(expected: "\"v1\"", actual: file.Validator);

        file.Seek(offset: -4, origin: SeekOrigin.End);
        byte[] footer = new byte[4];
        int read = await file.ReadAsync(buffer: footer, ct: TestContext.Current.CancellationToken);

        Assert.Equal(expected: 4, actual: read);
        Assert.Equal(expected: Content[996..], actual: footer);
        Assert.Equal(expected: 2, actual: origin.Requests); // the opening probe, and one block
    }

    [Fact]
    public async Task Serves_a_second_read_of_one_block_from_its_cache()
    {
        var origin = new RangeOrigin();
        using var runner = Runner(origin);

        await using var file = (await HttpFile.OpenAsync(
            runner: runner,
            spec: HttpRequest.Get("big.bin"),
            blockSize: 100,
            ct: TestContext.Current.CancellationToken
        )).Unwrap();

        byte[] buffer = new byte[10];
        file.Seek(offset: 0, origin: SeekOrigin.Begin);
        await file.ReadExactlyAsync(
            buffer: buffer,
            cancellationToken: TestContext.Current.CancellationToken
        );
        file.Seek(offset: 50, origin: SeekOrigin.Begin);
        await file.ReadExactlyAsync(
            buffer: buffer,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: Content[50..60], actual: buffer);
        Assert.Equal(expected: 2, actual: origin.Requests); // probe + one block covering both reads
    }

    [Fact]
    public async Task A_resource_that_changes_mid_read_fails_loudly()
    {
        var origin = new RangeOrigin();
        using var runner = Runner(origin);

        await using var file = (await HttpFile.OpenAsync(
            runner: runner,
            spec: HttpRequest.Get("big.bin"),
            blockSize: 100,
            ct: TestContext.Current.CancellationToken
        )).Unwrap();

        origin.ETag = "\"v2\"";
        file.Seek(offset: 500, origin: SeekOrigin.Begin);

        await Assert.ThrowsAsync<IOException>(async () =>
            await file.ReadExactlyAsync(
                buffer: new byte[10],
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task An_origin_without_ranges_fails_at_open_rather_than_downloading_everything()
    {
        var origin = new RangeOrigin { ServeRanges = false };
        using var runner = Runner(origin);

        var result = await HttpFile.OpenAsync(
            runner: runner,
            spec: HttpRequest.Get("big.bin"),
            ct: TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: new HttpError.Policy(PolicyFault.Unsupported), actual: result.Error);
    }

    [Fact]
    public async Task Download_resumes_from_what_is_already_on_disk()
    {
        var origin = new RangeOrigin();
        using var runner = Runner(origin);
        string path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"zigote-http-{Guid.NewGuid():N}.bin"
        );

        try
        {
            // Pretend a previous attempt died after 400 bytes — with the validator it recorded,
            // which is what entitles the resume to append rather than start over.
            await File.WriteAllBytesAsync(
                path: path + ".part",
                bytes: Content[..400],
                cancellationToken: TestContext.Current.CancellationToken
            );
            await File.WriteAllTextAsync(
                path: path + ".part.validator",
                contents: "\"v1\"",
                cancellationToken: TestContext.Current.CancellationToken
            );

            var length = await HttpFile.DownloadAsync(
                runner: runner,
                spec: HttpRequest.Get("big.bin"),
                path: path,
                ct: TestContext.Current.CancellationToken
            );

            Assert.Equal(expected: 1000L, actual: length.Unwrap());
            Assert.Equal(
                expected: Content,
                actual: await File.ReadAllBytesAsync(
                    path: path,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );
            Assert.Equal(
                expected: 1,
                actual: origin.Requests
            ); // one ranged request for the remainder
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".part");
            File.Delete(path + ".part.validator");
        }
    }
}
