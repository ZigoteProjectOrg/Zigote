using Xunit;

namespace Notifications.Tests;

public class NotificationsTests
{
    [Fact]
    public void Record_Defaults()
    {
        var n = new Notification("Title", "Body");
        Assert.Equal("Title", n.Title);
        Assert.Equal("Body", n.Body);
        Assert.Null(n.IconPath);
        Assert.Empty(n.Actions);
        Assert.False(n.Resident);
        Assert.False(n.Transient);
        Assert.Null(n.Category);
    }

    [Fact]
    public void Client_BeforeStart_IsInert()
    {
        // Never started: no connection exists, so nothing may throw and nothing reaches a bus.
        using var client = new NotificationClient("dev.zigote.Test", "Test");
        Assert.False(client.SupportsActions);
        client.Show(new Notification("t", "b"));
        client.Show(new Notification("t", "b"), slot: 7);
        client.Close();
        client.Close(7);
        client.Shutdown();
    }

    [Fact]
    public void Client_DisposeTwice_IsSafe()
    {
        var client = new NotificationClient("dev.zigote.Test", "Test");
        client.Dispose();
        client.Dispose();
    }
}
