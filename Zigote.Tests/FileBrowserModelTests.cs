using Xunit;

namespace Zigote.Tests;

/// <summary>
///     Pins the in-app file browser's view pipeline (filter → sort → visible), selection
///     semantics, navigation history, and lock-root clamping — all through
///     <see cref="FileBrowserModel.SetEntries" /> / real temp directories, no UI.
/// </summary>
public class FileBrowserModelTests
{
    private static FileBrowserEntry Dir(string name)
    {
        return new FileBrowserEntry(
            name,
            "/root/" + name,
            true,
            -1,
            new DateTime(2026, 1, 1),
            name.StartsWith('.')
        );
    }

    private static FileBrowserEntry File(string name, long size = 10, int day = 1)
    {
        return new FileBrowserEntry(
            name,
            "/root/" + name,
            false,
            size,
            new DateTime(2026, 1, day),
            name.StartsWith('.')
        );
    }

    private static FileBrowserModel Loaded(params FileBrowserEntry[] entries)
    {
        var model = new FileBrowserModel();
        model.SetEntries("/root", entries);
        return model;
    }

    [Fact]
    public void DirectoriesGroupFirst_ThenSortedByName()
    {
        var model = Loaded(
            File("zeta.txt"),
            Dir("beta"),
            File("alpha.txt"),
            Dir("Alpha")
        );
        Assert.Equal(
            ["Alpha", "beta", "alpha.txt", "zeta.txt"],
            model.Visible.Select(e => e.Name).ToArray()
        );
    }

    [Fact]
    public void SortBySize_TogglesDirection_DirectoriesStayFirst()
    {
        var model = Loaded(File("big.txt", 300), File("small.txt", 1), Dir("sub"));
        model.SortBy(FileSortColumn.Size);
        Assert.Equal(
            ["sub", "small.txt", "big.txt"],
            model.Visible.Select(e => e.Name).ToArray()
        );

        model.SortBy(FileSortColumn.Size); // same column again flips to descending
        Assert.False(model.SortAscending);
        Assert.Equal(
            ["sub", "big.txt", "small.txt"],
            model.Visible.Select(e => e.Name).ToArray()
        );
    }

    [Fact]
    public void HiddenEntries_FilteredUntilToggled()
    {
        var model = Loaded(File(".secret"), File("visible.txt"), Dir(".git"));
        Assert.Equal(["visible.txt"], model.Visible.Select(e => e.Name).ToArray());

        model.ShowHidden = true;
        model.ApplyView();
        Assert.Equal(3, model.Visible.Count);
    }

    [Fact]
    public void ExtensionFilter_AppliesToFilesOnly_AndStarAdmitsAll()
    {
        var model = Loaded(File("a.png"), File("b.txt"), Dir("assets"));
        model.ExtensionFilter = ["png"];
        model.ApplyView();
        Assert.Equal(["assets", "a.png"], model.Visible.Select(e => e.Name).ToArray());

        model.ExtensionFilter = ["*"];
        model.ApplyView();
        Assert.Equal(3, model.Visible.Count);
    }

    [Fact]
    public void Search_FiltersBySubstring_CaseInsensitive()
    {
        var model = Loaded(File("MainScene.scene"), File("other.scene"), Dir("scenes"));
        model.SearchText = "main";
        model.ApplyView();
        Assert.Equal(["MainScene.scene"], model.Visible.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Selection_PlainReplaces_ToggleAndRangeNeedMultiSelect()
    {
        var model = Loaded(
            File("a"),
            File("b"),
            File("c"),
            File("d")
        );
        model.AllowMultiSelect = true;

        model.SelectIndex(0);
        model.SelectIndex(2, range: true); // shift-click: a..c
        Assert.Equal(3, model.SelectedPaths.Count);

        model.SelectIndex(1, true); // cmd-click removes b
        Assert.Equal(2, model.SelectedPaths.Count);

        model.SelectIndex(3); // plain click replaces
        Assert.Equal(["d"], model.SelectedEntries().Select(e => e.Name).ToArray());

        model.AllowMultiSelect = false;
        model.SelectIndex(0, true); // toggle demotes to plain in single-select mode
        Assert.Equal(["a"], model.SelectedEntries().Select(e => e.Name).ToArray());
    }

    [Fact]
    public void TypeAhead_MatchesPrefix_AndWraps()
    {
        var model = Loaded(
            File("apple"),
            File("banana"),
            File("berry"),
            File("cherry")
        );
        Assert.Equal(1, model.TypeAheadIndex("b", 0)); // banana
        Assert.Equal(2, model.TypeAheadIndex("b", 1)); // berry, scanning on from banana
        Assert.Equal(1, model.TypeAheadIndex("b", 2)); // wraps back around to banana
        Assert.Equal(-1, model.TypeAheadIndex("zzz", 0));
    }

    [Fact]
    public void History_BackForward_TracksNavigation()
    {
        var rootDir = Directory.CreateTempSubdirectory("zigote-browser-test");
        try
        {
            var child = Directory.CreateDirectory(Path.Combine(rootDir.FullName, "child"));
            var model = new FileBrowserModel();
            model.NavigateTo(rootDir.FullName);
            model.NavigateTo(child.FullName);
            Assert.True(model.CanGoBack);

            model.GoBack();
            Assert.Equal(Path.GetFullPath(rootDir.FullName), model.CurrentDirectory);
            Assert.True(model.CanGoForward);

            model.GoForward();
            Assert.Equal(Path.GetFullPath(child.FullName), model.CurrentDirectory);
        }
        finally
        {
            rootDir.Delete(true);
        }
    }

    [Fact]
    public void LockRoot_ClampsUpAndNavigation()
    {
        var rootDir = Directory.CreateTempSubdirectory("zigote-browser-lock");
        try
        {
            var child = Directory.CreateDirectory(Path.Combine(rootDir.FullName, "child"));
            var model = new FileBrowserModel { LockRoot = rootDir.FullName };
            model.NavigateTo(child.FullName);
            Assert.True(model.CanGoUp); // child → root is allowed

            model.GoUp();
            Assert.Equal(Path.GetFullPath(rootDir.FullName), model.CurrentDirectory);
            Assert.False(model.CanGoUp); // may not leave the root

            var outside = Path.GetDirectoryName(rootDir.FullName)!;
            model.NavigateTo(outside); // rejected — outside the lock root
            Assert.Equal(Path.GetFullPath(rootDir.FullName), model.CurrentDirectory);
        }
        finally
        {
            rootDir.Delete(true);
        }
    }

    [Fact]
    public void NaturalSort_ComparesDigitRunsNumerically()
    {
        var model = Loaded(File("file10.txt"), File("file2.txt"), File("file1.txt"));
        Assert.Equal(
            ["file1.txt", "file2.txt", "file10.txt"],
            model.Visible.Select(e => e.Name).ToArray()
        );

        Assert.True(FileBrowserModel.NaturalCompare("scene9", "scene10") < 0);
        Assert.True(FileBrowserModel.NaturalCompare("Scene10", "scene9") > 0); // case-insensitive
        Assert.True(FileBrowserModel.NaturalCompare("a07", "a7") > 0); // zeros break the tie
        Assert.Equal(0, FileBrowserModel.NaturalCompare("Same", "same"));
    }

    [Fact]
    public void Load_MissingDirectory_ReportsErrorInsteadOfThrowing()
    {
        var model = new FileBrowserModel();
        model.Load("/definitely/not/a/real/dir");
        Assert.NotNull(model.LastError);
        Assert.Empty(model.Visible);
    }
}
