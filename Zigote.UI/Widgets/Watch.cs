using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Core.State;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets;

/// <summary>
///     A reactive subtree: runs <paramref name="build" /> under dependency tracking and rebuilds ONLY
///     when a <see cref="Signal{T}" />/<see cref="Computed{T}" /> it read changes — the C# counterpart
///     of the F# <c>Ui.bind</c>, and the framework's bridge from a signal to the retained widget tree
///     (replacing the old <c>BlocBuilder</c>/<c>Cubit</c> pattern). Drop signals straight into any tree:
///     <code>
///   new Watch(() => new Label($"Count: {count.Value}"))
/// </code>
///     <para>
///         The builder is wrapped in a <see cref="Computed{T}" /> that auto-tracks whatever it reads, so
///         there is no dependency list. A change recomputes the subtree; on the UI thread it swaps in
///         place, and an off-thread change (a timer/async completion setting a signal) is marshalled —
///         the loop is woken via <c>RequestLayout</c> and the swap happens in <see cref="Measure" /> on
///         the UI thread. Like any rebuild in this retained framework, the new subtree replaces the old,
///         so hoist stateful children (or give them keys via a container) if they must survive a rebuild.
///     </para>
/// </summary>
public sealed class Watch : Widget
{
    private readonly Func<Widget> _build;
    private Computed<Widget>? _computed;
    private Widget? _child;
    private IDisposable? _subscription;
    private Size _size;
    private bool _started;
    private bool _detached;
    private bool _dirty;
    private int _uiThread;

    public Watch(Func<Widget> build)
    {
        _build = build;
    }

    private void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        _uiThread = Environment.CurrentManagedThreadId;
        _computed = Computed.From(_build); // eager first build (on the UI thread), auto-tracks its reads
        Apply();
        _subscription = ((ISignal)_computed).Observe(OnChanged);
    }

    // Swap in the freshly-built subtree (UI thread only).
    private void Apply()
    {
        var next = _computed!.Value;
        if (ReferenceEquals(next, _child)) return;

        _child?.Detach();
        _child = next;
        if (Owner != null) _child?.Attach(Owner, this);
        MarkNeedsLayout();
    }

    private void OnChanged()
    {
        if (_detached) return;

        if (Environment.CurrentManagedThreadId == _uiThread)
        {
            Apply();
        }
        else
        {
            // Off the UI thread: flag the swap and ask the App to mark this widget's ancestor chain for
            // layout on the UI thread next frame — the App-level layout flag alone lets cached ancestors
            // (StatelessWidget) skip re-measuring this subtree, so the swap in Measure would never run.
            _dirty = true;
            Owner?.InvalidateLayoutFromAnyThread(this);
        }
    }

    public override void Attach(App owner, Widget? parent)
    {
        _detached = false;
        EnsureStarted();
        base.Attach(owner, parent); // attaches the current _child via GetChildren
    }

    public override void Detach()
    {
        _detached = true;
        _subscription?.Dispose();
        _subscription = null;
        _computed?.Dispose();
        _computed = null;
        base.Detach(); // detaches _child via GetChildren
        _child = null;
        _started = false;
        _dirty = false;
    }

    public override Size Measure(Constraints constraints)
    {
        EnsureStarted();
        if (_dirty)
        {
            _dirty = false;
            Apply();
        }

        _size = _child?.Measure(constraints) ?? constraints.Constrain(Size.Zero);
        MeasuredSize = _size;
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
        _child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_child);
    }

    public override int DebugStateHash()
    {
        return _child?.DebugStateHash() ?? 0;
    }
}
