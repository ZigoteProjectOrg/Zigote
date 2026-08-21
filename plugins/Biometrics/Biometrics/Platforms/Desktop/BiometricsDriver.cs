namespace Biometrics;

/// <summary>
///     Desktop implementation — none. Windows Hello lives behind WinRT's
///     <c>UserConsentVerifier</c>, macOS behind LocalAuthentication, and Linux has fprintd on
///     D-Bus with no common desktop prompt. Each is a real dependency for a feature desktop apps
///     rarely ask for.
///     <para>
///         ponytail: reports unavailable. Wire Windows Hello first when a desktop app needs it —
///         it is the one with a real user base.
///     </para>
/// </summary>
internal static class BiometricsDriver
{
    public static BiometricResult Check() => BiometricResult.Unavailable;

    public static Task<BiometricResult> AuthenticateAsync(
        string reason, string title, bool allowDeviceCredential, CancellationToken cancellationToken)
        => Task.FromResult(BiometricResult.Unavailable);
}
