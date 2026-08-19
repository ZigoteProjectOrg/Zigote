namespace Permissions;

/// <summary>
///     Desktop: no runtime permission model — everything answers granted. (Sandboxed desktops
///     gate camera/microphone through portals at time of use; wiring those up is future work.)
/// </summary>
internal static class PermissionsDriver
{
    public static bool IsGranted(ZigotePermission permission) => true;

    public static Task<bool> RequestAsync(ZigotePermission permission) => Task.FromResult(true);
}
