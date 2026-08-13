using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     Model-based fuzzing of the dependency graph: build a random DAG of signals and computeds (including
///     conditional nodes, whose dependency set changes as branches flip), then hammer it with random
///     writes, batches, reads, subscriptions and disposals — checking every read against a dumb reference
///     evaluator that just recomputes from the current signal values.
///     <para>
///         This is the cover the hand-written tests can't give: the positional dependency reconcile, the
///         tri-colour push/pull, the watched/unwatched transitions and the observer edges only go wrong on
///         shapes and orderings nobody thought to write down. Seeds are fixed, so a failure reproduces.
///     </para>
/// </summary>
[Collection("Reactive-serial")]
public class ReactiveModelFuzzTests
{
    private const int SignalCount = 5;
    private const int ComputedCount = 12;

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1234)]
    [InlineData(20260802)]
    public void A_random_graph_stays_consistent_with_a_reference_evaluator(int seed)
    {
        var rnd = new Random(seed);
        var graph = new Graph(rnd);
        try
        {
            for (var step = 0; step < 500; step++)
            {
                switch (rnd.Next(8))
                {
                    case 0:
                    case 1:
                    case 2:
                        graph.Write(rnd);
                        break;
                    case 3:
                        graph.BatchWrite(rnd);
                        break;
                    case 4:
                        graph.AssertRead(rnd);
                        break;
                    case 5:
                        graph.AddWatcher(rnd);
                        break;
                    case 6:
                        graph.DropWatcher(rnd);
                        break;
                    case 7:
                        graph.AssertAllReads();
                        break;
                }

                graph.AssertEffectsSettled(step);
            }

            graph.AssertAllReads();
        }
        finally
        {
            graph.Dispose();
        }
    }

    // ── the graph under test, mirrored by a plain recursive evaluator ────────────────────────────────
    private sealed class Graph : IDisposable
    {
        private readonly List<(int node, int seen)> _effectSlots = [];
        private readonly List<Effect> _effects = [];
        private readonly int[] _model = new int[SignalCount];
        private readonly Node[] _nodes = new Node[SignalCount + ComputedCount];

        private readonly IReadableSignal<int>[] _live =
            new IReadableSignal<int>[SignalCount + ComputedCount];

        private readonly Signal<int>[] _signals = new Signal<int>[SignalCount];
        private readonly List<IDisposable> _watchers = [];
        private readonly int[] _lastSeen = new int[4];

        public Graph(Random rnd)
        {
            for (var i = 0; i < SignalCount; i++)
            {
                _model[i] = rnd.Next(0, 10);
                _signals[i] = new Signal<int>(_model[i]);
                _nodes[i] = new Node(
                    NodeKind.Signal,
                    i,
                    0,
                    0,
                    0
                );
                _live[i] = _signals[i];
            }

            for (var i = SignalCount; i < _nodes.Length; i++)
            {
                var kind = (NodeKind)rnd.Next(1, 4);
                var a = rnd.Next(i);
                var b = rnd.Next(i);
                var c = rnd.Next(i);
                _nodes[i] = new Node(
                    kind,
                    a,
                    b,
                    c,
                    rnd.Next(1, 4)
                );
                var index = i; // capture
                _live[i] = Computed.From(() => EvalLive(index));
            }

            // A handful of effects, each watching one computed: they must always end a cascade holding the
            // reference value (glitch-free, settled, exactly once per change).
            for (var k = 0; k < _lastSeen.Length; k++)
            {
                var node = SignalCount + rnd.Next(ComputedCount);
                var slot = k;
                _effectSlots.Add((node, slot));
                _effects.Add(new Effect(() => _lastSeen[slot] = _live[node].Value));
            }
        }

        public void Dispose()
        {
            foreach (var w in _watchers) w.Dispose();
            foreach (var e in _effects) e.Dispose();
            for (var i = SignalCount; i < _live.Length; i++) (_live[i] as IDisposable)?.Dispose();
        }

        public void Write(Random rnd)
        {
            var i = rnd.Next(SignalCount);
            var v = rnd.Next(0, 20);
            _model[i] = v;
            _signals[i].Value = v;
        }

        public void BatchWrite(Random rnd)
        {
            var writes = new List<(int index, int value)>();
            var count = rnd.Next(2, 5);
            for (var k = 0; k < count; k++) writes.Add((rnd.Next(SignalCount), rnd.Next(0, 20)));

            Reactive.Batch(() =>
                {
                    foreach (var (index, value) in writes) _signals[index].Value = value;
                }
            );
            foreach (var (index, value) in writes) _model[index] = value;
        }

        public void AssertRead(Random rnd)
        {
            var i = SignalCount + rnd.Next(ComputedCount);
            Assert.Equal(EvalModel(i), _live[i].Value);
            Assert.Equal(EvalModel(i), ((Computed<int>)_live[i]).Peek());
        }

        public void AssertAllReads()
        {
            for (var i = 0; i < _live.Length; i++) Assert.Equal(EvalModel(i), _live[i].Value);
        }

        /// <summary>Observing makes a computed watched — the push path and the edge bookkeeping.</summary>
        public void AddWatcher(Random rnd)
        {
            if (_watchers.Count > 6) return;
            var i = SignalCount + rnd.Next(ComputedCount);
            _watchers.Add(_live[i].Observe(() => { }));
        }

        /// <summary>Dropping the last observer detaches the computed — the leak-free path back to lazy.</summary>
        public void DropWatcher(Random rnd)
        {
            if (_watchers.Count == 0) return;
            var k = rnd.Next(_watchers.Count);
            _watchers[k].Dispose();
            _watchers.RemoveAt(k);
        }

        public void AssertEffectsSettled(int step)
        {
            foreach (var (node, slot) in _effectSlots)
                Assert.True(
                    _lastSeen[slot] == EvalModel(node),
                    $"step {step}: effect on node {node} settled at {_lastSeen[slot]}, reference says {EvalModel(node)}"
                );
        }

        // Reads the REAL graph (this is what a computed body runs).
        private int EvalLive(int i)
        {
            var n = _nodes[i];
            return n.Kind switch {
                NodeKind.Signal => _signals[n.A].Value,
                NodeKind.Sum => _live[n.A].Value + _live[n.B].Value,
                NodeKind.Cond => _live[n.C].Value % 2 == 0 ? _live[n.A].Value : _live[n.B].Value,
                _ => _live[n.A].Value * n.K,
            };
        }

        // The reference: recompute everything from the model's signal values, no caching, no tracking.
        private int EvalModel(int i)
        {
            var n = _nodes[i];
            return n.Kind switch {
                NodeKind.Signal => _model[n.A],
                NodeKind.Sum => EvalModel(n.A) + EvalModel(n.B),
                NodeKind.Cond => EvalModel(n.C) % 2 == 0 ? EvalModel(n.A) : EvalModel(n.B),
                _ => EvalModel(n.A) * n.K,
            };
        }

        private enum NodeKind
        {
            Signal = 0,
            Sum = 1,
            Cond = 2,
            Scale = 3,
        }

        private readonly record struct Node(NodeKind Kind, int A, int B, int C, int K);
    }
}
