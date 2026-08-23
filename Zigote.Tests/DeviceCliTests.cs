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

/// <summary>
///     The one decision <c>zigote run</c> makes: where to run. Exactly one candidate is silent,
///     zero means desktop, a name matches loosely, and anything ambiguous is refused rather than
///     guessed — deploying to the wrong phone is a silent failure.
/// </summary>
public class RunTargetTests
{
    private static List<Device.Attached> Ready(params string[] serials) =>
        serials.Select(s => new Device.Attached(Serial: s, State: "device", Model: $"{s}-model"))
            .ToList();

    [Fact]
    public void NoDevices_MeansDesktop() => Assert.Null(Run.Resolve(spec: null, ready: []));

    [Fact]
    public void DesktopKeyword_WinsOverAnAttachedDevice() =>
        Assert.Null(Run.Resolve(spec: "Desktop", ready: Ready("emulator-5554")));

    [Fact]
    public void OneDevice_IsChosenSilently() =>
        Assert.Equal("emulator-5554", Run.Resolve(spec: null, ready: Ready("emulator-5554"))?.Serial);

    [Fact]
    public void ANameMatchesModelOrSerialLoosely() =>
        Assert.Equal("pixel8", Run.Resolve(spec: "PIXEL", ready: Ready("pixel8", "emulator-5554"))?.Serial);

    [Fact]
    public void AnUnmatchedNameIsAnError_NotASilentFallbackToDesktop() =>
        Assert.Throws<CliError>(() => Run.Resolve(spec: "nokia", ready: Ready("pixel8")));

    [Fact]
    public void AnAmbiguousNameIsRefused() =>
        Assert.Throws<CliError>(() => Run.Resolve(spec: "pixel", ready: Ready("pixel8", "pixel9")));
}
