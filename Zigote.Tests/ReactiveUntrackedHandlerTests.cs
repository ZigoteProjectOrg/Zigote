using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     User-facing event handlers (<c>Changed</c>/<c>Invalidated</c>, <c>Observe</c>/<c>Subscribe</c>
///     callbacks) fire while a reaction may be mid-run under dependency tracking. Their reads must NOT
///     be recorded as dependencies of that reaction — a handler that inspects other signals would
///     otherwise silently subscribe the running computed/effect to them (phantom dependencies).
/// </summary>
[Collection("Reactive-serial")]
public class ReactiveUntrackedHandlerTests
{
    [Fact]
    public void A_Changed_handler_reading_a_signal_does_not_subscribe_the_computed_to_it()
    {
        var a = new Signal<int>(1);
        var other = new Signal<int>(100);
        int runs = 0;
        using var doubled = Computed.From(() =>
            {
                runs++;
                return a.Value * 2;
            }
        );
        doubled.Changed += _ => { _ = other.Value; }; // fires inside the tracked recompute
        using var live = doubled.Observe(() => { });

        a.Value = 2;
        Assert.Equal(expected: 2, actual: runs);

        other.Value = 101; // must not recompute doubled — that would be a phantom dependency
        Assert.Equal(expected: 2, actual: runs);

        a.Value = 3; // the real dependency still reacts
        Assert.Equal(expected: 3, actual: runs);
        Assert.Equal(expected: 6, actual: doubled.Value);
    }

    [Fact]
    public void A_signal_Changed_handler_reading_a_signal_does_not_subscribe_the_running_effect()
    {
        var trigger = new Signal<int>(0);
        var target = new Signal<int>(0);
        var other = new Signal<int>(0);
        target.Changed += _ => { _ = other.Value; };

        int runs = 0;
        using var eff = new Effect(() =>
            {
                runs++;
                target.Value =
                    trigger.Value; // the write fires Changed inside the effect's tracked body
            }
        );
        Assert.Equal(expected: 1, actual: runs);

        trigger.Value = 1;
        Assert.Equal(expected: 2, actual: runs);

        other.Value = 5; // the handler's read must not have subscribed the effect
        Assert.Equal(expected: 2, actual: runs);
    }

    [Fact]
    public void An_Observe_callback_reading_a_signal_does_not_extend_the_subscription()
    {
        var s = new Signal<int>(0);
        var other = new Signal<int>(0);
        int fires = 0;
        using var sub = s.Observe(() =>
            {
                fires++;
                _ = other.Value;
            }
        );

        s.Value = 1;
        Assert.Equal(expected: 1, actual: fires);

        other.Value = 7; // the callback's read must not have become a dependency of the observer
        Assert.Equal(expected: 1, actual: fires);

        s.Value = 2;
        Assert.Equal(expected: 2, actual: fires);
    }
}
