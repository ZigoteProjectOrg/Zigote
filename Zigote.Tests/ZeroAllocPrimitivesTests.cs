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
        Assert.Equal(4, a.Length);
        Assert.True(buf.Capacity >= 4);

        var capAfterFirst = buf.Capacity;
        var b = buf.Get(3);
        Assert.Equal(3, b.Length);
        Assert.Equal(capAfterFirst, buf.Capacity); // no shrink, no realloc

        var c = buf.Get(1000);
        Assert.Equal(1000, c.Length);
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
        Assert.Equal(1, b[0]);
        Assert.Equal(2, b[1]);
        Assert.Equal(3, b[2]);
    }

    [Fact]
    public void ScratchBuffer_SteadyState_AllocatesZero()
    {
        _scratch.Get(256); // pre-grow
        AllocGuard.AssertZeroAlloc(() =>
            {
                var span = _scratch.Get(200);
                for (var i = 0; i < span.Length; i++) span[i] = i;
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
        Assert.Equal(2, list.Count);
        Assert.Equal(10, list[0]);
        Assert.Equal(20, list[1]);
        Assert.True(list.Span.SequenceEqual([10, 20]));

        list.Clear();
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void ValueList_SpillsPastStackBuffer_PreservingItems()
    {
        using var list = new ValueList<int>(stackalloc int[2]);
        for (var i = 0; i < 100; i++) list.Add(i);
        Assert.Equal(100, list.Count);
        for (var i = 0; i < 100; i++) Assert.Equal(i, list[i]);
    }

    [Fact]
    public void ValueList_ReferenceType_PoolBacked()
    {
        using var list = new ValueList<string>();
        list.Add("a");
        list.Add("b");
        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0]);
        Assert.Equal("b", list[1]);
    }

    [Fact]
    public void ValueList_IndexOutOfRange_Throws()
    {
        var thrown = false;
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
        for (var i = 0; i < 32; i++) list.Add(i); // spills to the (warm) pool
        var sum = 0;
        var span = list.Span;
        for (var i = 0; i < span.Length; i++) sum += span[i];
        if (sum != 496) throw new InvalidOperationException("bad sum");
    }

    private static void PoolBackedFrame()
    {
        using var list = new ValueList<object>();
        for (var i = 0; i < 20; i++) list.Add(BoxedSentinel);
        if (list.Count != 20) throw new InvalidOperationException("bad count");
    }

    // ---- KeyedText<TKey> ----

    [Fact]
    public void KeyedText_SkipsFormatting_WhileKeyUnchanged()
    {
        var kt = new KeyedText<int>();
        var calls = 0;

        string Part()
        {
            calls++;
            return "part";
        }

        var first = kt.Update(1, $"v={Part()}");
        var second = kt.Update(1, $"v={Part()}"); // key unchanged: Part() must NOT run
        Assert.Equal("v=part", first);
        Assert.Same(first, second);
        Assert.Equal(1, calls);

        var third = kt.Update(2, $"v={Part()} #{2}");
        Assert.Equal("v=part #2", third);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void KeyedText_FuncOverload_FormatsOncePerKey()
    {
        var kt = new KeyedText<StringComparison>();
        var a = kt.Update(StringComparison.Ordinal, static k => k.ToString());
        var b = kt.Update(StringComparison.Ordinal, static k => k.ToString());
        Assert.Equal("Ordinal", a);
        Assert.Same(a, b);

        var c = kt.Update(StringComparison.OrdinalIgnoreCase, static k => k.ToString());
        Assert.Equal("OrdinalIgnoreCase", c);
    }

    [Fact]
    public void KeyedText_Invalidate_ForcesReformat()
    {
        var kt = new KeyedText<int>();
        kt.Update(1, $"n={1}");
        kt.Invalidate();
        var calls = 0;

        string Probe()
        {
            calls++;
            return "x";
        }

        kt.Update(1, $"n={Probe()}");
        Assert.Equal(1, calls);
        Assert.Equal("n=x", kt.Value);
    }

    [Fact]
    public void KeyedText_SteadyState_AllocatesZero()
    {
        var value = 42;
        _keyed.Update(value, $"{value} fps · {value * 0.25f:F1} ms");
        AllocGuard.AssertZeroAlloc(() =>
            {
                _ = _keyed.Update(value, $"{value} fps · {value * 0.25f:F1} ms");
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
        var computes = 0;

        var a = memo.Get(
            1,
            k =>
            {
                computes++;
                return $"v{k}";
            }
        );
        var b = memo.Get(
            1,
            k =>
            {
                computes++;
                return $"v{k}";
            }
        );
        Assert.Equal("v1", a);
        Assert.Same(a, b);
        Assert.Equal(1, computes);

        var c = memo.Get(
            2,
            k =>
            {
                computes++;
                return $"v{k}";
            }
        );
        Assert.Equal("v2", c);
        Assert.Equal(2, computes);
    }

    [Fact]
    public void Memo_StateOverload_PassesContextWithoutCapture()
    {
        var memo = default(Memo<float, string>);
        var prefix = "w=";
        var a = memo.Get(
            3.5f,
            prefix,
            static (p, k) => p + k.ToString("F1", CultureInfo.InvariantCulture)
        );
        Assert.Equal("w=3.5", a);
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
