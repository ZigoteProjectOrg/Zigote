using Foundation;
using Security;

namespace SecureStorage;

/// <summary>
///     iOS implementation — the Keychain, one generic-password item per key, filed under the
///     service name. Items are <c>AfterFirstUnlockThisDeviceOnly</c>: readable by background
///     work once the user has unlocked the device since boot, and never carried to another
///     device by a backup.
/// </summary>
internal static class SecureStorageDriver
{
    public static string? DefaultService => NSBundle.MainBundle.BundleIdentifier;

    public static bool Available => true;

    private static SecRecord Query(string service, string key)
        => new(SecKind.GenericPassword) { Service = service, Account = key };

    public static Task<bool> WriteAsync(string service, string key, string value)
    {
        // Add refuses a duplicate, so a write is a remove followed by an add.
        SecKeyChain.Remove(Query(service, key));

        var record = Query(service, key);
        record.ValueData = NSData.FromString(value, NSStringEncoding.UTF8);
        record.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
        return Task.FromResult(SecKeyChain.Add(record) == SecStatusCode.Success);
    }

    public static Task<string?> ReadAsync(string service, string key)
    {
        var data = SecKeyChain.QueryAsData(Query(service, key), false, out SecStatusCode status);
        if (status != SecStatusCode.Success || data is null) return Task.FromResult<string?>(null);
        return Task.FromResult(NSString.FromData(data, NSStringEncoding.UTF8)?.ToString());
    }

    public static Task<bool> DeleteAsync(string service, string key)
    {
        var status = SecKeyChain.Remove(Query(service, key));
        // Already absent is the outcome the caller asked for.
        return Task.FromResult(status is SecStatusCode.Success or SecStatusCode.ItemNotFound);
    }
}
