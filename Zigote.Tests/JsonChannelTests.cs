using System.Text.Json.Serialization;
using Xunit;
using Zigote.Core.Platform;

namespace Zigote.Tests;

internal sealed record ShareArgs(string Text, int Count);

internal sealed record ShareReply(string Id, bool Ok);

/// <summary>
///     The source-generated serialization the typed channels ride — the same shape a real plugin
///     ships: one context, declared next to the payload types, no reflection at runtime.
/// </summary>
[JsonSerializable(typeof(ShareArgs))]
[JsonSerializable(typeof(ShareReply))]
internal sealed partial class ChannelTestJson : JsonSerializerContext;

/// <summary>
///     The typed layer over the channel transport: JSON crosses as UTF-8 through the byte path,
///     both ends share one <see cref="JsonChannel{TArgs,TReply}" /> declaration, async request
///     handlers respond through the frame pump, and a faulted handler surfaces on the awaiting
///     side as <see cref="PlatformChannelException" /> rather than a sentinel value.
/// </summary>
public class JsonChannelTests
{
    [Fact]
    public void Invoke_RoundTripsTypedPayloads()
    {
        var channel = new JsonChannel<ShareArgs, ShareReply>(
            name: "test.json.share",
            argsInfo: ChannelTestJson.Default.ShareArgs,
            replyInfo: ChannelTestJson.Default.ShareReply
        );
        channel.Handle(args => new ShareReply(Id: args.Text + "#" + args.Count, Ok: true));
        try
        {
            Assert.True(channel.Supports);
            var reply = channel.Invoke(new ShareArgs(Text: "hello", Count: 3));
            Assert.Equal(expected: new ShareReply(Id: "hello#3", Ok: true), actual: reply);
        }
        finally
        {
            PlatformChannel.Unhandle(channel.Name);
        }
    }

    [Fact]
    public void Invoke_UnimplementedChannel_ReturnsDefault()
    {
        var channel = new JsonChannel<ShareArgs, ShareReply>(
            name: "test.json.nobody",
            argsInfo: ChannelTestJson.Default.ShareArgs,
            replyInfo: ChannelTestJson.Default.ShareReply
        );
        Assert.False(channel.Supports);
        Assert.Null(channel.Invoke(new ShareArgs(Text: "x", Count: 0)));
    }

    [Fact]
    public async Task Request_AwaitsAsyncHandler_ThroughTheFramePump()
    {
        var channel = new JsonChannel<ShareArgs, ShareReply>(
            name: "test.json.request",
            argsInfo: ChannelTestJson.Default.ShareArgs,
            replyInfo: ChannelTestJson.Default.ShareReply
        );
        channel.HandleRequests(args => Task.FromResult(new ShareReply(Id: args.Text, Ok: true)));
        try
        {
            var request = channel.Request(
                new ShareArgs(Text: "dialog", Count: 1),
                cancellation: TestContext.Current.CancellationToken
            );
            PlatformChannel.Dispatch();
            Assert.Equal(expected: new ShareReply(Id: "dialog", Ok: true), actual: await request);
        }
        finally
        {
            PlatformChannel.Unhandle(channel.Name);
        }
    }

    [Fact]
    public async Task Request_FaultedHandler_SurfacesAsPlatformChannelException()
    {
        var channel = new JsonChannel<ShareArgs, ShareReply>(
            name: "test.json.fault",
            argsInfo: ChannelTestJson.Default.ShareArgs,
            replyInfo: ChannelTestJson.Default.ShareReply
        );
        channel.HandleRequests(_ =>
            Task.FromException<ShareReply>(new InvalidOperationException("denied by user"))
        );
        try
        {
            var request = channel.Request(
                new ShareArgs(Text: "x", Count: 0),
                cancellation: TestContext.Current.CancellationToken
            );
            PlatformChannel.Dispatch();
            var error = await Assert.ThrowsAsync<PlatformChannelException>(() => request);
            Assert.Equal(expected: "denied by user", actual: error.Message);
        }
        finally
        {
            PlatformChannel.Unhandle(channel.Name);
        }
    }

    [Fact]
    public void Events_DeliverTyped_AndUnsubscribeByDisposing()
    {
        var events = new JsonEvents<ShareArgs>(
            name: "test.json.events",
            payloadInfo: ChannelTestJson.Default.ShareArgs
        );
        var seen = new List<ShareArgs>();
        using (events.Listen(seen.Add))
        {
            events.Send(new ShareArgs(Text: "tick", Count: 1));
            PlatformChannel.Dispatch();
        }

        // Disposed: the second event has nobody to reach.
        events.Send(new ShareArgs(Text: "tock", Count: 2));
        PlatformChannel.Dispatch();

        Assert.Equal(expected: [new ShareArgs(Text: "tick", Count: 1)], actual: seen);
    }
}
