using Xunit;

using TelegramPanel.Core.Services.Telegram;

namespace TelegramPanel.Web.Tests;

public sealed class TelegramClientDeviceProfileTests
{
    [Fact]
    public void StableKeyAlwaysReturnsAndroidProfile()
    {
        var first = TelegramClientDeviceProfile.ForStableKey("8613800000001");
        var second = TelegramClientDeviceProfile.ForStableKey("8613800000001");

        Assert.Equal(first, second);
        Assert.StartsWith("Android ", first.SystemVersion);
        Assert.Contains(' ', first.DeviceModel);
        Assert.NotEqual("1.31.47.0", first.AppVersion);
    }

    [Fact]
    public void ConfigValuesExposeWTelegramDeviceKeys()
    {
        var profile = TelegramClientDeviceProfile.ForStableKey("8613800000002");

        Assert.Equal(profile.AppVersion, profile.GetConfigValue("app_version"));
        Assert.Equal(profile.DeviceModel, profile.GetConfigValue("device_model"));
        Assert.Equal(profile.SystemVersion, profile.GetConfigValue("system_version"));
        Assert.Equal("en-US", profile.GetConfigValue("system_lang_code"));
        Assert.Equal("en", profile.GetConfigValue("lang_code"));
        Assert.Null(profile.GetConfigValue("api_id"));
    }
}
