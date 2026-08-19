using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Zigote.Http;

/// <summary>
///     Either a <typeparamref name="T" /> or an <see cref="HttpError" />. The canonical return type
///     of everything in this assembly.
/// </summary>
/// <remarks>
///     <para>
///         This is the deliberate "adopt Result across one whole boundary" case, and the boundary is
///         <c>Zigote.Http</c>. It does not leak: <see cref="Unwrap" /> converts to an exception at
///         the caller's choosing, and F# sees it as <c>Result&lt;'T, HttpError&gt;</c> through
///         <c>Http.toResult</c>. What we never do is return this from some methods and throw from
///         others.
///     </para>
///     <para>A struct: the success path of a cache hit should not allocate a wrapper to say so.</para>
/// </remarks>
public readonly struct HttpResult<T>
{
    private readonly T _value;

    private HttpResult(T value, HttpError? error)
    {
        _value = value;
        Error = error;
    }

    /// <summary>The error, or null on success.</summary>
    public HttpError? Error { get; }

    /// <summary>True when this carries a value.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsOk => Error is null;

    /// <summary>The value. Throws <see cref="InvalidOperationException" /> when this is an error — check <see cref="IsOk" />, or use <see cref="Unwrap" />.</summary>
    public T Value => IsOk
        ? _value
        : throw new InvalidOperationException($"HttpResult is an error: {Error.Message}");

    /// <summary>A success.</summary>
    public static HttpResult<T> Ok(T value) => new(value, null);

    /// <summary>A failure.</summary>
    public static HttpResult<T> Fail(HttpError error) => new(default!, error);

    /// <summary>Lets a method <c>return value;</c> where an <see cref="HttpResult{T}" /> is expected.</summary>
    public static implicit operator HttpResult<T>(T value) => Ok(value);

    /// <summary>Lets a method <c>return error;</c> where an <see cref="HttpResult{T}" /> is expected.</summary>
    public static implicit operator HttpResult<T>(HttpError error) => Fail(error);

    /// <summary>Pattern-matching deconstruction: <c>if (result.TryGet(out var v, out var e))</c>.</summary>
    public bool TryGet([MaybeNullWhen(false)] out T value, [MaybeNullWhen(true)] out HttpError error)
    {
        value = _value;
        error = Error;
        return Error is null;
    }

    /// <summary>The value, or <paramref name="fallback" /> on any error.</summary>
    public T OrElse(T fallback) => IsOk ? _value : fallback;

    /// <summary>
    ///     The value, or <paramref name="fallback" /> when the error is exactly HTTP
    ///     <paramref name="code" /> — "a 404 is an empty list" as one word, while every other error
    ///     stays an error. The typed equivalent of Dio's <c>validateStatus</c>, without a predicate
    ///     living inside the request value.
    /// </summary>
    public HttpResult<T> Recover(HttpStatusCode code, T fallback) =>
        Error is HttpError.Status status && status.Code == code ? Ok(fallback) : this;

    /// <summary>The value, or throw <see cref="HttpException" />. The one sanctioned bridge to exceptions.</summary>
    public T Unwrap() => IsOk ? _value : throw new HttpException(Error);

    /// <summary>Transform the value, carrying any error through untouched.</summary>
    public HttpResult<TOut> Map<TOut>(Func<T, TOut> f) =>
        IsOk ? HttpResult<TOut>.Ok(f(_value)) : HttpResult<TOut>.Fail(Error);

    /// <summary>Chain another fallible step.</summary>
    public HttpResult<TOut> Bind<TOut>(Func<T, HttpResult<TOut>> f) =>
        IsOk ? f(_value) : HttpResult<TOut>.Fail(Error);

    /// <summary>Collapse both sides into one value.</summary>
    public TOut Match<TOut>(Func<T, TOut> onOk, Func<HttpError, TOut> onError) =>
        IsOk ? onOk(_value) : onError(Error);

    /// <inheritdoc />
    public override string ToString() => IsOk ? $"Ok({_value})" : $"Error({Error.Message})";
}

/// <summary>Constructors that infer <c>T</c>, so call sites read <c>HttpResult.Ok(x)</c>.</summary>
public static class HttpResult
{
    /// <summary>A success.</summary>
    public static HttpResult<T> Ok<T>(T value) => HttpResult<T>.Ok(value);

    /// <summary>A failure of the given shape.</summary>
    public static HttpResult<T> Fail<T>(HttpError error) => HttpResult<T>.Fail(error);
}
