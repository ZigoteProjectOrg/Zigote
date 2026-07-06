namespace Zigote.Editor.History;

/// <summary>
///     Represents an action that can be executed and undone.
/// </summary>
public interface ICommand
{
    void Execute();

    void Undo();

    /// <summary>
    ///     Attempt to absorb <paramref name="other" /> into this command (drag-scrub coalescing).
    ///     Returns true if merged — the caller then discards <paramref name="other" />.
    /// </summary>
    bool TryMergeWith(ICommand other);
}