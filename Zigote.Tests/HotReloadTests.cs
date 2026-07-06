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
///     The hot-reload contract: marking the tree reruns <c>Build()</c> while
///     preserving
///     widget instances and their <see cref="WidgetState" />. Also guards the off-thread pending-flag
///     plumbing the metadata-update handler drives.
/// </summary>
public class HotReloadTests
{
    private static void Pump(Widget w)
    {
        w.Measure(Constraints.Loose(200, 200));
        w.Layout(Offset.Zero);
    }

    [Fact]
    public void MarkSubtreeForRebuild_RerunsStatelessBuild()
    {
        var w = new CountingStateless();
        Pump(w);
        Assert.Equal(1, w.Builds);

        Pump(w); // cached — Build not re-run
        Assert.Equal(1, w.Builds);

        HotReload.MarkSubtreeForRebuild(w);
        Pump(w);
        Assert.Equal(2, w.Builds);
    }

    [Fact]
    public void MarkSubtreeForRebuild_PreservesStateButRerunsBuild()
    {
        var w = new CountingStateful();
        Pump(w);
        var state = (CountingStateful.S)w.InternalState!;
        Assert.Equal(1, state.Inits);
        Assert.Equal(1, state.Builds);

        HotReload.MarkSubtreeForRebuild(w);
        Pump(w);

        Assert.Same(state, w.InternalState); // State instance preserved across reload
        Assert.Equal(1, state.Inits); // InitState NOT re-run
        Assert.Equal(2, state.Builds); // Build re-ran against the new code
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
        Assert.Contains(typeof(int), types!);
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
        Assert.Contains(typeof(int), types!);
        Assert.Contains(typeof(string), types!);
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
            Assert.Contains(typeof(string), got!);
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
        var attrs = typeof(HotReload).Assembly
            .GetCustomAttributes(typeof(MetadataUpdateHandlerAttribute), false);
        Assert.Single(attrs);
        var handler = ((MetadataUpdateHandlerAttribute)attrs[0]).HandlerType;

        var update = handler.GetMethod(
            "UpdateApplication",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        );
        Assert.NotNull(update);
        var ps = update!.GetParameters();
        Assert.Single(ps);
        Assert.Equal(typeof(Type[]), ps[0].ParameterType);
    }

    private sealed class CountingStateless : StatelessWidget
    {
        public int Builds;

        protected override Widget Build(BuildContext context)
        {
            Builds++;
            return new SizedBox(10, 10);
        }
    }

    private sealed class CountingStateful : StatefulWidget
    {
        protected override WidgetState CreateState()
        {
            return new S();
        }

        internal sealed class S : WidgetState<CountingStateful>
        {
            public int Builds;
            public int Inits;

            public override void InitState()
            {
                Inits++;
            }

            public override Widget Build(BuildContext context)
            {
                Builds++;
                return new SizedBox(5, 5);
            }
        }
    }
}