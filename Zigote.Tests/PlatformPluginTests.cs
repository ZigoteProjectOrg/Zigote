using System.Buffers;
using Xunit;
using Zigote.Core.Platform;

namespace Zigote.Tests;

/// <summary>
///     The channel transport and plugin host, exercised entirely on the managed side (no engine in
///     the test process — the transport is expected to degrade to "the platform implements
///     nothing" rather than fail): invoke prefers managed handlers, listeners multicast and
///     unsubscribe individually, requests correlate their reply back to the awaiting task through
///     the frame-pumped dispatch, and plugins start in order, stop in reverse, and replace by name.
/// </summary>
public class PlatformPluginTests
{
    [Fact]
    public void Invoke_PrefersManagedHandler_AndUnhandledReturnsNull()
    {
        PlatformChannel.Handle(channel: "test.echo", handler: p => "got:" + p);
        try
        {
            Assert.Equal(expected: "got:hi", actual: PlatformChannel.Invoke(channel: "test.echo", payload: "hi"));
            Assert.True(PlatformChannel.Supports("test.echo"));
        }
        finally
        {
            PlatformChannel.Unhandle("test.echo");
        }

        // No managed handler and no engine in this process: the honest answer is "nobody home".
        Assert.Null(PlatformChannel.Invoke("test.echo"));
        Assert.False(PlatformChannel.Supports("test.echo"));
    }

    [Fact]
    public void Listen_Multicasts_AndUnlistensIndividually()
    {
        var first = new List<string>();
        var second = new List<string>();
        Action<string> onFirst = first.Add;
        Action<string> onSecond = second.Add;

        PlatformChannel.Listen(channel: "test.multi", onMessage: onFirst);
        PlatformChannel.Listen(channel: "test.multi", onMessage: onSecond);
        try
        {
            PlatformChannel.Send(channel: "test.multi", payload: "a");
            PlatformChannel.Dispatch();
            Assert.Equal(expected: ["a"], actual: first);
            Assert.Equal(expected: ["a"], actual: second);

            PlatformChannel.Unlisten(channel: "test.multi", onMessage: onFirst);
            PlatformChannel.Send(channel: "test.multi", payload: "b");
            PlatformChannel.Dispatch();
            Assert.Equal(expected: ["a"], actual: first);
            Assert.Equal(expected: ["a", "b"], actual: second);
        }
        finally
        {
            PlatformChannel.Unlisten("test.multi");
        }
    }

    [Fact]
    public async Task Request_CompletesOnDispatch_WithCorrelatedReply()
    {
        string? seenPayload = null;
        PlatformChannel.Handle(
            channel: "test.permission",
            handler: envelope =>
            {
                (string token, string payload) = PlatformChannel.ParseRequest(envelope);
                seenPayload = payload;
                // The "async" platform work answers later — here, immediately after acking.
                PlatformChannel.Respond(token: token, reply: "granted");
                return "";
            }
        );
        try
        {
            var request = PlatformChannel.Request(
                channel: "test.permission",
                payload: "camera",
                cancellation: TestContext.Current.CancellationToken
            );
            Assert.False(request.IsCompleted); // the reply sits queued until the frame pump runs
            PlatformChannel.Dispatch();
            Assert.Equal(expected: "granted", actual: await request);
            Assert.Equal(expected: "camera", actual: seenPayload);
        }
        finally
        {
            PlatformChannel.Unhandle("test.permission");
        }
    }

    [Fact]
    public async Task Request_UnhandledChannel_CompletesNullImmediately()
    {
        var request = PlatformChannel.Request(
            channel: "test.nobody",
            payload: "x",
            cancellation: TestContext.Current.CancellationToken
        );
        Assert.True(request.IsCompleted);
        Assert.Null(await request);
    }

    [Fact]
    public async Task Request_Cancellation_CancelsTask_AndLateReplyIsDropped()
    {
        string? token = null;
        PlatformChannel.Handle(
            channel: "test.slow",
            handler: envelope =>
            {
                token = PlatformChannel.ParseRequest(envelope).Token;
                return ""; // acknowledged, reply never sent before the caller gives up
            }
        );
        try
        {
            using var cts = new CancellationTokenSource();
            var request = PlatformChannel.Request(
                channel: "test.slow",
                payload: "",
                cancellation: cts.Token
            );
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

            // The answer arriving after cancellation has nobody waiting — it must drain silently.
            PlatformChannel.Respond(token: token!, reply: "too late");
            PlatformChannel.Dispatch();
        }
        finally
        {
            PlatformChannel.Unhandle("test.slow");
        }
    }

    [Fact]
    public void Listen_ReturnsDisposableSubscription_DisposeIsIdempotent()
    {
        var seen = new List<string>();
        var subscription = PlatformChannel.Listen(channel: "test.sub", onMessage: seen.Add);

        PlatformChannel.Send(channel: "test.sub", payload: "a");
        PlatformChannel.Dispatch();
        subscription.Dispose();
        subscription.Dispose(); // second dispose must not throw or unsubscribe someone else
        PlatformChannel.Send(channel: "test.sub", payload: "b");
        PlatformChannel.Dispatch();

        Assert.Equal(expected: ["a"], actual: seen);
    }

    [Fact]
    public void ByteInvoke_RoundTripsBinary_IncludingEmbeddedZeros()
    {
        PlatformChannel.Handle(
            channel: "test.bytes",
            handler: (ReadOnlySpan<byte> payload, IBufferWriter<byte> reply) =>
            {
                reply.Write(payload);
                reply.Write("!"u8);
                return true;
            }
        );
        try
        {
            byte[] binary = [1, 0, 2, 0, 3];
            var reply = new ArrayBufferWriter<byte>();
            Assert.True(PlatformChannel.Invoke(channel: "test.bytes", payload: binary, reply: reply));
            Assert.Equal(expected: (byte[]) [1, 0, 2, 0, 3, (byte)'!'], actual: reply.WrittenSpan.ToArray());
        }
        finally
        {
            PlatformChannel.Unhandle("test.bytes");
        }
    }

    [Fact]
    public void Invoke_CrossesRepresentations_BothWays()
    {
        // Text caller reaching a byte-shaped implementation…
        PlatformChannel.Handle(
            channel: "test.cross.bytes",
            handler: (ReadOnlySpan<byte> payload, IBufferWriter<byte> reply) =>
            {
                reply.Write(payload);
                return true;
            }
        );
        // …and a byte caller reaching a text-shaped one.
        PlatformChannel.Handle(channel: "test.cross.text", handler: p => p.ToUpperInvariant());
        try
        {
            Assert.Equal(
                expected: "mirror",
                actual: PlatformChannel.Invoke(channel: "test.cross.bytes", payload: "mirror")
            );
            Assert.True(PlatformChannel.Supports("test.cross.bytes"));

            var reply = new ArrayBufferWriter<byte>();
            Assert.True(PlatformChannel.Invoke(channel: "test.cross.text", payload: "up"u8, reply: reply));
            Assert.Equal(expected: "UP"u8.ToArray(), actual: reply.WrittenSpan.ToArray());
        }
        finally
        {
            PlatformChannel.Unhandle("test.cross.bytes");
            PlatformChannel.Unhandle("test.cross.text");
        }
    }

    [Fact]
    public void ParseRequest_SplitsTokenFromPayload()
    {
        Assert.Equal(expected: ("7", "a\nb"), actual: PlatformChannel.ParseRequest("7\na\nb"));
        Assert.Equal(expected: ("7", ""), actual: PlatformChannel.ParseRequest("7"));
    }

    private sealed class RecordingPlugin(string name, List<string> journal) : IPlatformPlugin
    {
        public string Name => name;
        public void Start() => journal.Add(name + ":start");
        public void Stop() => journal.Add(name + ":stop");
    }

    [Fact]
    public void PluginHost_StartsInOrder_StopsInReverse_AndReplacesByName()
    {
        var journal = new List<string>();
        try
        {
            PluginHost.Register(new RecordingPlugin(name: "t.alpha", journal: journal));
            PluginHost.Register(new RecordingPlugin(name: "t.beta", journal: journal));
            // Same name before start: replaced, not duplicated — only one start below.
            PluginHost.Register(new RecordingPlugin(name: "t.alpha", journal: journal));
            Assert.True(PluginHost.IsRegistered("t.alpha"));

            PluginHost.StartAll();
            PluginHost.StartAll(); // idempotent
            Assert.Equal(expected: ["t.alpha:start", "t.beta:start"], actual: journal);

            // Late registration starts immediately; replacing a running plugin stops the old one.
            PluginHost.Register(new RecordingPlugin(name: "t.gamma", journal: journal));
            PluginHost.Register(new RecordingPlugin(name: "t.beta", journal: journal));
            Assert.Equal(
                expected: ["t.alpha:start", "t.beta:start", "t.gamma:start", "t.beta:stop", "t.beta:start"],
                actual: journal
            );

            journal.Clear();
            PluginHost.StopAll();
            Assert.Equal(expected: ["t.gamma:stop", "t.beta:stop", "t.alpha:stop"], actual: journal);

            // Registrations survive a stop, so an app relaunch starts the same set again.
            journal.Clear();
            PluginHost.StartAll();
            Assert.Equal(expected: ["t.alpha:start", "t.beta:start", "t.gamma:start"], actual: journal);
        }
        finally
        {
            PluginHost.StopAll();
        }
    }

    [Fact]
    public void PluginHost_RaisesStartedAndStopped_AndExposesRunning()
    {
        var events = new List<string>();
        Action<IPlatformPlugin> onStarted = p => events.Add(p.Name + ":started");
        Action<IPlatformPlugin> onStopped = p => events.Add(p.Name + ":stopped");
        PluginHost.PluginStarted += onStarted;
        PluginHost.PluginStopped += onStopped;
        try
        {
            var journal = new List<string>();
            PluginHost.Register(new RecordingPlugin(name: "t.events", journal: journal));
            Assert.Empty(PluginHost.Running); // registered but the host has not started

            PluginHost.StartAll();
            Assert.Contains(expected: "t.events:started", collection: events);
            Assert.Contains(collection: PluginHost.Running, filter: p => p.Name == "t.events");

            // Replacing a running plugin reports the old one stopped and the new one started.
            events.Clear();
            PluginHost.Register(new RecordingPlugin(name: "t.events", journal: journal));
            Assert.Equal(expected: ["t.events:stopped", "t.events:started"], actual: events);

            events.Clear();
            PluginHost.StopAll();
            Assert.Contains(expected: "t.events:stopped", collection: events);
            Assert.Empty(PluginHost.Running);
        }
        finally
        {
            PluginHost.PluginStarted -= onStarted;
            PluginHost.PluginStopped -= onStopped;
            PluginHost.StopAll();
        }
    }
}
