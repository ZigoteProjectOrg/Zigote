using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Zigote.UI.Host;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Rows handed to a ListView in items mode after the list is already in the tree must be attached
///     to it — builder mode does this in ItemAt, items mode used to rely on the Attach cascade and so
///     left every later row ownerless (no Watch ever started; a Draggable inside one refused to drag).
/// </summary>
public class ListViewItemAttachTests
{
    // A real App needs a window and a GPU. Attach/MarkNeedsLayout only touch the repaint tracker,
    // so an uninitialized instance with that one field filled in stands in for one headlessly.
    private static readonly App FakeOwner = FakeApp();

    private static App FakeApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App)
            .GetField("_repaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, new RepaintTracker());
        return app;
    }

    private static ListView AttachedList()
    {
        var list = new ListView(itemExtent: 20f);
        list.Attach(FakeOwner, null);
        return list;
    }

    [Fact]
    public void RowsAddedAfterAttach_GetOwnerAndParent()
    {
        var list = AttachedList();
        var a = new SizedBox(10f, 20f);
        var b = new SizedBox(10f, 20f);

        list.SetItems([a]);
        list.AddItem(b);

        Assert.Same(list, a.Parent);
        Assert.Same(list, b.Parent);
        Assert.Same(FakeOwner, a.Owner);
        Assert.Same(FakeOwner, b.Owner);
    }

    [Fact]
    public void ReplacedRows_AreDetached_ButRetainedOnesSurvive()
    {
        var list = AttachedList();
        var kept = new SizedBox(10f, 20f);
        var dropped = new SizedBox(10f, 20f);
        list.SetItems([kept, dropped]);

        list.SetItems([kept]);

        Assert.Same(list, kept.Parent);
        Assert.Null(dropped.Parent);

        list.Clear();
        Assert.Null(kept.Parent);
    }
}