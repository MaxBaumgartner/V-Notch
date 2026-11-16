using System.Globalization;
using System.Text;
using VNotch.Services.Phone;
using Xunit;

namespace VNotch.Tests;

public sealed class PhoneWatcherTests
{
    [Fact]
    public void DevicesIncludeKnownStatesAndWirelessSerials()
    {
        const string output = """
        * daemon not running; starting now at tcp:5037
        * daemon started successfully
        List of devices attached
        usbphone               device product:nezha_eea model:2512BPNDAG device:nezha transport_id:1
        lockedphone            unauthorized transport_id:2
        lostphone              offline transport_id:3
        192.168.18.29:5555      device product:nezha model:2512BPNDAG
        recoveryphone          recovery
        malformed
        """;

        Assert.Equal(new[]
        {
            ("usbphone", PhoneState.Device),
            ("lockedphone", PhoneState.Unauthorized),
            ("lostphone", PhoneState.Offline),
            ("192.168.18.29:5555", PhoneState.Device)
        }, AdbOutput.ParseDevices(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("List of devices attached\r\n\r\n")]
    [InlineData("* daemon started successfully\n")]
    public void EmptyDeviceListsHaveNoPhones(string output) => Assert.Empty(AdbOutput.ParseDevices(output));

    [Fact]
    public void DeviceLinesAcceptTabsAndWindowsNewlines()
    {
        var phone = Assert.Single(AdbOutput.ParseDevices("phone\tdevice\r\n"));
        Assert.Equal(("phone", PhoneState.Device), phone);
    }

    [Fact]
    public void TrackDevicesRetainsPartialLengthsAndLines()
    {
        var buffer = new StringBuilder();
        string frame = Frame("phone\tdevice\n");
        Assert.Empty(AdbOutput.ParseTrackDevices(buffer, frame[..2]));
        Assert.Equal(frame[..2], buffer.ToString());
        Assert.Empty(AdbOutput.ParseTrackDevices(buffer, frame[2..9]));
        Assert.Equal(frame[..9], buffer.ToString());
        var block = Assert.Single(AdbOutput.ParseTrackDevices(buffer, frame[9..]));
        Assert.Equal(("phone", PhoneState.Device), Assert.Single(block));
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TrackDevicesReadsSeveralBlocksAndAnEmptyList()
    {
        var buffer = new StringBuilder();
        string second = "locked\tunauthorized\nlost\toffline\n192.168.18.29:5555\tdevice\n";
        var blocks = AdbOutput.ParseTrackDevices(buffer, Frame("phone\tdevice\n") + "0000" + Frame(second)).ToArray();
        Assert.Equal(3, blocks.Length);
        Assert.Equal(("phone", PhoneState.Device), Assert.Single(blocks[0]));
        Assert.Empty(blocks[1]);
        Assert.Equal(new[] { ("locked", PhoneState.Unauthorized), ("lost", PhoneState.Offline), ("192.168.18.29:5555", PhoneState.Device) }, blocks[2]);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TrackDevicesRetainsTheNextUnfinishedBlock()
    {
        var buffer = new StringBuilder();
        string next = Frame("phone\toffline\n");
        Assert.Single(AdbOutput.ParseTrackDevices(buffer, "0000" + next[..7]));
        Assert.Equal(next[..7], buffer.ToString());
        Assert.Equal(("phone", PhoneState.Offline), Assert.Single(Assert.Single(AdbOutput.ParseTrackDevices(buffer, next[7..]))));
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TrackDevicesAcceptsOneCharacterAtATime()
    {
        var buffer = new StringBuilder();
        var blocks = new List<IReadOnlyList<(string Serial, PhoneState State)>>();
        foreach (char character in Frame("phone\tdevice\n") + "0000")
            blocks.AddRange(AdbOutput.ParseTrackDevices(buffer, character.ToString()));
        Assert.Equal(2, blocks.Count);
        Assert.Equal(("phone", PhoneState.Device), Assert.Single(blocks[0]));
        Assert.Empty(blocks[1]);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TrackDevicesRejectsAnInvalidFrameLength() =>
        Assert.Throws<FormatException>(() => AdbOutput.ParseTrackDevices(new StringBuilder(), "oops").ToArray());

    [Theory]
    [InlineData("AC powered")]
    [InlineData("USB powered")]
    [InlineData("Wireless powered")]
    public void AnyPoweredSourceMeansCharging(string source)
    {
        var battery = AdbOutput.ParseBattery($"Current Battery Service state:\r\n  level: 37\r\n  {source}: true\r\n  USB powered: false\r\n");
        Assert.Equal(37, battery.Level);
        Assert.True(battery.Charging);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void BatteryAcceptsBoundaryLevels(int level) =>
        Assert.Equal((level, false), AdbOutput.ParseBattery($"  level: {level}\n  AC powered: false\n  USB powered: false\n  Wireless powered: false"));

    [Theory]
    [InlineData("")]
    [InlineData("  status: 2\n  scale: 100")]
    [InlineData("  level: unknown")]
    [InlineData("  level: -1")]
    [InlineData("  level: 101")]
    public void MissingOrInvalidBatteryLevelStaysUnknown(string output)
    {
        var battery = AdbOutput.ParseBattery(output);
        Assert.Null(battery.Level);
        Assert.False(battery.Charging);
    }

    [Fact]
    public void ChargingDoesNotRequireALevel()
    {
        var battery = AdbOutput.ParseBattery("  Wireless powered: true\n");
        Assert.Null(battery.Level);
        Assert.True(battery.Charging);
    }

    [Theory]
    [InlineData("usbphone", false)]
    [InlineData("192.168.18.29:5555", true)]
    public void SerialIndicatesWirelessConnection(string serial, bool wireless) =>
        Assert.Equal(wireless, new PhoneInfo(serial, PhoneState.Device, null, null, false).IsWireless);

    private static string Frame(string devices) => devices.Length.ToString("x4", CultureInfo.InvariantCulture) + devices;
}
