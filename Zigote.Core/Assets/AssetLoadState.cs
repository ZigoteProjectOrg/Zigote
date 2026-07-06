namespace Zigote.Core.Assets;

/// <summary>Residency state of a streamed asset. Byte-backed so it can be a <c>volatile</c> field.</summary>
public enum AssetLoadState : byte
{
    /// <summary>No load requested, or the asset was evicted.</summary>
    Unloaded = 0,

    /// <summary>A background load is in flight; <see cref="AssetHandle{T}.Value" /> is not yet available.</summary>
    Loading = 1,

    /// <summary>Resident — the value is available on the main thread.</summary>
    Loaded = 2,

    /// <summary>The load failed; see <see cref="AssetHandle{T}.Error" />.</summary>
    Failed = 3,
}