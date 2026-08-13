namespace Zigote.Core;

/// <summary>
///     Change detection for the "recompute only when an input changed" hot-path pattern (the
///     <c>Slider._measureW</c> / wrap-cache idiom) as a primitive. Embed as a (non-readonly) field;
///     compose multi-part keys as tuples. Keys compare via <see cref="EqualityComparer{T}.Default" />
///     (devirtualized + unboxed for value types, so enums/tuples are allocation-free):
///     <code>
///     private ChangeGate&lt;(string, float)&gt; _wrapKey;
///     if (_wrapKey.Changed((Text, maxWidth))) Rewrap(maxWidth);   // cold branch may allocate
///     </code>
///     The gate answers "did the key change since last time?" — the caller owns the cached result.
///     For caching the derived value too, use <see cref="Memo{TKey,TValue}" />.
/// </summary>
public struct ChangeGate<TKey>
{
    private TKey _key;
    private bool _has;

    /// <summary>True (and latches the key) when <paramref name="key" /> differs from the last call.</summary>
    public bool Changed(TKey key)
    {
        if (_has && EqualityComparer<TKey>.Default.Equals(_key, key)) return false;
        _has = true;
        _key = key;
        return true;
    }

    /// <summary>Forces the next <see cref="Changed" /> to report true.</summary>
    public void Invalidate()
    {
        _has = false;
    }
}

/// <summary>
///     A single-entry key→value cache: recomputes only when the key changed, so the compute
///     (which may allocate) runs on the cold change path while steady-state reads are free.
///     Pass a <b>static</b> lambda — a capturing lambda allocates its closure at method entry on
///     every call, defeating the point; thread context through the <c>TState</c> overload instead:
///     <code>
///     private Memo&lt;float, GlyphRun[]&gt; _runs;                            // field — NOT readonly
///     var runs = _runs.Get(width, this, static (self, w) =&gt; self.BuildRuns(w));
///     </code>
/// </summary>
public struct Memo<TKey, TValue>
{
    private TKey _key;
    private bool _has;

    /// <summary>The cached value (default before the first Get).</summary>
    public TValue? Value { get; private set; }

    public TValue Get(TKey key, Func<TKey, TValue> compute)
    {
        if (_has && EqualityComparer<TKey>.Default.Equals(_key, key)) return Value!;
        Value = compute(key);
        _key = key;
        _has = true;
        return Value;
    }

    /// <summary>
    ///     Capture-free variant: pass <c>this</c> (or any context) as <paramref name="state" /> with
    ///     a static lambda.
    /// </summary>
    public TValue Get<TState>(TKey key, TState state, Func<TState, TKey, TValue> compute)
    {
        if (_has && EqualityComparer<TKey>.Default.Equals(_key, key)) return Value!;
        Value = compute(state, key);
        _key = key;
        _has = true;
        return Value;
    }

    /// <summary>Forces the next <see cref="Get" /> to recompute.</summary>
    public void Invalidate()
    {
        _has = false;
        Value = default;
    }
}
