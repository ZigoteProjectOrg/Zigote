using Xunit;
using Zigote.Editor.History;

namespace Zigote.Tests;

/// <summary>Undo/redo stack semantics — the safety net under every inspector edit.</summary>
public class CommandHistoryTests
{
    [Fact]
    public void Execute_AppliesAndEnablesUndo()
    {
        var cell = new[] { 0 };
        var h = new CommandHistory();
        h.Execute(new AddCommand(cell, 5));
        Assert.Equal(5, cell[0]);
        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Undo_Then_Redo_RestoresState()
    {
        var cell = new[] { 0 };
        var h = new CommandHistory();
        h.Execute(new AddCommand(cell, 5));
        h.Execute(new AddCommand(cell, 3));
        Assert.Equal(8, cell[0]);

        h.Undo();
        Assert.Equal(5, cell[0]);
        Assert.True(h.CanRedo);

        h.Redo();
        Assert.Equal(8, cell[0]);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Execute_AfterUndo_ClearsRedoStack()
    {
        var cell = new[] { 0 };
        var h = new CommandHistory();
        h.Execute(new AddCommand(cell, 5));
        h.Undo();
        Assert.True(h.CanRedo);

        h.Execute(new AddCommand(cell, 9)); // a new branch must drop the redo history
        Assert.False(h.CanRedo);
        Assert.Equal(9, cell[0]);
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
        var cell = new[] { 0 };
        var h = new CommandHistory();
        h.Execute(new AddCommand(cell, 1));
        h.Undo();
        h.Clear();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    private sealed class AddCommand(int[] cell, int delta) : ICommand
    {
        public void Execute()
        {
            cell[0] += delta;
        }

        public void Undo()
        {
            cell[0] -= delta;
        }

        public bool TryMergeWith(ICommand other)
        {
            return false;
        }
    }
}