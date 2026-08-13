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
            Name: name,
            FullPath: "/root/" + name,
            IsDirectory: true,
            Size: -1,
            Modified: new DateTime(year: 2026, month: 1, day: 1),
            IsHidden: name.StartsWith('.')
        );
    }

    private static FileBrowserEntry File(string name, long size = 10, int day = 1)
    {
        return new FileBrowserEntry(
            Name: name,
            FullPath: "/root/" + name,
            IsDirectory: false,
            Size: size,
            Modified: new DateTime(year: 2026, month: 1, day: day),
            IsHidden: name.StartsWith('.')
        );
    }

    private static FileBrowserModel Loaded(params FileBrowserEntry[] entries)
    {
        var model = new FileBrowserModel();
        model.SetEntries(directory: "/root", entries: entries);
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
            expectedSpan: ["Alpha", "beta", "alpha.txt", "zeta.txt"],
            actualArray: model.Visible.Select(e => e.Name).ToArray()
        );
    }

    [Fact]
    public void SortBySize_TogglesDirection_DirectoriesStayFirst()
    {
        var model = Loaded(
            File(name: "big.txt", size: 300),
            File(name: "small.txt", size: 1),
            Dir("sub")
        );
        model.SortBy(FileSortColumn.Size);
        Assert.Equal(
            expectedSpan: ["sub", "small.txt", "big.txt"],
            actualArray: model.Visible.Select(e => e.Name).ToArray()
        );

        model.SortBy(FileSortColumn.Size); // same column again flips to descending
        Assert.False(model.SortAscending);
        Assert.Equal(
            expectedSpan: ["sub", "big.txt", "small.txt"],
            actualArray: model.Visible.Select(e => e.Name).ToArray()
        );
    }

    [Fact]
    public void HiddenEntries_FilteredUntilToggled()
    {
        var model = Loaded(File(".secret"), File("visible.txt"), Dir(".git"));
        Assert.Equal(
            expectedSpan: ["visible.txt"],
            actualArray: model.Visible.Select(e => e.Name).ToArray()
        );

        model.ShowHidden = true;
        model.ApplyView();
        Assert.Equal(expected: 3, actual: model.Visible.Count);
    }

    [Fact]
    public void ExtensionFilter_AppliesToFilesOnly_AndStarAdmitsAll()
    {
        var model = Loaded(File("a.png"), File("b.txt"), Dir("assets"));
        model.ExtensionFilter = ["png"];
        model.ApplyView();
        Assert.Equal(
            expectedSpan: ["assets", "a.png"],
            actualArray: model.Visible.Select(e => e.Name).ToArray()
        );

        model.ExtensionFilter = ["*"];
        model.ApplyView();
        Assert.Equal(expected: 3, actual: model.Visible.Count);
    }

    [Fact]
    public void Search_FiltersBySubstring_CaseInsensitive()
    {
        var model = Loaded(File("MainScene.scene"), File("other.scene"), Dir("scenes"));
        model.SearchText = "main";
        model.ApplyView();
        Assert.Equal(
            expectedSpan: ["MainScene.scene"],
            actualArray: model.Visible.Select(e => e.Name).ToArray()
        );
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
        model.SelectIndex(index: 2, range: true); // shift-click: a..c
        Assert.Equal(expected: 3, actual: model.SelectedPaths.Count);

        model.SelectIndex(index: 1, toggle: true); // cmd-click removes b
        Assert.Equal(expected: 2, actual: model.SelectedPaths.Count);

        model.SelectIndex(3); // plain click replaces
        Assert.Equal(
            expectedSpan: ["d"],
            actualArray: model.SelectedEntries().Select(e => e.Name).ToArray()
        );

        model.AllowMultiSelect = false;
        model.SelectIndex(index: 0, toggle: true); // toggle demotes to plain in single-select mode
        Assert.Equal(
            expectedSpan: ["a"],
            actualArray: model.SelectedEntries().Select(e => e.Name).ToArray()
        );
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
        Assert.Equal(expected: 1, actual: model.TypeAheadIndex(prefix: "b", from: 0)); // banana
        Assert.Equal(
            expected: 2,
            actual: model.TypeAheadIndex(prefix: "b", from: 1)
        ); // berry, scanning on from banana
        Assert.Equal(
            expected: 1,
            actual: model.TypeAheadIndex(prefix: "b", from: 2)
        ); // wraps back around to banana
        Assert.Equal(expected: -1, actual: model.TypeAheadIndex(prefix: "zzz", from: 0));
    }

    [Fact]
    public void History_BackForward_TracksNavigation()
    {
        var rootDir = Directory.CreateTempSubdirectory("zigote-browser-test");
        try
        {
            var child =
                Directory.CreateDirectory(Path.Combine(path1: rootDir.FullName, path2: "child"));
            var model = new FileBrowserModel();
            model.NavigateTo(rootDir.FullName);
            model.NavigateTo(child.FullName);
            Assert.True(model.CanGoBack);

            model.GoBack();
            Assert.Equal(
                expected: Path.GetFullPath(rootDir.FullName),
                actual: model.CurrentDirectory
            );
            Assert.True(model.CanGoForward);

            model.GoForward();
            Assert.Equal(
                expected: Path.GetFullPath(child.FullName),
                actual: model.CurrentDirectory
            );
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
            var child =
                Directory.CreateDirectory(Path.Combine(path1: rootDir.FullName, path2: "child"));
            var model = new FileBrowserModel { LockRoot = rootDir.FullName };
            model.NavigateTo(child.FullName);
            Assert.True(model.CanGoUp); // child → root is allowed

            model.GoUp();
            Assert.Equal(
                expected: Path.GetFullPath(rootDir.FullName),
                actual: model.CurrentDirectory
            );
            Assert.False(model.CanGoUp); // may not leave the root

            string outside = Path.GetDirectoryName(rootDir.FullName)!;
            model.NavigateTo(outside); // rejected — outside the lock root
            Assert.Equal(
                expected: Path.GetFullPath(rootDir.FullName),
                actual: model.CurrentDirectory
            );
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
            expectedSpan: ["file1.txt", "file2.txt", "file10.txt"],
            actualArray: model.Visible.Select(e => e.Name).ToArray()
        );

        Assert.True(FileBrowserModel.NaturalCompare(a: "scene9", b: "scene10") < 0);
        Assert.True(
            FileBrowserModel.NaturalCompare(a: "Scene10", b: "scene9") > 0
        ); // case-insensitive
        Assert.True(FileBrowserModel.NaturalCompare(a: "a07", b: "a7") > 0); // zeros break the tie
        Assert.Equal(expected: 0, actual: FileBrowserModel.NaturalCompare(a: "Same", b: "same"));
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
