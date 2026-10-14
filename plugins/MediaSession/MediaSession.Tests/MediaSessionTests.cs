using Xunit;

namespace MediaSession.Tests;

/// <summary>
///     The shared rules every driver leans on: what counts as a new track, how a relative seek
///     lands, and what an app id becomes on the bus. The drivers themselves need a real session.
/// </summary>
public class MediaSessionTests
{
    private static readonly NowPlaying Song = new("Song")
    {
        Artist = "Artist", Duration = TimeSpan.FromMinutes(3), Position = TimeSpan.FromSeconds(30)
    };

    [Fact]
    public void SameItem_IgnoresPlaybackState_ButNotIdentity()
    {
        Assert.True(Song.SameItem(Song with { Playing = true, Position = TimeSpan.FromMinutes(2) }));
        Assert.False(Song.SameItem(Song with { Title = "Other" }));
        Assert.False(Song.SameItem(Song with { ArtPath = "/cover.png" }));
        Assert.False(Song.SameItem(null));
    }

    [Fact]
    public void SeekTarget_ClampsToTheTrack()
    {
        Assert.Equal(TimeSpan.FromSeconds(40), Song.SeekTarget(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, Song.SeekTarget(TimeSpan.FromMinutes(-5)));
        Assert.Equal(Song.Duration, Song.SeekTarget(TimeSpan.FromMinutes(5)));
        // A live stream has no end to clamp to.
        var live = Song with { Duration = TimeSpan.Zero };
        Assert.Equal(TimeSpan.FromMinutes(5.5), live.SeekTarget(TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData("dev.zigote.Timbre", "dev.zigote.Timbre")]
    [InlineData("com.my-app.Player", "com.my_app.Player")]
    [InlineData("org.7zip..app", "org._7zip.app")]
    [InlineData("", "app")]
    public void BusNameElement_IsAValidBusName(string appId, string expected)
        => Assert.Equal(expected, Mpris.BusNameElement(appId));

    [Fact]
    public async Task Client_OffLinux_IsInactive_AndSilent()
    {
        if (OperatingSystem.IsLinux()) return; // a real session bus answers differently
        using var client = new MediaSessionClient("dev.zigote.Test", "Test");
        await client.StartAsync();
        client.Update(Song);
        client.Seeked(TimeSpan.Zero);
        client.Update(null);
        Assert.False(client.Active);
    }
}
