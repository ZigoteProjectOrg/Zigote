namespace Zigote.Save;

public enum SaveStatus
{
    Ok,
    NotFound,
    Corrupt,
    FutureVersion,
    MigrationMissing,
    MigrationFailed,
    InvalidSlot,
    IoError,
}

public sealed record SaveWriteResult(SaveStatus Status, string? Error = null)
{
    public bool IsOk => Status == SaveStatus.Ok;
}

public sealed record SaveReadResult<T>(SaveStatus Status, T? State = default, string? Error = null)
{
    public bool IsOk => Status == SaveStatus.Ok;
}

public sealed record SaveSlotInfo(string Slot, int Version, DateTimeOffset SavedAt, long SizeBytes);
