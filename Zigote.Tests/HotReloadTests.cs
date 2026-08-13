using System.Reflection;
using System.Reflection.Metadata;
using Xunit;
using Zigote.Core;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Headless coverage for <see cref="HotReload" /> — the .NET hot-reload (Edit &amp; Continue)
///     bridge.
///     The hot-reload contract: marking the tree reruns <c>Build()</c> while preserving widget
///     instances — and a widget's fields ARE its state. Also guards the off-thread pending-flag
///     plumbing the metadata-update handler drives.
/// </summary>
public class HotReloadTests
{
    private static void Pump(Widget w)
    {
        w.Measure(Constraints.Loose(width: 200, height: 200));
        w.Layout(Offset.Zero);
    }

    [Fact]
    public void MarkSubtreeForRebuild_RerunsStatelessBuild()
    {
        var w = new CountingStateless();
        Pump(w);
        Assert.Equal(expected: 1, actual: w.Builds);

        Pump(w); // cached — Build not re-run
        Assert.Equal(expected: 1, actual: w.Builds);

        HotReload.MarkSubtreeForRebuild(w);
        Pump(w);
        Assert.Equal(expected: 2, actual: w.Builds);
    }

    [Fact]
    public void MarkSubtreeForRebuild_PreservesStateButRerunsBuild()
    {
        var w = new CountingMounted();
        Pump(w);
        Assert.Equal(expected: 1, actual: w.Mounts);
        Assert.Equal(expected: 1, actual: w.Builds);

        HotReload.MarkSubtreeForRebuild(w);
        Pump(w);

        // The widget instance IS the state, so a reload cannot lose it; only Build re-runs.
        Assert.Equal(expected: 1, actual: w.Mounts); // OnMount NOT re-run
        Assert.Equal(expected: 2, actual: w.Builds); // Build re-ran against the new code
    }

    [Fact]
    public void MarkSubtreeForRebuild_RecursesIntoChildren()
    {
        var a = new CountingStateless();
        var b = new CountingStateless();
        var col = new Column {
            Children = {
                a,
                b,
            },
        };
        Pump(col);
        Assert.False(a.NeedsBuild);
        Assert.False(b.NeedsBuild);

        HotReload.MarkSubtreeForRebuild(col);

        Assert.True(col.NeedsBuild);
        Assert.True(a.NeedsBuild);
        Assert.True(b.NeedsBuild);
        Assert.True(a.NeedsLayout);
        Assert.True(b.NeedsLayout);
    }

    [Fact]
    public void Request_SetsPending_AndTakeClearsItExactlyOnce()
    {
        HotReload.TryTakePending(out _); // drain any residue from prior tests
        Assert.False(HotReload.HasPendingReload);

        HotReload.Request([typeof(int)]);
        Assert.True(HotReload.HasPendingReload);

        Assert.True(HotReload.TryTakePending(out var types));
        Assert.Contains(expected: typeof(int), collection: types!);
        Assert.False(HotReload.HasPendingReload);
        Assert.False(HotReload.TryTakePending(out _)); // already taken
    }

    [Fact]
    public void Request_AccumulatesTypesAcrossDeltas()
    {
        HotReload.TryTakePending(out _);

        HotReload.Request([typeof(int)]);
        HotReload.Request([typeof(string)]);

        Assert.True(HotReload.TryTakePending(out var types));
        Assert.Contains(expected: typeof(int), collection: types!);
        Assert.Contains(expected: typeof(string), collection: types!);
    }

    [Fact]
    public void Request_RespectsEnabledFlag()
    {
        HotReload.TryTakePending(out _);
        try
        {
            HotReload.Enabled = false;
            HotReload.Request(null);
            Assert.False(HotReload.HasPendingReload);
        }
        finally
        {
            HotReload.Enabled = true;
        }
    }

    [Fact]
    public void RaiseReloaded_InvokesSubscribers()
    {
        Type[]? got = null;
        var handler = new Action<Type[]?>(t => got = t);
        HotReload.Reloaded += handler;
        try
        {
            HotReload.RaiseReloaded([typeof(string)]);
            Assert.NotNull(got);
            Assert.Contains(expected: typeof(string), collection: got!);
        }
        finally
        {
            HotReload.Reloaded -= handler;
        }
    }

    [Fact]
    public void Handler_IsRegistered_WithConventionMethod()
    {
        // The runtime resolves UpdateApplication by name + signature (static, single Type[] param); a
        // rename/typo would compile fine but silently disable hot reload. Guard the convention.
        object[] attrs = typeof(HotReload).Assembly
            .GetCustomAttributes(
                attributeType: typeof(MetadataUpdateHandlerAttribute),
                inherit: false
            );
        Assert.Single(attrs);
        var handler = ((MetadataUpdateHandlerAttribute)attrs[0]).HandlerType;

        var update = handler.GetMethod(
            name: "UpdateApplication",
            bindingAttr: BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        );
        Assert.NotNull(update);
        var ps = update!.GetParameters();
        Assert.Single(ps);
        Assert.Equal(expected: typeof(Type[]), actual: ps[0].ParameterType);
    }

    private sealed class CountingStateless : ComposedWidget
    {
        public int Builds;

        protected override Widget Build(BuildContext context)
        {
            Builds++;
            return new SizedBox(width: 10, height: 10);
        }
    }

    private sealed class CountingMounted : ComposedWidget
    {
        public int Builds;
        public int Mounts;

        protected override void OnMount() => Mounts++;

        protected override Widget Build(BuildContext context)
        {
            Builds++;
            return new SizedBox(width: 5, height: 5);
        }
    }
}
