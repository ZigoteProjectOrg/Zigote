using System.Text;
using Android.App;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace SecureStorage;

/// <summary>
///     Android implementation — an AES-GCM key that lives in the hardware-backed Android
///     Keystore (it can be used, never exported) encrypting values that are then filed in
///     ordinary SharedPreferences. That is what the AndroidX security-crypto library does; doing
///     it here is ~60 lines and one dependency fewer.
///     <para>
///         Each value carries its own IV: <c>base64(iv ‖ ciphertext)</c>, the IV being GCM's
///         fixed 12 bytes.
///     </para>
/// </summary>
internal static class SecureStorageDriver
{
    private const string KeystoreName = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvBytes = 12;
    private const int TagBits = 128;

    public static string? DefaultService => Application.Context.PackageName;

    public static bool Available => true;

    public static Task<bool> WriteAsync(string service, string key, string value)
    {
        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, KeyFor(service));

            byte[] encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(value))!;
            byte[] iv = cipher.GetIV()!;
            byte[] blob = new byte[iv.Length + encrypted.Length];
            iv.CopyTo(blob, 0);
            encrypted.CopyTo(blob, iv.Length);

            using var editor = Preferences(service).Edit()!;
            editor.PutString(key, Convert.ToBase64String(blob));
            editor.Apply();
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }

    public static Task<string?> ReadAsync(string service, string key)
    {
        try
        {
            string? stored = Preferences(service).GetString(key, null);
            if (stored is null) return Task.FromResult<string?>(null);

            byte[] blob = Convert.FromBase64String(stored);
            if (blob.Length <= IvBytes) return Task.FromResult<string?>(null);

            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, KeyFor(service), new GCMParameterSpec(TagBits, blob, 0, IvBytes));
            byte[] plain = cipher.DoFinal(blob, IvBytes, blob.Length - IvBytes)!;
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
        }
        catch (Exception)
        {
            // A key wiped by a lock-screen change or a restored backup: the value is gone, and
            // "gone" is what the caller can act on.
            return Task.FromResult<string?>(null);
        }
    }

    public static Task<bool> DeleteAsync(string service, string key)
    {
        try
        {
            using var editor = Preferences(service).Edit()!;
            editor.Remove(key);
            editor.Apply();
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }

    private static ISharedPreferences Preferences(string service)
        => Application.Context.GetSharedPreferences(service + ".secrets", FileCreationMode.Private)!;

    /// <summary>The keystore entry for this service, generated on first use and never leaving the keystore.</summary>
    private static IKey KeyFor(string service)
    {
        var keystore = KeyStore.GetInstance(KeystoreName)!;
        keystore.Load(null);

        string alias = service + ".zigote.securestorage";
        if (keystore.GetKey(alias, null) is { } existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreName)!;
        generator.Init(new KeyGenParameterSpec.Builder(
                alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .Build()!);
        return generator.GenerateKey()!;
    }
}
