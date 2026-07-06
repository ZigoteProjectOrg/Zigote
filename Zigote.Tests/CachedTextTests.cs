using Xunit;
using Zigote.Core;

namespace Zigote.Tests;

/// <summary>
///     <see cref="CachedText" /> is the zero-allocation primitive for per-frame UI readouts: it must
///     render interpolations correctly, return the same instance while the text is unchanged (so
///     steady readouts allocate nothing and Text setters early-out on reference equality), and only
///     allocate when the rendered text actually changes.
/// </summary>
public class CachedTextTests
{
    [Fact]
    public void Update_RendersInterpolation_InvariantCulture()
    {
        var t = new CachedText();
        Assert.Equal("60 fps", t.Update($"{60.4f:F0} fps"));
        Assert.Equal("16.67 ms", t.Update($"{16.666:F2} ms"));
        Assert.Equal("a=true b=x c=1.5K", t.Update($"a={true} b={'x'} c={1.5:0.#}K"));
    }

    [Fact]
    public void Update_SameText_ReturnsSameInstance()
    {
        var t = new CachedText();
        var a = t.Update($"{60f:F0} fps");
        var b = t.Update($"{60.2f:F0} fps"); // renders identically
        Assert.Same(a, b);
    }

    [Fact]
    public void Update_ChangedText_ReturnsNewValue()
    {
        var t = new CachedText();
        var a = t.Update($"{60f:F0} fps");
        var b = t.Update($"{30f:F0} fps");
        Assert.NotSame(a, b);
        Assert.Equal("30 fps", b);
    }

    [Fact]
    public void Update_GrowsPastInitialCapacity_KeepsPrefix()
    {
        var t = new CachedText(16);
        var longTail = new string('y', 300);
        var s = t.Update($"prefix-{longTail}-{123456:N0}");
        Assert.StartsWith("prefix-yyy", s);
        Assert.EndsWith("-123,456", s);
        Assert.Equal(7 + 300 + 1 + 7, s.Length);
    }

    [Fact]
    public void Update_SpanOverload_CachesByContent()
    {
        var t = new CachedText();
        var a = t.Update("hello".AsSpan());
        var b = t.Update("hello".AsSpan());
        Assert.Same(a, b);
    }

    [Fact]
    public void Update_SteadyState_AllocatesZero()
    {
        var t = new CachedText();
        var fps = 60.2f;
        var ms = 16.61;

        // Warm up: first render allocates the string + JIT.
        for (var i = 0; i < 100; i++) _ = t.Update($"{fps:F0} fps · {ms:F1} ms");

        const int frames = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < frames; i++) _ = t.Update($"{fps:F0} fps · {ms:F1} ms");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated == 0,
            $"CachedText.Update allocated {allocated} B over {frames} unchanged renders; expected 0."
        );
    }
}