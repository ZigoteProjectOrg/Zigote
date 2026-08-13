using R3;
using Zigote.Core.State;
using Zigote.Preferences;

namespace Zigote.Reactive;

/// <summary>
///     The one bridge between an app's two reactive primitives, so neither layer has to know the
///     other's. The UI is built on Zigote <see cref="Signal{T}" /> — <c>Watch</c> subscribes to it,
///     and a widget rebuild is a signal read. Data and domain layers are built on R3
///     <see cref="Observable{T}" />, which is where the operators live: debounce, latest-wins, merge.
///     <para>
///         Cross the boundary once at each end — a bloc turns repository streams into events on the
///         way in and publishes a <see cref="Signal{T}" /> on the way out — so nothing in between
///         has to hold both.
///     </para>
/// </summary>
public static class SignalStreams
{
    /// <summary>
    ///     A signal as a stream: the current value first, then every change.
    ///     <para>
    ///         <see cref="Signal{T}.Subscribe" /> already has exactly those semantics, so this is a
    ///         wrapper rather than a replay buffer — the signal stays the single source of truth.
    ///         Values arrive on whichever thread wrote the signal; add an <c>ObserveOn</c> after this
    ///         if the consumer cares which one.
    ///     </para>
    /// </summary>
    public static Observable<T> AsStream<T>(this Signal<T> signal)
    {
        return Stream<T>(signal.Subscribe);
    }

    /// <inheritdoc cref="AsStream{T}(Signal{T})" />
    /// <remarks>
    ///     A persisted signal streams the same way — <see cref="Preference{T}" /> has the same
    ///     subscribe-and-replay contract as <see cref="Signal{T}" />, it just also survives a
    ///     restart. There is no shared interface declaring <c>Subscribe</c>, hence the overload.
    /// </remarks>
    public static Observable<T> AsStream<T>(this Preference<T> preference)
    {
        return Stream<T>(preference.Subscribe);
    }

    /// <summary>
    ///     A signal as a stream of changes only, skipping the value it already holds. For a listener
    ///     that wants edges rather than state — "the user picked a different place", not "this is
    ///     the place".
    /// </summary>
    public static Observable<T> AsChangeStream<T>(this Signal<T> signal)
    {
        return signal.AsStream().Skip(1);
    }

    /// <summary>
    ///     A stream as a signal, seeded with <paramref name="initial" /> until the first value
    ///     arrives. The returned handle owns the subscription: dispose it and the signal stops
    ///     tracking.
    ///     <para>
    ///         Writes go through <see cref="Reactive.Sync" /> because a stream may emit from any
    ///         thread and every graph mutation belongs under the graph lock.
    ///     </para>
    /// </summary>
    public static SignalSubscription<T> ToSignal<T>(this Observable<T> stream, T initial)
    {
        var signal = new Signal<T>(initial);
        var subscription =
            stream.Subscribe(value => Core.State.Reactive.Sync(() => signal.Value = value));
        return new SignalSubscription<T>(signal, subscription);
    }

    private static Observable<T> Stream<T>(Func<Action<T>, IDisposable> subscribe)
    {
        return Observable.Create<T, Func<Action<T>, IDisposable>>(
            subscribe,
            static (observer, source) => source(observer.OnNext)
        );
    }

    /// <summary>A signal fed by a stream, together with the subscription keeping it fed.</summary>
    public sealed class SignalSubscription<T>(Signal<T> signal, IDisposable subscription)
        : IDisposable
    {
        public Signal<T> Signal { get; } = signal;

        public T Value => Signal.Value;

        public void Dispose()
        {
            subscription.Dispose();
        }

        public static implicit operator Signal<T>(SignalSubscription<T> handle)
        {
            return handle.Signal;
        }
    }
}
