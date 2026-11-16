using Microsoft.Win32;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class NotificationBannerSwitchTests : IDisposable
{
    private readonly string _root = $@"Software\VNotchTests\{Guid.NewGuid():N}";
    private readonly Dictionary<string, int?> _restore = new();

    [Fact]
    public void HideSetsShowBannerZeroAndRemembersOld()
    {
        using (var key = Registry.CurrentUser.CreateSubKey($@"{_root}\Test.App"))
            key.SetValue("ShowBanner", 1, RegistryValueKind.DWord);

        var service = new NotificationBannerSwitch(_root);
        service.Hide("Test.App", _restore);

        using var hidden = Registry.CurrentUser.OpenSubKey($@"{_root}\Test.App");
        Assert.Equal(0, hidden!.GetValue("ShowBanner"));
        Assert.Equal(RegistryValueKind.DWord, hidden.GetValueKind("ShowBanner"));
        Assert.Equal(1, _restore["Test.App"]);
        service.RestoreAll(_restore);
        Assert.Equal(1, hidden.GetValue("ShowBanner"));
        Assert.Empty(_restore);
    }

    [Fact]
    public void RestoreDeletesWhatItCreated()
    {
        var service = new NotificationBannerSwitch(_root);
        service.Hide("Test.App", _restore);
        Assert.Null(_restore["Test.App"]);
        using (var key = Registry.CurrentUser.OpenSubKey($@"{_root}\Test.App"))
            Assert.Equal(0, key!.GetValue("ShowBanner"));

        service.RestoreAll(_restore);

        using var restored = Registry.CurrentUser.OpenSubKey($@"{_root}\Test.App");
        Assert.Null(restored);
        Assert.Empty(_restore);
    }

    [Fact]
    public void HideTwiceKeepsTheFirstOriginal()
    {
        using (var key = Registry.CurrentUser.CreateSubKey($@"{_root}\Test.App"))
            key.SetValue("ShowBanner", 1, RegistryValueKind.DWord);
        var service = new NotificationBannerSwitch(_root);

        service.Hide("Test.App", _restore);
        service.Hide("Test.App", _restore);

        Assert.Equal(1, _restore["Test.App"]);
        service.RestoreAll(_restore);
        using var restored = Registry.CurrentUser.OpenSubKey($@"{_root}\Test.App");
        Assert.Equal(1, restored!.GetValue("ShowBanner"));
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
}
