# Biometrics

Face, fingerprint or device passcode — the `local_auth` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md). The natural partner of **SecureStorage**: keep the
token in the keystore, unlock it with the user's face.

```csharp
var result = await BiometricsPlugin.AuthenticateAsync("Unlock your vault", ct: ct);
if (result == BiometricResult.Success) Reveal();
else if (result == BiometricResult.NotEnrolled) await AppSettingsPlugin.OpenAsync();
```

`BiometricResult` is `Success`, `Cancelled`, `Failed`, `Unavailable` or `NotEnrolled` — the last
one kept separate so an app can offer "set up Face ID" instead of hiding the option. Never throws.
Static, so no `PluginHost.Register`.

This proves the person holding the device is its enrolled owner. It is not a key and does not
protect data at rest on its own — pair it with SecureStorage for that.

| Platform | Backend |
|---|---|
| Android | framework `BiometricPrompt` (API 28+; API 26–27 report unavailable) — no AndroidX dependency. Device-credential fallback via `SetAllowedAuthenticators` on API 30+, an explicit Cancel button below it |
| iOS | `LAContext` — `DeviceOwnerAuthentication` with the passcode fallback, `…WithBiometrics` without. A fresh context per call, because iOS caches an old one's answer |
| Desktop | `Unavailable`. Windows Hello is the first one worth wiring when a desktop app asks |
