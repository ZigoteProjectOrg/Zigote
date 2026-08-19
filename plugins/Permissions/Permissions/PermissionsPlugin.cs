namespace Permissions;

/// <summary>A capability the app may have to ask the OS for. One value per user-facing ask,
///     not per manifest string — the platform decides what backs it.</summary>
public enum ZigotePermission
{
    Notifications,
    Camera,
    Microphone,
    MediaAudio,
    MediaImages,
    MediaVideo,
    LocationWhenInUse,
}

/// <summary>
///     Permissions — ask the platform for a capability. Static facts and prompts, so there is
///     nothing to register with <c>PluginHost</c>. The csproj compiles exactly one
///     <c>Platforms/PermissionsDriver</c> per target framework; desktop answers granted for
///     everything.
/// </summary>
public static class PermissionsPlugin
{
    /// <summary>
    ///     One request at a time, strictly in sequence: each request puts a system dialog on
    ///     screen, and asking for the second while the first is still up loses it — the second
    ///     request never reaches the user and the permission stays denied. So the next request
    ///     only goes up after the previous answer.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(initialCount: 1, maxCount: 1);

    /// <summary>Whether the capability is granted right now. Never prompts.</summary>
    public static bool IsGranted(ZigotePermission permission)
        => PermissionsDriver.IsGranted(permission);

    /// <summary>
    ///     Ensure the capability, prompting the user if the platform requires it and has not
    ///     asked before. True = granted. Call from a user gesture where possible. Concurrent
    ///     calls are serialized, so it is safe to fire several without awaiting in sequence.
    /// </summary>
    public static async Task<bool> RequestAsync(ZigotePermission permission)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await PermissionsDriver.RequestAsync(permission).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
