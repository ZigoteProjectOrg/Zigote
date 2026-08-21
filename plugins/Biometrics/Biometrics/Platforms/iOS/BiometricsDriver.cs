using Foundation;
using LocalAuthentication;

namespace Biometrics;

/// <summary>
///     iOS implementation — <c>LAContext</c>. A context is single-use: iOS caches its answer, so
///     re-using one hands back the previous result instead of asking again.
/// </summary>
internal static class BiometricsDriver
{
    public static BiometricResult Check()
    {
        using var context = new LAContext();
        if (context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, out var error))
            return BiometricResult.Success;

        // LAError.BiometryNotEnrolled — hardware is there, the user has set nothing up.
        return (LAStatus?)error?.Code == LAStatus.BiometryNotEnrolled
            ? BiometricResult.NotEnrolled
            : BiometricResult.Unavailable;
    }

    public static async Task<BiometricResult> AuthenticateAsync(
        string reason, string title, bool allowDeviceCredential, CancellationToken cancellationToken)
    {
        using var context = new LAContext();
        var policy = allowDeviceCredential
            ? LAPolicy.DeviceOwnerAuthentication              // biometrics, passcode as fallback
            : LAPolicy.DeviceOwnerAuthenticationWithBiometrics;

        await using var cancellation = cancellationToken.Register(context.Invalidate);
        try
        {
            var (ok, error) = await context.EvaluatePolicyAsync(policy, reason);
            if (ok) return BiometricResult.Success;

            return (LAStatus?)error?.Code switch
            {
                LAStatus.UserCancel or LAStatus.AppCancel or LAStatus.SystemCancel => BiometricResult.Cancelled,
                LAStatus.BiometryNotEnrolled or LAStatus.PasscodeNotSet => BiometricResult.NotEnrolled,
                LAStatus.BiometryNotAvailable => BiometricResult.Unavailable,
                _ => BiometricResult.Failed
            };
        }
        catch (Exception)
        {
            return BiometricResult.Failed;
        }
    }
}
