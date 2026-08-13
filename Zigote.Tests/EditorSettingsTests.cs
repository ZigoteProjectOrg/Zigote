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
        Assert.Equal(expected: EditorThemeMode.System, actual: settings.ThemeMode.Value);
        Assert.Null(settings.UiFontPath.Value);
        Assert.Equal(expected: 13f, actual: settings.UiFontSize.Value);
        Assert.Null(settings.EditorFontPath.Value);
        Assert.Equal(expected: 13f, actual: settings.EditorFontSize.Value);
        Assert.Equal(expected: 0f, actual: settings.ConsoleFontSize.Value);
        Assert.False(settings.ConsoleClearOnPlay.Value);
        Assert.True(settings.VSync.Value);
        Assert.False(settings.ReducedEditorGraphics.Value);

        Assert.All(
            collection: settings.Preferences,
            action: p => Assert.StartsWith(expectedStartString: "editor.", actualString: p.Key)
        );
        Assert.All(collection: settings.Preferences, action: p => Assert.False(p.IsSet));
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

        Assert.Equal(expected: EditorThemeMode.System, actual: settings.ThemeMode.Value);
        Assert.Equal(expected: 13f, actual: settings.UiFontSize.Value);
        Assert.Equal(
            expectedSpan: [Path.GetFullPath("/tmp/demo.zigoteproj")],
            actualArray: history.Recent.Value
        );
        Assert.Equal(
            expected: Path.GetFullPath("/tmp/demo.zigoteproj"),
            actual: history.Last.Value
        );
    }
}
