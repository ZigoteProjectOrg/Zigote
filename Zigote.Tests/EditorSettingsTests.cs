using Xunit;
using Zigote.Editor.Settings;
using Zigote.Persistence;
using Zigote.Preferences;

namespace Zigote.Tests;

[Collection("Reactive-serial")] // preferences sit on the reactive graph's process-static state
public sealed class EditorSettingsTests
{
    [Fact]
    public void Defaults_AreTheFactorySettings()
    {
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var settings = new EditorSettings(store);

        Assert.True(settings.ReopenLastProject.Value);
        Assert.True(settings.NativeMenuBar.Value);
        Assert.Equal(EditorThemeMode.System, settings.ThemeMode.Value);
        Assert.Null(settings.UiFontPath.Value);
        Assert.Equal(13f, settings.UiFontSize.Value);
        Assert.Null(settings.EditorFontPath.Value);
        Assert.Equal(13f, settings.EditorFontSize.Value);
        Assert.Equal(0f, settings.ConsoleFontSize.Value);
        Assert.False(settings.ConsoleClearOnPlay.Value);
        Assert.True(settings.VSync.Value);
        Assert.False(settings.ReducedEditorGraphics.Value);

        Assert.All(settings.Preferences, p => Assert.StartsWith("editor.", p.Key));
        Assert.All(settings.Preferences, p => Assert.False(p.IsSet));
    }

    [Fact]
    public void ResetAll_LeavesProjectHistoryAlone()
    {
        // The Settings window's "Reset All" is the "editor" group's Reset — the "projects" group
        // on the same store must keep its data.
        using var store = new PreferenceStore(new InMemoryKeyValueStore());
        var settings = new EditorSettings(store);
        var history = new ProjectHistory(store);

        settings.ThemeMode.Value = EditorThemeMode.Light;
        settings.UiFontSize.Value = 16f;
        history.RecordOpened("/tmp/demo.zigoteproj");

        settings.Reset();

        Assert.Equal(EditorThemeMode.System, settings.ThemeMode.Value);
        Assert.Equal(13f, settings.UiFontSize.Value);
        Assert.Equal([Path.GetFullPath("/tmp/demo.zigoteproj")], history.Recent.Value);
        Assert.Equal(Path.GetFullPath("/tmp/demo.zigoteproj"), history.Last.Value);
    }
}