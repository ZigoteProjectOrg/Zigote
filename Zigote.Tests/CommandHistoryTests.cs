using Xunit;
using Zigote.Editor.History;

namespace Zigote.Tests;

/// <summary>Undo/redo stack semantics — the safety net under every inspector edit.</summary>
public class CommandHistoryTests
{
    [Fact]
    public void Execute_AppliesAndEnablesUndo()
    {
        int[] cell = new[] { 0 };
        var h = new CommandHistory();
        h.Execute(new AddCommand(cell: cell, delta: 5));
        Assert.Equal(expected: 5, actual: cell[0]);
        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Undo_Then_Redo_RestoresState()
    {
        int[] cell = new[] { 0 };
        var h = new CommandHistory();
        h.Execute(new AddCommand(cell: cell, delta: 5));
        h.Execute(new AddCommand(cell: cell, delta: 3));
        Assert.Equal(expected: 8, actual: cell[0]);

        h.Undo();
        Assert.Equal(expected: 5, actual: cell[0]);
        Assert.True(h.CanRedo);

        h.Redo();
        Assert.Equal(expected: 8, actual: cell[0]);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Execute_AfterUndo_ClearsRedoStack()
    {
        int[] cell = new[] { 0 };
        var h = new CommandHistory();
        h.Execute(new AddCommand(cell: cell, delta: 5));
        h.Undo();
        Assert.True(h.CanRedo);

        h.Execute(new AddCommand(cell: cell, delta: 9)); // a new branch must drop the redo history
        Assert.False(h.CanRedo);
        Assert.Equal(expected: 9, actual: cell[0]);
    }

    [Fact]
    public void UndoRedo_OnEmpty_AreNoOps()
    {
        var h = new CommandHistory();
        h.Undo(); // must not throw
        h.Redo();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Clear_DropsBothStacks()
    {
        int[] cell = new[] { 0 };
        var h = new CommandHistory();
        h.Execute(new AddCommand(cell: cell, delta: 1));
        h.Undo();
        h.Clear();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    private sealed class AddCommand(int[] cell, int delta) : ICommand
    {
        public void Execute() => cell[0] += delta;

        public void Undo() => cell[0] -= delta;

        public bool TryMergeWith(ICommand other) => false;
    }
}
