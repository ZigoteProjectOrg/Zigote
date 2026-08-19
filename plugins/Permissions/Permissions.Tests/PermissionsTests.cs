using Xunit;

namespace Permissions.Tests;

public class PermissionsTests
{
    [Fact]
    public void Desktop_EverythingGranted()
    {
        foreach (var permission in Enum.GetValues<ZigotePermission>())
            Assert.True(PermissionsPlugin.IsGranted(permission));
    }

    [Fact]
    public async Task RequestAsync_ConcurrentCallsSerializeAndGrant()
    {
        bool[] results = await Task.WhenAll(
            PermissionsPlugin.RequestAsync(ZigotePermission.Camera),
            PermissionsPlugin.RequestAsync(ZigotePermission.Microphone),
            PermissionsPlugin.RequestAsync(ZigotePermission.Notifications));
        Assert.All(results, Assert.True);
    }
}
