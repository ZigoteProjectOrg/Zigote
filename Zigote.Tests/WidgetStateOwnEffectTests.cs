using Xunit;
using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     <see cref="WidgetState" />.OwnEffect ties an <see cref="Effect" />'s lifetime to the state:
///     it tracks signals like a bare Effect but is disposed with the state. Detaching the owning
///     <see cref="StatefulWidget" /> must stop the effect — signals hold their observers strongly,
///     so an unowned effect would keep firing against the detached subtree forever.
/// </summary>
[Collection("Reactive-serial")] // signals/effects share process-static reactive state
public class WidgetStateOwnEffectTests
{
    [Fact]
    public void OwnEffect_RunsImmediately_AndTracksSignals()
    {
        var s = new Signal<int>(1);
        var w = new CounterWidget(s);
        w.Measure(Constraints.Tight(100, 100)); // initializes the state (InitState → OwnEffect)

        var state = (CounterState)w.InternalState!;
        Assert.Equal(1, state.Seen);
        Assert.Equal(1, state.Runs);

        s.Value = 2;
        Assert.Equal(2, state.Seen);
        Assert.Equal(2, state.Runs);
    }

    [Fact]
    public void OwnEffect_IsDisposed_WhenTheWidgetDetaches()
    {
        var s = new Signal<int>(1);
        var w = new CounterWidget(s);
        w.Measure(Constraints.Tight(100, 100));
        var state = (CounterState)w.InternalState!;

        w.Detach(); // → DisposeState → WidgetState.Dispose drains owned effects

        var runsAtDetach = state.Runs;
        s.Value = 3;
        Assert.Equal(runsAtDetach, state.Runs); // disposed → no more runs
        Assert.Equal(1, state.Cleanups); // the Func<Action> overload ran its final cleanup
    }

    private sealed class CounterWidget(Signal<int> source) : StatefulWidget
    {
        public Signal<int> Source { get; } = source;

        protected override WidgetState CreateState()
        {
            return new CounterState();
        }
    }

    private sealed class CounterState : WidgetState<CounterWidget>
    {
        public int Cleanups;
        public int Runs;
        public int Seen;

        public override void InitState()
        {
            OwnEffect(() =>
                {
                    Runs++;
                    Seen = Widget.Source.Value;
                    return () => Cleanups++;
                }
            );
        }

        public override Widget Build(BuildContext context)
        {
            return new SizedBox(10, 10);
        }
    }
}