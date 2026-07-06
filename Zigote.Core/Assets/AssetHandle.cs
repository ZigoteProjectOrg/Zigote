namespace Zigote.Core.Assets;

/// <summary>
///     A cheap, copyable reference to a streamed asset that may not be resident yet. Reading
///     <see cref="State" />/<see cref="IsLoaded" />/<see cref="Value" /> is allocation-free, so a
///     handle is safe to poll on the per-frame path. A handle keeps the asset resident for as long
///     as it is held; call <see cref="AssetManager.Release{T}" /> when done.
///     <para>
///         Poll, don't block: while <see cref="State" /> is <see cref="AssetLoadState.Loading" />,
///         <see cref="Value" /> is <see langword="null" /> — draw a placeholder via
///         <see cref="ValueOr" /> until <see cref="IsLoaded" /> flips true.
///     </para>
/// </summary>
public readonly struct AssetHandle<T> where T : class
{
    internal readonly AssetEntry<T>? Entry;

    internal AssetHandle(AssetEntry<T> entry)
    {
        Entry = entry;
    }

    /// <summary>The empty handle — never resolves; <see cref="IsValid" /> is false.</summary>
    public static AssetHandle<T> None => default;

    /// <summary>True if this handle refers to a real asset entry (not <see cref="None" />).</summary>
    public bool IsValid => Entry is not null;

    public AssetId Id => Entry?.Id ?? AssetId.Empty;

    /// <summary>Current residency state (volatile read — acquires the paired <c>State</c> write).</summary>
    public AssetLoadState State => Entry?.State ?? AssetLoadState.Unloaded;

    public bool IsLoaded => Entry is { State: AssetLoadState.Loaded };

    public bool IsLoading => Entry is { State: AssetLoadState.Loading };

    public bool IsFailed => Entry is { State: AssetLoadState.Failed };

    public string? Error => Entry?.Error;

    /// <summary>
    ///     The resident value, or <see langword="null" /> until <see cref="IsLoaded" />. Reading
    ///     <see cref="State" /> first (volatile) guarantees a non-null <see cref="Value" /> whenever it
    ///     reports <see cref="AssetLoadState.Loaded" /> — see <see cref="AssetEntry{T}.ApplyLoaded" />.
    /// </summary>
    public T? Value => IsLoaded ? Entry!.Value : null;

    /// <summary>Return the value if resident, else <paramref name="fallback" /> (a placeholder asset).</summary>
    public T ValueOr(T fallback)
    {
        return IsLoaded ? Entry!.Value! : fallback;
    }

    /// <summary>Non-allocating try-get for the resident value.</summary>
    public bool TryGet(out T value)
    {
        if (IsLoaded)
        {
            value = Entry!.Value!;
            return true;
        }

        value = null!;
        return false;
    }
}