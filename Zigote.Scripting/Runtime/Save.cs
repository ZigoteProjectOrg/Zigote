using System.Text.Json.Serialization.Metadata;
using Zigote.Save;

namespace Zigote.Scripting;

/// <summary>
///     Slot-based save-game persistence for scripts, over <see cref="SaveStore" />. Unlike the backend
///     providers (<see cref="Physics" />/<see cref="World" />), the game owns the store: the host
///     publishes
///     <see cref="DefaultDirectory" /> in play mode, and a game typically does
///     <c>Save.Store = new SaveStore(Save.DefaultDirectory!, version)</c> in <c>OnCreate</c> and
///     registers its
///     migrations there — the save schema version belongs to the game, not the engine. Without a
///     <see cref="Store" /> every call is a safe no-op (<c>Write</c> →
///     <see cref="SaveStatus.IoError" />,
///     <c>Read</c> → <see cref="SaveStatus.NotFound" /> as if never saved).
///     <para>
///         Naming: inside <c>Zigote.*</c> namespaces the bare name <c>Save</c> binds to the
///         <c>Zigote.Save</c>
///         NAMESPACE (enclosing-namespace lookup wins over using-directives), so engine code writes
///         <c>Scripting.Save.…</c>; game code in its own namespace writes <c>Save.…</c>. This mirrors
///         the existing
///         <see cref="World" />/<see cref="Vfx" /> providers.
///     </para>
/// </summary>
public static class Save
{
    /// <summary>Assigned by the game (or a test) — the host never builds a store on the game's behalf.</summary>
    public static SaveStore? Store { get; set; }

    /// <summary>
    ///     Host-published base directory for this project's saves (set in play mode, cleared on
    ///     stop).
    /// </summary>
    public static string? DefaultDirectory { get; set; }

    public static bool IsAvailable => Store != null;

    public static SaveWriteResult Write<T>(string slot, T state)
    {
        return Store?.Write(slot: slot, state: state) ?? new SaveWriteResult(
            Status: SaveStatus.IoError,
            Error: "No save store assigned."
        );
    }

    public static SaveWriteResult Write<T>(string slot, T state, JsonTypeInfo<T> typeInfo)
    {
        return Store?.Write(slot: slot, state: state, typeInfo: typeInfo) ??
               new SaveWriteResult(Status: SaveStatus.IoError, Error: "No save store assigned.");
    }

    public static SaveReadResult<T> Read<T>(string slot) =>
        Store?.Read<T>(slot) ?? new SaveReadResult<T>(SaveStatus.NotFound);

    public static SaveReadResult<T> Read<T>(string slot, JsonTypeInfo<T> typeInfo) =>
        Store?.Read(slot: slot, typeInfo: typeInfo) ?? new SaveReadResult<T>(SaveStatus.NotFound);

    public static bool Exists(string slot) => Store?.Exists(slot) ?? false;

    public static bool Delete(string slot) => Store?.Delete(slot) ?? false;

    public static IReadOnlyList<SaveSlotInfo> List() => Store?.List() ?? [];
}
