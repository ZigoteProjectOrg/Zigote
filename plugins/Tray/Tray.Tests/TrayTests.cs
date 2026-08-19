using Xunit;
using Zigote.Core.Engine;

namespace Tray.Tests;

public class TrayTests
{
    [Fact]
    public void Separator_RoundTrips()
    {
        Assert.True(TrayMenuItem.Separator.IsSeparator);
        Assert.False(new TrayMenuItem(1, "Play").IsSeparator);
        // Tag 0 with a label is a real item, not a separator.
        Assert.False(new TrayMenuItem(0, "Zero").IsSeparator);
    }

    [Fact]
    public void EscapeMnemonics_DoublesUnderscores()
    {
        Assert.Equal("Play__Pause", StatusNotifierItem.EscapeMnemonics("Play_Pause"));
        Assert.Equal("____", StatusNotifierItem.EscapeMnemonics("__"));
        Assert.Equal("Plain", StatusNotifierItem.EscapeMnemonics("Plain"));
    }

    [Fact]
    public void BeforeStart_IsInertAndSafe()
    {
        // No bus interaction until StartAsync: constructing, mutating and disposing must be
        // side-effect-free so tests (and headless apps) never touch a real session bus.
        using var sni = new StatusNotifierItem("dev.zigote.Test", "Test", "tip", _ => { }, () => { });
        Assert.False(sni.Running);
        Assert.Null(sni.LastError);
        sni.SetTooltip("new tip");
        sni.SetMenu([new TrayMenuItem(1, "Item"), TrayMenuItem.Separator]);
    }
}
