namespace Biometrics;

/// <summary>How an authentication attempt ended.</summary>
public enum BiometricResult
{
    /// <summary>The user proved who they are.</summary>
    Success,

    /// <summary>The user dismissed the prompt, or the caller cancelled it.</summary>
    Cancelled,

    /// <summary>The check ran and did not pass — a wrong face, too many failed fingers, a lockout.</summary>
    Failed,

    /// <summary>No biometric hardware, or the platform has no such API.</summary>
    Unavailable,

    /// <summary>Hardware is there but the user has enrolled nothing — send them to Settings.</summary>
    NotEnrolled
}

/// <summary>
///     Biometrics — a face, a finger or the device passcode, the <c>local_auth</c> slot from the
///     plugin roadmap. The natural partner of SecureStorage: keep the token in the keystore,
///     unlock it with the user's face.
///     <para>
///         This proves the person holding the device is the enrolled owner. It is not a
///         cryptographic key and it does not protect data at rest by itself — pair it with
///         SecureStorage for that. Static, nothing to register with <c>PluginHost</c>.
///     </para>
/// </summary>
public static class BiometricsPlugin
{
    /// <summary>
    ///     Whether a prompt would do anything here: hardware present, and something enrolled.
    ///     <see cref="BiometricResult.NotEnrolled" /> is reported separately so an app can offer
    ///     "set up Face ID" instead of hiding the option.
    /// </summary>
    public static BiometricResult Check() => BiometricsDriver.Check();

    /// <summary>True when <see cref="AuthenticateAsync" /> can succeed right now.</summary>
    public static bool Available => Check() == BiometricResult.Success;

    /// <summary>
    ///     Show the platform prompt and wait for the answer. Never throws — a refusal, a
    ///     lockout and a device with no sensor are all results, not exceptions.
    /// </summary>
    /// <param name="reason">Shown to the user: why the app is asking. iOS requires it.</param>
    /// <param name="title">Prompt title on Android; iOS shows the app name.</param>
    /// <param name="allowDeviceCredential">Let the user fall back to the PIN, pattern or passcode.</param>
    public static Task<BiometricResult> AuthenticateAsync(
        string reason,
        string? title = null,
        bool allowDeviceCredential = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Check() is var state && state is BiometricResult.Unavailable or BiometricResult.NotEnrolled)
            return Task.FromResult(state);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(BiometricResult.Cancelled);

        return BiometricsDriver.AuthenticateAsync(
            reason, title ?? reason, allowDeviceCredential, cancellationToken);
    }
}
