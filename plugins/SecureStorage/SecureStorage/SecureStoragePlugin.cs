using System.Reflection;

namespace SecureStorage;

/// <summary>
///     SecureStorage — small secrets (tokens, refresh keys, a passphrase) in whatever the OS
///     uses to keep them, the <c>flutter_secure_storage</c> slot from the plugin roadmap.
///     Static, nothing to register with <c>PluginHost</c>.
///     <para>
///         There is no plaintext fallback, deliberately. Where the platform has no keystore the
///         plugin reports <see cref="Available" /> false and every write fails — an app that
///         cannot store a token securely should say so, not write it to a file and pretend.
///     </para>
///     <para>
///         Values are strings and meant to be small (a keystore is not a database). Reads and
///         writes hit the OS, so they are async; concurrent calls for the same key are the
///         caller's business.
///     </para>
/// </summary>
public static class SecureStoragePlugin
{
    private static string? _service;

    /// <summary>
    ///     The namespace secrets are filed under — the keychain "service", the Secret Service
    ///     attribute, the settings file name. Defaults to the app's package id (mobile) or entry
    ///     assembly name (desktop); set it once at startup if you need a specific one, e.g. to
    ///     share secrets with another build of the same app.
    /// </summary>
    public static string Service
    {
        get => _service ??= SecureStorageDriver.DefaultService
                            ?? Assembly.GetEntryAssembly()?.GetName().Name
                            ?? "zigote.app";
        set => _service = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Service cannot be blank", nameof(value))
            : value;
    }

    /// <summary>False where this device has no keystore to write to — then every write fails and every read is null.</summary>
    public static bool Available => SecureStorageDriver.Available;

    /// <summary>Store a secret under a key, replacing what was there. False means it was not stored.</summary>
    public static Task<bool> WriteAsync(string key, string value)
    {
        Validate(key);
        ArgumentNullException.ThrowIfNull(value);
        return Available ? SecureStorageDriver.WriteAsync(Service, key, value) : Task.FromResult(false);
    }

    /// <summary>Read a secret. Null means "not stored here" — and also "could not be read".</summary>
    public static Task<string?> ReadAsync(string key)
    {
        Validate(key);
        return Available ? SecureStorageDriver.ReadAsync(Service, key) : Task.FromResult<string?>(null);
    }

    /// <summary>Forget a secret. True if it is gone (including when it was never there).</summary>
    public static Task<bool> DeleteAsync(string key)
    {
        Validate(key);
        return Available ? SecureStorageDriver.DeleteAsync(Service, key) : Task.FromResult(false);
    }

    /// <summary>Whether a secret is stored under this key.</summary>
    public static async Task<bool> ContainsAsync(string key) => await ReadAsync(key) is not null;

    /// <summary>
    ///     Keys name a keychain entry, a settings entry and a filename on the three desktops, so
    ///     they are kept to what all of those accept without escaping: letters, digits, dot,
    ///     dash, underscore. A key that breaks the rule is a bug in the calling code, not a
    ///     runtime condition — hence the exception rather than a false.
    /// </summary>
    internal static void Validate(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128)
            throw new ArgumentException("Key must be 128 characters or fewer", nameof(key));
        // "." and ".." are legal by the character rule and mean something else entirely to a
        // filesystem — the Windows backend names a file after the key.
        if (key.All(c => c == '.'))
            throw new ArgumentException("Key cannot be all dots", nameof(key));
        foreach (char c in key)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
                throw new ArgumentException(
                    $"Key may only contain letters, digits, '.', '-' and '_' (got '{c}')", nameof(key));
    }
}
