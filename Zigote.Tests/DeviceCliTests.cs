using Xunit;
using Zigote.Cli;

namespace Zigote.Tests;

/// <summary>
///     The two pieces of <c>zigote device</c> that decide something: which devices adb reported, and
///     which RID a device's ABI means. Everything else in that verb is argument assembly a test can
///     only restate.
/// </summary>
public class DeviceCliTests
{
    // Real `adb devices -l` output, daemon chatter and all — the lines that are not devices are the
    // whole reason this is parsed rather than split.
    private const string AdbOutput = """
                                     * daemon not running; starting now at tcp:5037
                                     * daemon started successfully
                                     List of devices attached
                                     emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64x
                                     3A291FDH200
                                     """;

    [Fact]
    public void Parse_ReadsDevicesAndSkipsChatter()
    {
        var devices = Device.Parse(AdbOutput);

        // The daemon lines, the header, and the serial with no state at all are not devices.
        var only = Assert.Single(devices);
        Assert.Equal("emulator-5554", only.Serial);
        Assert.Equal("sdk_gphone64_x86_64", only.Model);
        Assert.True(only.Ready);
    }

    [Fact]
    public void Parse_KeepsUnreadyDevicesButDoesNotCallThemReady()
    {
        var devices = Device.Parse(
            "List of devices attached\nZY223KJ8QW           unauthorized usb:1-3\n"
        );

        var only = Assert.Single(devices);
        Assert.False(only.Ready); // named in the error rather than silently absent
        Assert.Equal("unauthorized", only.State);
    }

    [Theory]
    [InlineData("arm64-v8a", "android-arm64")]
    [InlineData("x86_64\n", "android-x64")] // getprop's trailing newline
    public void Rid_MapsDeviceAbi(string abi, string expected) => Assert.Equal(expected, Device.Rid(abi));

    [Fact]
    public void Rid_RefusesAnAbiTheEngineHasNoLibFor() =>
        Assert.Throws<CliError>(() => Device.Rid("armeabi-v7a"));
}
