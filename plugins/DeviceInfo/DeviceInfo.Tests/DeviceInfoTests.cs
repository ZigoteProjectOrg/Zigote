using Xunit;

namespace DeviceInfo.Tests;

public class DeviceInfoTests
{
    [Fact]
    public void ParsePrettyName_ReadsQuotedValue()
    {
        string?[] cases =
        [
            DeviceInfoDriver.ParsePrettyName(["NAME=Fedora", "PRETTY_NAME=\"Fedora Linux 44 (Workstation Edition)\""]),
            DeviceInfoDriver.ParsePrettyName(["PRETTY_NAME=Alpine Linux v3.20"]),
        ];
        Assert.Equal("Fedora Linux 44 (Workstation Edition)", cases[0]);
        Assert.Equal("Alpine Linux v3.20", cases[1]);
    }

    [Fact]
    public void ParsePrettyName_NullWhenAbsentOrEmpty()
    {
        Assert.Null(DeviceInfoDriver.ParsePrettyName([]));
        Assert.Null(DeviceInfoDriver.ParsePrettyName(["NAME=Fedora"]));
        Assert.Null(DeviceInfoDriver.ParsePrettyName(["PRETTY_NAME=\"\""]));
    }

    [Fact]
    public void Get_AnswersOnThisMachine()
    {
        var profile = DeviceInfoPlugin.Get();
        Assert.NotEqual("", profile.Os);
        Assert.NotEqual("", profile.OsVersion);
        Assert.NotEqual("", profile.Architecture);
        Assert.NotEqual("", profile.AppName); // entry assembly fallback
        Assert.True(profile.IsPhysicalDevice); // always true on desktop
        Assert.Equal(0, profile.SdkInt);
        if (OperatingSystem.IsLinux()) Assert.NotEqual("", profile.DeviceId); // /etc/machine-id
        Assert.Same(profile, DeviceInfoPlugin.Get()); // cached
    }
}
