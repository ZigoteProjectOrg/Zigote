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
        Assert.Equal(expected: "60 fps", actual: t.Update($"{60.4f:F0} fps"));
        Assert.Equal(expected: "16.67 ms", actual: t.Update($"{16.666:F2} ms"));
        Assert.Equal(
            expected: "a=true b=x c=1.5K",
            actual: t.Update($"a={true} b={'x'} c={1.5:0.#}K")
        );
    }

    [Fact]
    public void Update_SameText_ReturnsSameInstance()
    {
        var t = new CachedText();
        string a = t.Update($"{60f:F0} fps");
        string b = t.Update($"{60.2f:F0} fps"); // renders identically
        Assert.Same(expected: a, actual: b);
    }

    [Fact]
    public void Update_ChangedText_ReturnsNewValue()
    {
        var t = new CachedText();
        string a = t.Update($"{60f:F0} fps");
        string b = t.Update($"{30f:F0} fps");
        Assert.NotSame(expected: a, actual: b);
        Assert.Equal(expected: "30 fps", actual: b);
    }

    [Fact]
    public void Update_GrowsPastInitialCapacity_KeepsPrefix()
    {
        var t = new CachedText(16);
        string longTail = new(c: 'y', count: 300);
        string s = t.Update($"prefix-{longTail}-{123456:N0}");
        Assert.StartsWith(expectedStartString: "prefix-yyy", actualString: s);
        Assert.EndsWith(expectedEndString: "-123,456", actualString: s);
        Assert.Equal(expected: 7 + 300 + 1 + 7, actual: s.Length);
    }

    [Fact]
    public void Update_SpanOverload_CachesByContent()
    {
        var t = new CachedText();
        string a = t.Update("hello".AsSpan());
        string b = t.Update("hello".AsSpan());
        Assert.Same(expected: a, actual: b);
    }

    [Fact]
    public void Update_SteadyState_AllocatesZero()
    {
        var t = new CachedText();
        float fps = 60.2f;
        double ms = 16.61;

        // Warm up: first render allocates the string + JIT.
        for (int i = 0; i < 100; i++) _ = t.Update($"{fps:F0} fps · {ms:F1} ms");

        const int frames = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _ = t.Update($"{fps:F0} fps · {ms:F1} ms");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            condition: allocated == 0,
            userMessage:
            $"CachedText.Update allocated {allocated} B over {frames} unchanged renders; expected 0."
        );
    }
}
