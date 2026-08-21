# SecureStorage

Small secrets — a token, a refresh key, a passphrase — in whatever the OS uses to keep them.
The `flutter_secure_storage` slot from the [plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
if (!SecureStoragePlugin.Available) return SignInAgain();   // no keystore here
await SecureStoragePlugin.WriteAsync("refresh.token", token);
string? token = await SecureStoragePlugin.ReadAsync("refresh.token");
await SecureStoragePlugin.DeleteAsync("refresh.token");
```

**No plaintext fallback.** Where the platform has no keystore, `Available` is false, writes fail
and reads answer null — an app that cannot store a token securely should say so rather than
write it to a file and pretend. Static, so no `PluginHost.Register`.

Keys are letters, digits, `.`, `-`, `_`, up to 128 characters (they name a keychain item on one
platform and a file on another); anything else throws `ArgumentException`, because a bad key is a
bug in the calling code. Values are strings and meant to be small — a keystore is not a database.

| Platform | Backend |
|---|---|
| Linux | Secret Service (GNOME Keyring, KWallet) via `secret-tool`; the secret goes in on stdin, never argv. No `secret-tool` → `Available` is false |
| macOS | login keychain via the `security` tool |
| Windows | DPAPI (`CryptProtectData`, current user, service name as entropy) over a file in LocalAppData |
| Android | AES-GCM key in the hardware-backed Android Keystore; ciphertext in SharedPreferences (what AndroidX security-crypto does, one dependency fewer) |
| iOS | Keychain generic-password items, `AfterFirstUnlockThisDeviceOnly` — not carried to another device by a backup |

`SecureStoragePlugin.Service` is the namespace secrets are filed under: the package id on mobile,
the entry assembly name on desktop. Set it at startup to share secrets across builds of one app.
