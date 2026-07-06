using Xunit;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

namespace Zigote.Tests;

/// <summary>
///     The <see cref="Watch" /> widget — the C# signal→rebuild bridge (counterpart of the F#
///     <c>Ui.bind</c>). Built headlessly: attach, measure, mutate a signal, assert the subtree rebuilt.
/// </summary>
// Serialized with the other reactive tests: exact rebuild-count assertions are sensitive to the
// process-global Reactive.GlobalVersion, which a parallel reactive stress test would otherwise bump.
[Collection("Reactive-serial")]
public class WatchTests
{
    [Fact]
    public void Watch_rebuilds_its_subtree_when_a_read_signal_changes()
    {
        var count = new Signal<int>(0);
        var builds = 0;
        var root = new Watch(() =>
        {
            builds++;
            return new Label($"count: {count.Value}");
        });

        root.Attach(null!, null);
        root.Measure(Constraints.Tight(200f, 100f));

        Assert.Equal("count: 0", Find<Label>(root)!.Text);
        Assert.Equal(1, builds); // eager first build

        count.Value = 3;
        root.Measure(Constraints.Tight(200f, 100f));
        Assert.Equal("count: 3", Find<Label>(root)!.Text);
        Assert.Equal(2, builds); // rebuilt exactly once
    }

    [Fact]
    public void Watch_ignores_signals_the_builder_did_not_read()
    {
        var shown = new Signal<int>(0);
        var unrelated = new Signal<int>(0);
        var builds = 0;
        var root = new Watch(() =>
        {
            builds++;
            return new Label($"{shown.Value}");
        });
        root.Attach(null!, null);
        root.Measure(Constraints.Tight(200f, 100f));
        Assert.Equal(1, builds);

        unrelated.Value = 99; // not read by the builder → no rebuild
        Assert.Equal(1, builds);

        shown.Value = 7;
        Assert.Equal(2, builds);
    }

    [Fact]
    public void Watch_stops_rebuilding_after_detach()
    {
        var count = new Signal<int>(0);
        var builds = 0;
        var root = new Watch(() =>
        {
            builds++;
            return new Label($"{count.Value}");
        });
        root.Attach(null!, null);
        root.Measure(Constraints.Tight(200f, 100f));
        Assert.Equal(1, builds);

        root.Detach();
        count.Value = 5; // detached → the internal computed is disposed → no rebuild
        Assert.Equal(1, builds);
    }

    private static T? Find<T>(Widget w) where T : Widget
    {
        if (w is T match) return match;
        foreach (var c in w.GetChildren())
        {
            var found = Find<T>(c);
            if (found != null) return found;
        }

        return null;
    }
}
