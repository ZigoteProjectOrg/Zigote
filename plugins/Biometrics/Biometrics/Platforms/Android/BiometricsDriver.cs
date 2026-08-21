using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
using Java.Lang;
using Exception = System.Exception;

namespace Biometrics;

/// <summary>
///     Android implementation — the framework <see cref="BiometricPrompt" /> (API 28+), not
///     AndroidX: one dependency fewer, at the cost of answering
///     <see cref="BiometricResult.Unavailable" /> on API 26–27, where the only alternative was
///     the deprecated fingerprint manager.
/// </summary>
internal static class BiometricsDriver
{
    public static BiometricResult Check()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return BiometricResult.Unavailable;

        var manager = (BiometricManager?)Application.Context.GetSystemService(Context.BiometricService);
        if (manager is null) return BiometricResult.Unavailable;

        return manager.CanAuthenticate() switch
        {
            BiometricCode.Success => BiometricResult.Success,
            BiometricCode.ErrorNoneEnrolled => BiometricResult.NotEnrolled,
            _ => BiometricResult.Unavailable
        };
    }

    public static Task<BiometricResult> AuthenticateAsync(
        string reason, string title, bool allowDeviceCredential, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
            return Task.FromResult(BiometricResult.Unavailable);

        var tcs = new TaskCompletionSource<BiometricResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var builder = new BiometricPrompt.Builder(Application.Context)
                .SetTitle(title)!
                .SetDescription(reason)!;

            if (allowDeviceCredential && OperatingSystem.IsAndroidVersionAtLeast(30))
                // The binding types this parameter as a plain int; the flags are the values of
                // BiometricManagerAuthenticators.
                builder.SetAllowedAuthenticators(
                    (int)(BiometricManagerAuthenticators.BiometricWeak |
                          BiometricManagerAuthenticators.DeviceCredential));
            else
                // Without a credential fallback the prompt must offer its own way out, or the
                // user is stuck staring at it.
                builder.SetNegativeButton("Cancel", Application.Context.MainExecutor!,
                    new DialogClick(() => tcs.TrySetResult(BiometricResult.Cancelled)));

            var signal = new CancellationSignal();
            cancellationToken.Register(signal.Cancel);

            builder.Build()!.Authenticate(
                signal, Application.Context.MainExecutor!, new Callback(tcs));
        }
        catch (Exception)
        {
            tcs.TrySetResult(BiometricResult.Unavailable);
        }

        return tcs.Task;
    }

    private sealed class Callback(TaskCompletionSource<BiometricResult> tcs)
        : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result)
            => tcs.TrySetResult(BiometricResult.Success);

        public override void OnAuthenticationError(BiometricErrorCode errorCode, ICharSequence? errString)
            => tcs.TrySetResult(errorCode is BiometricErrorCode.Canceled or BiometricErrorCode.UserCanceled
                ? BiometricResult.Cancelled
                : errorCode is BiometricErrorCode.NoBiometrics
                    ? BiometricResult.NotEnrolled
                    : BiometricResult.Failed);

        // Fired for every rejected finger; the prompt stays up, so it is not an answer yet.
        public override void OnAuthenticationFailed()
        {
        }
    }

    private sealed class DialogClick(Action onClick) : Java.Lang.Object, IDialogInterfaceOnClickListener
    {
        public void OnClick(IDialogInterface? dialog, int which) => onClick();
    }
}
