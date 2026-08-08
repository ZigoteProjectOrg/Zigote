namespace Zigote.Core;

/// <summary>
///     The outcome of an operation that is allowed to fail: a value, or a key explaining why there
///     isn't one.
///     <para>
///         Failures cross layer boundaries as values, not exceptions. A repository knows an
///         <see cref="System.Net.Http.HttpRequestException" /> means "offline"; a bloc two layers up
///         should not have to, and a caught-and-rethrown exception is how it ends up having to.
///     </para>
///     <para>
///         <see cref="Error" /> is a key, not a sentence — the UI maps it to localized text, so a
///         message can never reach a user untranslated, which is exactly what happens the first time
///         an exception message is rendered straight into a banner. Each app owns its own set of
///         keys; this type only carries them.
///     </para>
/// </summary>
public readonly record struct Result<T>
{
    private Result(T? value, string? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    /// <summary>The failure key, or null when this succeeded.</summary>
    public string? Error { get; }

    public bool IsOk => Error is null;

    public static Result<T> Ok(T value)
    {
        return new Result<T>(value, null);
    }

    public static Result<T> Fail(string error)
    {
        return new Result<T>(default, error);
    }

    /// <summary>Fold both cases into one value, so a caller cannot forget the failure.</summary>
    public TOut Match<TOut>(Func<T, TOut> ok, Func<string, TOut> fail)
    {
        return Error is { } error ? fail(error) : ok(Value!);
    }
}