using Xunit;

namespace Battery.Tests;

/// <summary>The two desktop parsers — the only places a reading can be got wrong.</summary>
public class BatteryTests
{
    [Theory]
    [InlineData("87", "Discharging", 87, ChargeStatus.Discharging)]
    [InlineData("100", "Full", 100, ChargeStatus.Full)]
    [InlineData("5", "Charging", 5, ChargeStatus.Charging)]
    [InlineData("120", "Charging", 100, ChargeStatus.Charging)]
    [InlineData(null, null, -1, ChargeStatus.Unknown)]
    public void ParseLinux_ReadsCapacityAndStatus(
        string? capacity, string? status, int percent, ChargeStatus charge)
    {
        var reading = BatteryDriver.ParseLinux(capacity, status);
        Assert.Equal(percent, reading.Percent);
        Assert.Equal(charge, reading.Status);
    }

    [Fact]
    public void ParsePmset_ReadsPercentAndState()
    {
        var reading = BatteryDriver.ParsePmset(
            "Now drawing from 'Battery Power'\n" +
            " -InternalBattery-0 (id=1234)\t87%; discharging; 3:21 remaining present: true\n");
        Assert.Equal(87, reading.Percent);
        Assert.Equal(ChargeStatus.Discharging, reading.Status);

        Assert.Equal(ChargeStatus.Full, BatteryDriver.ParsePmset(
            " -InternalBattery-0 (id=1)\t100%; charged; 0:00 remaining present: true").Status);
        Assert.False(BatteryDriver.ParsePmset("Now drawing from 'AC Power'").Present);
    }
}
