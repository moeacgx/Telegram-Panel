using Xunit;

using TelegramPanel.Core.Services.Telegram;

namespace TelegramPanel.Web.Tests;

public sealed class TelegramClientDeviceProfileTests
{
    [Fact]
    public void StableKeyAlwaysReturnsAndroidProfileForAndroidApi()
    {
        var first = TelegramClientDeviceProfile.ForStableKey(6, "8613800000001");
        var second = TelegramClientDeviceProfile.ForStableKey(6, "8613800000001");

        Assert.Equal(first, second);
        Assert.StartsWith("Android ", first.SystemVersion);
        Assert.DoesNotContain("macOS", first.DeviceModel, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("1.31.47.0", first.AppVersion);
    }

    [Fact]
    public void ApiFamilyKeepsApplicationAndSystemConsistent()
    {
        var android = TelegramClientDeviceProfile.ForStableKey(6, "same-account");
        var macos = TelegramClientDeviceProfile.ForStableKey(2834, "same-account");
        var desktop = TelegramClientDeviceProfile.ForStableKey(2040, "same-account");

        Assert.StartsWith("Android ", android.SystemVersion);
        Assert.DoesNotContain("macOS", android.DeviceModel, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("macOS ", macos.SystemVersion);
        Assert.DoesNotContain("Android", macos.SystemVersion, StringComparison.OrdinalIgnoreCase);
        Assert.True(desktop.SystemVersion.StartsWith("Windows ", StringComparison.Ordinal) || desktop.SystemVersion.StartsWith("GNU/Linux ", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValuesExposeWTelegramDeviceKeys()
    {
        var profile = TelegramClientDeviceProfile.ForStableKey(6, "8613800000002");

        Assert.Equal(profile.AppVersion, profile.GetConfigValue("app_version"));
        Assert.Equal(profile.DeviceModel, profile.GetConfigValue("device_model"));
        Assert.Equal(profile.SystemVersion, profile.GetConfigValue("system_version"));
        Assert.Equal("en-US", profile.GetConfigValue("system_lang_code"));
        Assert.Equal("en", profile.GetConfigValue("lang_code"));
        Assert.Null(profile.GetConfigValue("api_id"));
    }
}
