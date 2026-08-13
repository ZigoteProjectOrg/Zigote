namespace Zigote.Core.Assets;

/// <summary>
///     Stable, file-system-location-independent identity for a project asset.
///     Stored in <see cref="AssetRegistry" /> and persisted to disk alongside the project.
///     When a file is renamed or moved, only the registry's path entry changes — all scene
///     references that hold the <see cref="AssetId" /> remain valid.
/// </summary>
public readonly record struct AssetId(Guid Value)
{
    public static readonly AssetId Empty = new(Guid.Empty);

    public bool IsEmpty => Value == Guid.Empty;

    public static AssetId New()
    {
        return new AssetId(Guid.NewGuid());
    }

    public static AssetId Parse(string s)
    {
        return new AssetId(Guid.Parse(s));
    }

    public static bool TryParse(string? s, out AssetId id)
    {
        if (Guid.TryParse(s, out var g))
        {
            id = new AssetId(g);
            return true;
        }

        id = Empty;
        return false;
    }

    /// <summary>Compact lowercase hex without dashes — safe as a filename/JSON key.</summary>
    public override string ToString()
    {
        return Value.ToString("N");
    }
}
