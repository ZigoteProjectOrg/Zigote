using System.Globalization;
using Xunit;
using Zigote.Core;

namespace Zigote.Tests;

/// <summary>
///     Correctness + steady-state-zero-allocation coverage for the hot-path primitives in
///     <c>Zigote.Core/Types/</c> (<see cref="ScratchBuffer{T}" />, <see cref="ValueList{T}" />,
///     <see cref="KeyedText{TKey}" />, <see cref="ChangeGate{TKey}" />,
///     <see cref="Memo{TKey,TValue}" />).
/// </summary>
public class ZeroAllocPrimitivesTests
{
    private static readonly object BoxedSentinel = new();

    private readonly KeyedText<int> _keyed = new();

    private ChangeGate<float> _gate;
    // ---- ScratchBuffer<T> ----

    private ScratchBuffer<float> _scratch;

    [Fact]
    public void ScratchBuffer_GrowsAndReuses()
    {
        var buf = default(ScratchBuffer<float>);
        var a = buf.Get(4);
        Assert.Equal(expected: 4, actual: a.Length);
        Assert.True(buf.Capacity >= 4);

        int capAfterFirst = buf.Capacity;
        var b = buf.Get(3);
        Assert.Equal(expected: 3, actual: b.Length);
        Assert.Equal(expected: capAfterFirst, actual: buf.Capacity); // no shrink, no realloc

        var c = buf.Get(1000);
        Assert.Equal(expected: 1000, actual: c.Length);
        Assert.True(buf.Capacity >= 1000);
    }

    [Fact]
    public void ScratchBuffer_GetPreserving_KeepsContentsAcrossGrowth()
    {
        var buf = default(ScratchBuffer<int>);
        var a = buf.GetPreserving(3);
        a[0] = 1;
        a[1] = 2;
        a[2] = 3;

        var b = buf.GetPreserving(64); // forces growth
        Assert.Equal(expected: 1, actual: b[0]);
        Assert.Equal(expected: 2, actual: b[1]);
        Assert.Equal(expected: 3, actual: b[2]);
    }

    [Fact]
    public void ScratchBuffer_SteadyState_AllocatesZero()
    {
        _scratch.Get(256); // pre-grow
        AllocGuard.AssertZeroAlloc(() =>
            {
                var span = _scratch.Get(200);
                for (int i = 0; i < span.Length; i++) span[i] = i;
            }
        );
    }

    // ---- ValueList<T> ----

    [Fact]
    public void ValueList_StackSeed_AddIndexClear()
    {
        using var list = new ValueList<int>(stackalloc int[4]);
        list.Add(10);
        list.Add(20);
        Assert.Equal(expected: 2, actual: list.Count);
        Assert.Equal(expected: 10, actual: list[0]);
        Assert.Equal(expected: 20, actual: list[1]);
        Assert.True(list.Span.SequenceEqual([10, 20]));

        list.Clear();
        Assert.Equal(expected: 0, actual: list.Count);
    }

    [Fact]
    public void ValueList_SpillsPastStackBuffer_PreservingItems()
    {
        using var list = new ValueList<int>(stackalloc int[2]);
        for (int i = 0; i < 100; i++) list.Add(i);
        Assert.Equal(expected: 100, actual: list.Count);
        for (int i = 0; i < 100; i++) Assert.Equal(expected: i, actual: list[i]);
    }

    [Fact]
    public void ValueList_ReferenceType_PoolBacked()
    {
        using var list = new ValueList<string>();
        list.Add("a");
        list.Add("b");
        Assert.Equal(expected: 2, actual: list.Count);
        Assert.Equal(expected: "a", actual: list[0]);
        Assert.Equal(expected: "b", actual: list[1]);
    }

    [Fact]
    public void ValueList_IndexOutOfRange_Throws()
    {
        bool thrown = false;
        var list = new ValueList<int>(stackalloc int[2]);
        list.Add(1);
        try
        {
            _ = list[1];
        }
        catch (ArgumentOutOfRangeException)
        {
            thrown = true;
        }

        list.Dispose();
        Assert.True(thrown);
    }

    [Fact]
    public void ValueList_SteadyState_AllocatesZero()
    {
        // Stack-seeded (unmanaged) and pool-backed (reference type) variants; the stackalloc lives
        // inside the callee so it is released per call (never stackalloc in a loop — CA2014).
        AllocGuard.AssertZeroAlloc(static () =>
            {
                StackSeededFrame();
                PoolBackedFrame();
            }
        );
    }

    private static void StackSeededFrame()
    {
        using var list = new ValueList<int>(stackalloc int[8]);
        for (int i = 0; i < 32; i++) list.Add(i); // spills to the (warm) pool
        int sum = 0;
        var span = list.Span;
        for (int i = 0; i < span.Length; i++) sum += span[i];
        if (sum != 496) throw new InvalidOperationException("bad sum");
    }

    private static void PoolBackedFrame()
    {
        using var list = new ValueList<object>();
        for (int i = 0; i < 20; i++) list.Add(BoxedSentinel);
        if (list.Count != 20) throw new InvalidOperationException("bad count");
    }

    // ---- KeyedText<TKey> ----

    [Fact]
    public void KeyedText_SkipsFormatting_WhileKeyUnchanged()
    {
        var kt = new KeyedText<int>();
        int calls = 0;

        string Part()
        {
            calls++;
            return "part";
        }

        string first = kt.Update(key: 1, text: $"v={Part()}");
        string second = kt.Update(
            key: 1,
            text: $"v={Part()}"
        ); // key unchanged: Part() must NOT run
        Assert.Equal(expected: "v=part", actual: first);
        Assert.Same(expected: first, actual: second);
        Assert.Equal(expected: 1, actual: calls);

        string third = kt.Update(key: 2, text: $"v={Part()} #{2}");
        Assert.Equal(expected: "v=part #2", actual: third);
        Assert.Equal(expected: 2, actual: calls);
    }

    [Fact]
    public void KeyedText_FuncOverload_FormatsOncePerKey()
    {
        var kt = new KeyedText<StringComparison>();
        string a = kt.Update(key: StringComparison.Ordinal, format: static k => k.ToString());
        string b = kt.Update(key: StringComparison.Ordinal, format: static k => k.ToString());
        Assert.Equal(expected: "Ordinal", actual: a);
        Assert.Same(expected: a, actual: b);

        string c = kt.Update(
            key: StringComparison.OrdinalIgnoreCase,
            format: static k => k.ToString()
        );
        Assert.Equal(expected: "OrdinalIgnoreCase", actual: c);
    }

    [Fact]
    public void KeyedText_Invalidate_ForcesReformat()
    {
        var kt = new KeyedText<int>();
        kt.Update(key: 1, text: $"n={1}");
        kt.Invalidate();
        int calls = 0;

        string Probe()
        {
            calls++;
            return "x";
        }

        kt.Update(key: 1, text: $"n={Probe()}");
        Assert.Equal(expected: 1, actual: calls);
        Assert.Equal(expected: "n=x", actual: kt.Value);
    }

    [Fact]
    public void KeyedText_SteadyState_AllocatesZero()
    {
        int value = 42;
        _keyed.Update(key: value, text: $"{value} fps · {value * 0.25f:F1} ms");
        AllocGuard.AssertZeroAlloc(() =>
            {
                _ = _keyed.Update(key: value, text: $"{value} fps · {value * 0.25f:F1} ms");
            }
        );
    }

    // ---- ChangeGate / Memo ----

    [Fact]
    public void ChangeGate_ReportsOnlyChanges()
    {
        var gate = default(ChangeGate<(string, float)>);
        Assert.True(gate.Changed(("a", 1f)));
        Assert.False(gate.Changed(("a", 1f)));
        Assert.True(gate.Changed(("a", 2f)));
        Assert.True(gate.Changed(("b", 2f)));
        Assert.False(gate.Changed(("b", 2f)));

        gate.Invalidate();
        Assert.True(gate.Changed(("b", 2f)));
    }

    [Fact]
    public void Memo_RecomputesOnlyOnKeyChange()
    {
        var memo = default(Memo<int, string>);
        int computes = 0;

        string a = memo.Get(
            key: 1,
            compute: k =>
            {
                computes++;
                return $"v{k}";
            }
        );
        string b = memo.Get(
            key: 1,
            compute: k =>
            {
                computes++;
                return $"v{k}";
            }
        );
        Assert.Equal(expected: "v1", actual: a);
        Assert.Same(expected: a, actual: b);
        Assert.Equal(expected: 1, actual: computes);

        string c = memo.Get(
            key: 2,
            compute: k =>
            {
                computes++;
                return $"v{k}";
            }
        );
        Assert.Equal(expected: "v2", actual: c);
        Assert.Equal(expected: 2, actual: computes);
    }

    [Fact]
    public void Memo_StateOverload_PassesContextWithoutCapture()
    {
        var memo = default(Memo<float, string>);
        string prefix = "w=";
        string a = memo.Get(
            key: 3.5f,
            state: prefix,
            compute: static (p, k) => p + k.ToString(
                format: "F1",
                provider: CultureInfo.InvariantCulture
            )
        );
        Assert.Equal(expected: "w=3.5", actual: a);
    }

    [Fact]
    public void ChangeGate_SteadyState_AllocatesZero()
    {
        AllocGuard.AssertZeroAlloc(() =>
            {
                if (_gate.Changed(800f)) return; // latches on the first (warmup) call only
            }
        );
    }
}
