using System;
using System.Linq;
using VNotch.Services.ControlCenter;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class OutputDevicesTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Lautsprecher (MOONDROP Rays)", "Rays")]
    [InlineData("Kopfhörer (WF-1000XM6)", "XM6")]
    [InlineData("Headphones (Sony WH-1000XM5)", "XM5")]
    [InlineData("Lautsprecher", "Lautsprecher")]
    public void ShortNames(string friendly, string expected) => Assert.Equal(expected, OutputDevices.Nickname(friendly));

    [Fact]
    public void LaptopJackAndSpeakersGetTheirOwnNames()
    {
        Assert.NotEqual(OutputDevices.Nickname("Kopfhörer (Realtek(R) Audio)"), OutputDevices.Nickname("Lautsprecher (Realtek(R) Audio)"));
    }

    [Theory]
    [InlineData("System (Elgato Virtual Audio)")]
    [InlineData("Headset (WF-1000XM6 Hands-Free)")]
    [InlineData("Steam Streaming Speakers")]
    public void VirtualAndHandsFreeAreLeftOut(string friendly) => Assert.True(OutputDevices.IsVirtual(friendly));

    [Fact]
    public void ListsThisMachinesSpeakers()
    {
        foreach (var o in OutputDevices.List(Array.Empty<string>()))
            output.WriteLine($"{(o.IsDefault ? "*" : " ")} {o.Name}  <-  {o.FriendlyName}");
    }
}
