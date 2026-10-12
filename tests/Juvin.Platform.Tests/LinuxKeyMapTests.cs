using System.Runtime.Versioning;
using Juvin.Core.Hotkeys;
using Juvin.Platform.Linux.Hotkeys.Evdev;

namespace Juvin.Platform.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxKeyMapTests
{
    [LinuxFact]
    public void KeyCodes_MapEveryChordKeyToADistinctCode()
    {
        var codes = LinuxKeyMap.KeyCodes;

        Assert.Equal(KeyChord.KeyNames.Order(), codes.Keys.Order());
        Assert.Equal(codes.Count, codes.Values.Distinct().Count());
        Assert.All(codes.Values, code => Assert.Equal(ChordModifiers.None, LinuxKeyMap.ModifierOf(code)));
    }

    [Theory]
    [InlineData("F", 33)]
    [InlineData("1", 2)]
    [InlineData("0", 11)]
    [InlineData("F12", 88)]
    [InlineData("F13", 183)]
    [InlineData("Quote", 40)]
    [InlineData("Delete", 111)]
    public void KeyCodes_FollowTheKernelHeader(string key, int expected)
    {
        Assert.Equal(expected, LinuxKeyMap.KeyCodes[key]);
    }

    [Theory]
    [InlineData(29, ChordModifiers.Ctrl)]
    [InlineData(97, ChordModifiers.Ctrl)]
    [InlineData(42, ChordModifiers.Shift)]
    [InlineData(54, ChordModifiers.Shift)]
    [InlineData(56, ChordModifiers.Alt)]
    [InlineData(100, ChordModifiers.Alt)]
    [InlineData(125, ChordModifiers.Super)]
    [InlineData(126, ChordModifiers.Super)]
    [InlineData(30, ChordModifiers.None)]
    public void ModifierOf_TreatsBothSidesAlike(int code, ChordModifiers expected)
    {
        Assert.Equal(expected, LinuxKeyMap.ModifierOf(code));
    }

    [Theory]
    [InlineData("Juvin virtual keyboard", true)]
    [InlineData("AT Translated Set 2 keyboard", false)]
    public void KeyboardDiscovery_SkipsJuvinsOwnDevices(string name, bool excluded)
    {
        Assert.Equal(excluded, KeyboardDeviceDiscovery.IsExcludedByName(name));
    }

    [Theory]
    [InlineData(new[] { "input:x:104:alice" }, "Groups:\t4 104 1000", true)]
    [InlineData(new[] { "wheel:x:10:", "input:x:104:" }, "Groups:\t4 1000", false)]
    [InlineData(new[] { "wheel:x:10:" }, "Groups:\t4 1000", null)]
    public void InputGroupCheck_ReadsTheProcessGroups(string[] groupFile, string status, bool? expected)
    {
        Assert.Equal(expected, InputGroupCheck.IsInInputGroup(groupFile, ["Name:\tjuvin", status]));
    }
}
