using System.Globalization;
using System.Runtime.CompilerServices;

namespace Zigote.Core;

/// <summary>
///     A per-callsite text cache for per-frame UI readouts: <c>label.Text = _fps.Update($"{fps:F0} fps")</c>
///     formats the interpolation directly into a reusable char buffer (via a custom interpolated-string
///     handler — no intermediate strings, no boxing) and returns the <b>previously returned instance</b>
///     when the rendered text is unchanged, so a steady readout allocates nothing and downstream
///     <c>Text</c> setters early-out on reference equality. A string is allocated only when the text
///     actually changed. Numbers format with invariant culture (matching charts/devtools).
///     One <see cref="CachedText" /> per readout; not thread-safe, and do not nest an Update of the
///     same instance inside its own interpolation. NOTE: formatting runs before the cache compare, so
///     an enum (or any non-<see cref="ISpanFormattable" /> value) in the interpolation still pays its
///     ToString every call — pre-cache enum names (<c>Enum.GetNames</c>) or key-cache by value instead.
/// </summary>
public sealed class CachedText
{
    private char[] _scratch;
    private string _value = "";

    public CachedText(int capacity = 64)
    {
        _scratch = new char[Math.Max(16, capacity)];
    }

    /// <summary>The last rendered text.</summary>
    public string Value => _value;

    /// <summary>Format an interpolated string, returning the cached instance when unchanged.</summary>
    public string Update([InterpolatedStringHandlerArgument("")] ref Handler text)
    {
        var span = text.Written;
        if (span.SequenceEqual(_value)) return _value;
        _value = new string(span);
        return _value;
    }

    /// <summary>Non-interpolated variant: cache an externally produced span (e.g. sliced text).</summary>
    public string Update(ReadOnlySpan<char> text)
    {
        if (text.SequenceEqual(_value)) return _value;
        _value = new string(text);
        return _value;
    }

    private void Grow(int needed)
    {
        var next = new char[Math.Max(_scratch.Length * 2, needed)];
        _scratch.AsSpan().CopyTo(next); // preserve the written prefix
        _scratch = next;
    }

    /// <summary>
    ///     Writes interpolation parts straight into the owner's scratch buffer. Value types that
    ///     implement <see cref="ISpanFormattable" /> (all numeric primitives, TimeSpan, DateTime, …)
    ///     format via <c>TryFormat</c> — the <c>value is ISpanFormattable</c> pattern below is the
    ///     BCL's own: the JIT devirtualizes it per instantiation, so no boxing.
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct Handler
    {
        private readonly CachedText _owner;
        private int _pos;

        public Handler(int literalLength, int formattedCount, CachedText owner)
        {
            _owner = owner;
            _pos = 0;
        }

        internal readonly ReadOnlySpan<char> Written => _owner._scratch.AsSpan(0, _pos);

        public void AppendLiteral(string s)
        {
            Ensure(s.Length);
            s.CopyTo(_owner._scratch.AsSpan(_pos));
            _pos += s.Length;
        }

        public void AppendFormatted(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            Ensure(s.Length);
            s.CopyTo(_owner._scratch.AsSpan(_pos));
            _pos += s.Length;
        }

        public void AppendFormatted(ReadOnlySpan<char> s)
        {
            Ensure(s.Length);
            s.CopyTo(_owner._scratch.AsSpan(_pos));
            _pos += s.Length;
        }

        public void AppendFormatted(char c)
        {
            Ensure(1);
            _owner._scratch[_pos++] = c;
        }

        public void AppendFormatted(bool b)
        {
            AppendLiteral(b ? "true" : "false");
        }

        // Non-generic overloads for the common primitives: guaranteed box-free in ANY build.
        // (The BCL's `value is ISpanFormattable` box-elision is a JIT optimization that Debug
        // assemblies — DisableOptimizations — never get, so the generic path boxes there.)
        public void AppendFormatted(int value, string? format = null)
        {
            int written;
            while (!value.TryFormat(
                       _owner._scratch.AsSpan(_pos),
                       out written,
                       format,
                       CultureInfo.InvariantCulture
                   ))
                _owner.Grow(_owner._scratch.Length + 1);
            _pos += written;
        }

        public void AppendFormatted(uint value, string? format = null)
        {
            int written;
            while (!value.TryFormat(
                       _owner._scratch.AsSpan(_pos),
                       out written,
                       format,
                       CultureInfo.InvariantCulture
                   ))
                _owner.Grow(_owner._scratch.Length + 1);
            _pos += written;
        }

        public void AppendFormatted(long value, string? format = null)
        {
            int written;
            while (!value.TryFormat(
                       _owner._scratch.AsSpan(_pos),
                       out written,
                       format,
                       CultureInfo.InvariantCulture
                   ))
                _owner.Grow(_owner._scratch.Length + 1);
            _pos += written;
        }

        public void AppendFormatted(ulong value, string? format = null)
        {
            int written;
            while (!value.TryFormat(
                       _owner._scratch.AsSpan(_pos),
                       out written,
                       format,
                       CultureInfo.InvariantCulture
                   ))
                _owner.Grow(_owner._scratch.Length + 1);
            _pos += written;
        }

        public void AppendFormatted(float value, string? format = null)
        {
            int written;
            while (!value.TryFormat(
                       _owner._scratch.AsSpan(_pos),
                       out written,
                       format,
                       CultureInfo.InvariantCulture
                   ))
                _owner.Grow(_owner._scratch.Length + 1);
            _pos += written;
        }

        public void AppendFormatted(double value, string? format = null)
        {
            int written;
            while (!value.TryFormat(
                       _owner._scratch.AsSpan(_pos),
                       out written,
                       format,
                       CultureInfo.InvariantCulture
                   ))
                _owner.Grow(_owner._scratch.Length + 1);
            _pos += written;
        }

        public void AppendFormatted(TimeSpan value, string? format = null)
        {
            int written;
            while (!value.TryFormat(
                       _owner._scratch.AsSpan(_pos),
                       out written,
                       format,
                       CultureInfo.InvariantCulture
                   ))
                _owner.Grow(_owner._scratch.Length + 1);
            _pos += written;
        }

        /// <summary>Fallback for exotic types — may box a struct; avoid on hot paths.</summary>
        public void AppendFormatted<T>(T value, string? format = null)
        {
            if (value is ISpanFormattable)
            {
                int written;
                while (!((ISpanFormattable)value).TryFormat(
                           _owner._scratch.AsSpan(_pos),
                           out written,
                           format,
                           CultureInfo.InvariantCulture
                       ))
                    _owner.Grow(_owner._scratch.Length + 1);
                _pos += written;
                return;
            }

            AppendFormatted(value?.ToString());
        }

        private void Ensure(int more)
        {
            var needed = _pos + more;
            if (needed > _owner._scratch.Length) _owner.Grow(needed);
        }
    }
}