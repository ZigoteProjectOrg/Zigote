using Xunit;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     The key-based reconciliation that makes per-item widget state survive insert/remove/reorder —
///     CLAUDE.md's stated reason keys exist, and previously untested. Driven through the public
///     <see cref="MultiChildWidget.SetChildren" /> path.
/// </summary>
public class ChildReconcilerTests
{
    private static SizedBox Keyed(int key)
    {
        return new SizedBox { Key = new ValueKey<int>(key) };
    }

    [Fact]
    public void Reorder_PreservesKeyedInstances()
    {
        var a = Keyed(1);
        var b = Keyed(2);
        var row = new Row([a, b]);

        // New instances, same keys, reversed order — reconciler must reuse the originals.
        row.SetChildren([Keyed(2), Keyed(1)]);

        Assert.Same(b, row.Children[0]);
        Assert.Same(a, row.Children[1]);
    }

    [Fact]
    public void Remove_DropsMissingChild_KeepsRemaining()
    {
        var a = Keyed(1);
        var b = Keyed(2);
        var row = new Row([a, b]);

        row.SetChildren([Keyed(1)]); // key 2 is gone

        Assert.Single(row.Children);
        Assert.Same(a, row.Children[0]);
        Assert.DoesNotContain(b, row.Children);
    }

    [Fact]
    public void Insert_AddsNewChild_KeepsExisting()
    {
        var a = Keyed(1);
        var row = new Row([a]);

        var inserted = Keyed(2);
        row.SetChildren([a, inserted]);

        Assert.Equal(2, row.Children.Count);
        Assert.Same(a, row.Children[0]);
        Assert.Same(inserted, row.Children[1]);
    }

    [Fact]
    public void SameReference_IsAlwaysReused_WithoutKeys()
    {
        var a = new SizedBox(1, 1);
        var b = new SizedBox(2, 2);
        var row = new Row([a, b]);

        row.SetChildren([a, b]); // identical references, no keys
        Assert.Same(a, row.Children[0]);
        Assert.Same(b, row.Children[1]);
    }
}
