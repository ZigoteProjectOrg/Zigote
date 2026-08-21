using Xunit;

namespace Push.Tests;

/// <summary>
///     The wire format and the delivery bookkeeping — the two places a push can be lost. The
///     transports themselves are the OS's (or the app's) code.
/// </summary>
public class PushTests
{
    [Fact]
    public void Parse_ReadsTitleBodyDataAndTap()
    {
        var message = PushPlugin.Parse(
            """{"title":"Hi","body":"There","tapped":true,"data":{"id":"42","n":7}}""");

        Assert.Equal("Hi", message.Title);
        Assert.Equal("There", message.Body);
        Assert.True(message.Tapped);
        Assert.Equal("42", message.Data["id"]);
        // Non-string values keep their JSON text rather than being dropped.
        Assert.Equal("7", message.Data["n"]);
    }

    [Fact]
    public void Parse_SurvivesTransportsThatSendSomethingElse()
    {
        var bare = PushPlugin.Parse("just text");
        Assert.Equal("just text", bare.Body);
        Assert.Empty(bare.Data);

        var empty = PushPlugin.Parse("");
        Assert.Null(empty.Body);
        Assert.Empty(empty.Data);
        Assert.False(empty.Tapped);
    }

    [Fact]
    public void Messages_ReachEveryListener_UntilDisposed()
    {
        List<PushMessage> first = [];
        List<PushMessage> second = [];
        var a = PushPlugin.OnMessage(first.Add);
        var b = PushPlugin.OnMessage(second.Add);

        var message = PushPlugin.Parse("""{"body":"one"}""");
        PushPlugin.DeliverMessage(message);
        a.Dispose();
        PushPlugin.DeliverMessage(PushPlugin.Parse("""{"body":"two"}"""));
        b.Dispose();

        Assert.Equal(["one"], first.Select(m => m.Body));
        Assert.Equal(["one", "two"], second.Select(m => m.Body));
    }

    [Fact]
    public void Token_IsRememberedAndReplayedToLateListeners()
    {
        PushPlugin.DeliverToken("abc123");
        Assert.Equal("abc123", PushPlugin.Token);

        string? late = null;
        using var subscription = PushPlugin.OnToken(t => late = t);
        Assert.Equal("abc123", late);   // a listener that arrives after the token still gets it

        PushPlugin.DeliverToken("abc123");   // unchanged — not re-announced
        PushPlugin.DeliverToken("def456");
        Assert.Equal("def456", late);
    }

    [Fact]
    public async Task Desktop_HasNoPush()
    {
        Assert.False(PushPlugin.Available);
        Assert.Null(await PushPlugin.RegisterAsync(TestContext.Current.CancellationToken));
    }
}
