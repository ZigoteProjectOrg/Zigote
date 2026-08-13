using Xunit;
using Zigote.Graphs.Commands;
using Zigote.Graphs.Core;

namespace Zigote.Tests;

/// <summary>
///     Regression guard for the dirty-tracking fix: undoing/redoing back to the saved checkpoint must
///     report clean, instead of the previous behaviour where every Undo/Redo set IsDirty = true.
/// </summary>
public class GraphCommandStackTests
{
    private static GraphCommandStack NewStack() => new(new GraphDocument());

    [Fact]
    public void FreshStack_IsClean()
    {
        var s = NewStack();
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Execute_MarksDirty_AndMarkCleanClears()
    {
        var s = NewStack();
        s.Execute(new NoopCommand());
        Assert.True(s.IsDirty);

        s.MarkClean();
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void UndoBackToSavedCheckpoint_ReportsClean()
    {
        var s = NewStack();
        s.Execute(new NoopCommand()); // depth 1
        s.MarkClean(); // saved at depth 1
        s.Execute(new NoopCommand()); // depth 2 — dirty
        Assert.True(s.IsDirty);

        s.Undo(); // back to depth 1 == saved checkpoint
        Assert.False(s.IsDirty); // previously this stayed true — the bug

        s.Redo(); // forward to depth 2 again
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void UndoPastSavedCheckpoint_IsDirty()
    {
        var s = NewStack();
        s.Execute(new NoopCommand());
        s.MarkClean(); // saved at depth 1
        s.Undo(); // back to empty — differs from saved
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void SavedAtEmpty_RedoThenUndo_ReturnsToClean()
    {
        var s = NewStack(); // saved at empty by default
        s.Execute(new NoopCommand());
        Assert.True(s.IsDirty);
        s.Undo(); // back to empty == saved
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Clear_ResetsToClean()
    {
        var s = NewStack();
        s.Execute(new NoopCommand());
        s.Clear();
        Assert.False(s.IsDirty);
        Assert.False(s.CanUndo);
        Assert.False(s.CanRedo);
    }

    private sealed class NoopCommand : IGraphCommand
    {
        public void Execute(GraphDocument graph) { }

        public void Undo(GraphDocument graph) { }
    }
}
