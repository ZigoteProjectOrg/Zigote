using System.Runtime.CompilerServices;

namespace Zigote.Core;

/// <summary>
///     A key-gated <see cref="CachedText" />: formatting is skipped entirely while the key is
///     unchanged — including evaluation of the interpolation's argument expressions — so per-frame
///     readouts built from expensive parts (enum <c>ToString</c>, multi-value formats, lookups) cost
///     one key compare in steady state. This is the "key-cache" pattern from the zero-allocation
///     rules as a primitive:
///     <code>
///     private readonly KeyedText&lt;int&gt; _fps = new();
///     label.Text = _fps.Update(fpsInt, $"{fpsInt} fps · {ms:F1} ms");   // formats only when fpsInt changed
/// 
///     private readonly KeyedText&lt;GcMode&gt; _mode = new();
///     label.Text = _mode.Update(mode, static m =&gt; m.ToString());        // static lambda: no closure
///     </code>
///     Compose multi-part keys as tuples. One instance per readout; not thread-safe.
/// </summary>
public sealed class KeyedText<TKey>
{
    // EqualityComparer<T>.Default is devirtualized (and unboxed) by the JIT for value types, so
    // enum/tuple keys compare allocation-free without an IEquatable constraint (enums lack one).
    private readonly CachedText _text;
    private bool _hasKey;
    private TKey _key = default!;

    public KeyedText(int capacity = 64) => _text = new CachedText(capacity);

    /// <summary>The last rendered text.</summary>
    public string Value => _text.Value;

    /// <summary>
    ///     Interpolated variant. When <paramref name="key" /> equals the previous key the handler is
    ///     disabled — the compiler skips every append (and its argument evaluation) — and the cached
    ///     string is returned.
    /// </summary>
    public string Update(
        TKey key,
        [InterpolatedStringHandlerArgument("", nameof(key))]
        ref Handler text)
    {
        if (!text.ShouldFormat) return _text.Value;
        _hasKey = true;
        _key = key;
        return _text.Update(ref text.Inner);
    }

    /// <summary>
    ///     Formatter variant for pre-existing formatters — pass a <b>static</b> lambda (a capturing
    ///     one allocates every call).
    /// </summary>
    public string Update(TKey key, Func<TKey, string> format)
    {
        if (_hasKey && EqualityComparer<TKey>.Default.Equals(x: _key, y: key)) return _text.Value;
        _hasKey = true;
        _key = key;
        return _text.Update(format(key));
    }

    /// <summary>
    ///     True (and latches the key) when <paramref name="key" /> changed — for manual flows; pair
    ///     with <see cref="Set" />.
    /// </summary>
    public bool Changed(TKey key)
    {
        if (_hasKey && EqualityComparer<TKey>.Default.Equals(x: _key, y: key)) return false;
        _hasKey = true;
        _key = key;
        return true;
    }

    /// <summary>Stores externally produced text (after a true <see cref="Changed" />).</summary>
    public string Set(ReadOnlySpan<char> text) => _text.Update(text);

    /// <summary>Forces the next Update to reformat.</summary>
    public void Invalidate() => _hasKey = false;

    /// <summary>
    ///     Conditional handler: reports <c>shouldAppend = false</c> to the compiler when the key is
    ///     unchanged, so no interpolation work happens at all. Delegates the actual writing to
    ///     <see cref="CachedText.Handler" /> (same scratch-buffer, invariant-culture, box-free rules).
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct Handler
    {
        internal CachedText.Handler Inner;
        internal readonly bool ShouldFormat;

        public Handler(int literalLength, int formattedCount, KeyedText<TKey> owner, TKey key,
            out bool shouldAppend)
        {
            shouldAppend = ShouldFormat =
                !(owner._hasKey && EqualityComparer<TKey>.Default.Equals(x: owner._key, y: key));
            Inner = new CachedText.Handler(
                literalLength: literalLength,
                formattedCount: formattedCount,
                owner: owner._text
            );
        }

        public void AppendLiteral(string s) => Inner.AppendLiteral(s);

        public void AppendFormatted(string? s) => Inner.AppendFormatted(s);

        public void AppendFormatted(ReadOnlySpan<char> s) => Inner.AppendFormatted(s);

        public void AppendFormatted(char c) => Inner.AppendFormatted(c);

        public void AppendFormatted(bool b) => Inner.AppendFormatted(b);

        public void AppendFormatted(int value, string? format = null) =>
            Inner.AppendFormatted(value: value, format: format);

        public void AppendFormatted(uint value, string? format = null) =>
            Inner.AppendFormatted(value: value, format: format);

        public void AppendFormatted(long value, string? format = null) =>
            Inner.AppendFormatted(value: value, format: format);

        public void AppendFormatted(ulong value, string? format = null) =>
            Inner.AppendFormatted(value: value, format: format);

        public void AppendFormatted(float value, string? format = null) =>
            Inner.AppendFormatted(value: value, format: format);

        public void AppendFormatted(double value, string? format = null) =>
            Inner.AppendFormatted(value: value, format: format);

        public void AppendFormatted(TimeSpan value, string? format = null) =>
            Inner.AppendFormatted(value: value, format: format);

        /// <summary>Fallback for exotic types — may box a struct; avoid on hot paths.</summary>
        public void AppendFormatted<T>(T value, string? format = null) =>
            Inner.AppendFormatted(value: value, format: format);
    }
}
