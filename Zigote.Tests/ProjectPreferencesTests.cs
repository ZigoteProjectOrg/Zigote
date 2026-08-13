using Xunit;
using Zigote.Editor.Settings;
using Zigote.Editor.Widgets;

namespace Zigote.Tests;

[Collection("Reactive-serial")] // preferences sit on the reactive graph's process-static state
public sealed class ProjectPreferencesTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zigote-project-prefs");

    private string ProjectFile => Path.Combine(path1: _dir.FullName, path2: "demo.zigoteproj");

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void PathFor_IsProjectRelative_NextToTheManifest()
    {
        Assert.Equal(
            expected: Path.Combine(path1: _dir.FullName, path2: "demo.prefs.json"),
            actual: ProjectPreferences.PathFor(ProjectFile)
        );
    }

    [Fact]
    public void Defaults_AreTheSessionDefaults()
    {
        using var prefs = new ProjectPreferences(ProjectFile);

        Assert.False(prefs.Viewport.PhysicsWireframe.Value);
        Assert.Equal(expected: 0f, actual: prefs.Viewport.StreamDistance.Value);
        Assert.False(prefs.Viewport.NativeVfx.Value);
        Assert.False(prefs.Viewport.GpuVfx.Value);
        Assert.False(prefs.Viewport.AnimateEditVfx.Value);
        Assert.Equal(expected: 0f, actual: prefs.Viewport.SnapGrid.Value);
        Assert.Null(prefs.Layout.Dock.Value);

        Assert.All(
            collection: prefs.Viewport.Preferences,
            action: p => Assert.StartsWith(expectedStartString: "viewport.", actualString: p.Key)
        );
        Assert.All(
            collection: prefs.Layout.Preferences,
            action: p => Assert.StartsWith(expectedStartString: "layout.", actualString: p.Key)
        );
    }

    [Fact]
    public void ViewportPreferences_RoundTrip_AcrossReopen()
    {
        using (var prefs = new ProjectPreferences(ProjectFile))
        {
            prefs.Viewport.SnapGrid.Value = 0.5f;
            prefs.Viewport.StreamDistance.Value = 250f;
            prefs.Viewport.PhysicsWireframe.Value = true;
        }

        using var reopened = new ProjectPreferences(ProjectFile);
        Assert.Equal(expected: 0.5f, actual: reopened.Viewport.SnapGrid.Value);
        Assert.Equal(expected: 250f, actual: reopened.Viewport.StreamDistance.Value);
        Assert.True(reopened.Viewport.PhysicsWireframe.Value);
        Assert.False(reopened.Viewport.NativeVfx.Value); // untouched → still default, unset
        Assert.False(reopened.Viewport.NativeVfx.IsSet);
    }

    [Fact]
    public void DockLayout_RoundTrips_ThroughThePreference()
    {
        var tree = new DockSplit(
            first: new DockLeaf(["hierarchy", "browser"]) { ActiveIndex = 1 },
            second: new DockLeaf("viewport"),
            vertical: false,
            ratio: 0.25f
        );
        string[] known = ["hierarchy", "browser", "viewport"];

        using (var prefs = new ProjectPreferences(ProjectFile))
            prefs.Layout.Dock.Value = DockLayoutStore.ToData(tree);

        using var reopened = new ProjectPreferences(ProjectFile);
        var restored = DockLayoutStore.FromData(
            data: reopened.Layout.Dock.Value!,
            knownPanels: known.ToHashSet()
        );

        var split = Assert.IsType<DockSplit>(restored);
        Assert.False(split.Vertical);
        Assert.Equal(expected: 0.25f, actual: split.Ratio);
        var first = Assert.IsType<DockLeaf>(split.First);
        Assert.Equal(expected: ["hierarchy", "browser"], actual: first.PanelIds);
        Assert.Equal(expected: 1, actual: first.ActiveIndex);
        Assert.Equal(
            expected: ["viewport"],
            actual: Assert.IsType<DockLeaf>(split.Second).PanelIds
        );
    }

    [Fact]
    public void DockLayout_Restore_DropsUnknownPanels_AndCollapses()
    {
        var data = DockLayoutStore.ToData(
            new DockSplit(
                first: new DockLeaf(["gone", "alsoGone"]),
                second: new DockLeaf(["viewport", "gone"]) { ActiveIndex = 1 },
                vertical: true,
                ratio: 0.5f
            )
        );

        // The left leaf vanishes entirely, so the split collapses to the surviving leaf, and the
        // ActiveIndex is re-clamped to the filtered list.
        var restored = DockLayoutStore.FromData(
            data: data,
            knownPanels: new HashSet<string> { "viewport" }
        );
        var leaf = Assert.IsType<DockLeaf>(restored);
        Assert.Equal(expected: ["viewport"], actual: leaf.PanelIds);
        Assert.Equal(expected: 0, actual: leaf.ActiveIndex);

        // Nothing survives → null → caller falls back to the default arrangement.
        Assert.Null(DockLayoutStore.FromData(data: data, knownPanels: new HashSet<string>()));
    }

    [Fact]
    public void LayoutReset_ReturnsToNoSavedLayout()
    {
        using var prefs = new ProjectPreferences(ProjectFile);
        prefs.Layout.Dock.Value = DockLayoutStore.ToData(new DockLeaf("viewport"));
        Assert.NotNull(prefs.Layout.Dock.Value);

        prefs.Layout.Dock.Reset();

        Assert.Null(prefs.Layout.Dock.Value);
        Assert.False(prefs.Layout.Dock.IsSet);
    }
}
