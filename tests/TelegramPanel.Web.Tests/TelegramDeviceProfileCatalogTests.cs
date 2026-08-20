using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Services.Telegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class TelegramDeviceProfileCatalogTests
{
    [Fact]
    public void BuiltInProfilesProvideStableDefaultAndConfigValues()
    {
        var configuration = new ConfigurationBuilder().Build();

        var profile = TelegramDeviceProfileCatalog.Find(configuration, null);

        Assert.NotNull(profile);
        Assert.Equal(TelegramDeviceProfileCatalog.DefaultProfileKey, profile!.Key);
        Assert.Equal(profile.AppVersion, profile.ToClientProfile().GetConfigValue("app_version"));
        Assert.Equal(profile.DeviceModel, profile.ToClientProfile().GetConfigValue("device_model"));
    }

    [Fact]
    public void RandomDefaultKeyUsesStableGeneratedProfile()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:DefaultDeviceProfileKey"] = "random"
            })
            .Build();

        var profile = TelegramDeviceProfileCatalog.ResolveClientProfile(
            configuration,
            6,
            null,
            "stable-key");

        Assert.Equal(TelegramDeviceProfileCatalog.RandomProfileKey, TelegramDeviceProfileCatalog.ResolveDefaultKey(configuration));
        Assert.Equal(TelegramClientDeviceProfile.ForStableKey(6, "stable-key"), profile);
    }

    [Fact]
    public void RandomKeyIsSelectableButReservedFromConfiguredCatalog()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:DeviceProfiles:0:Key"] = "random",
                ["Telegram:DeviceProfiles:0:Name"] = "Should Not Appear",
                ["Telegram:DeviceProfiles:0:Enabled"] = "true"
            })
            .Build();

        var profiles = TelegramDeviceProfileCatalog.ReadProfiles(configuration);

        Assert.True(TelegramDeviceProfileCatalog.TryNormalizeSelectableKey(configuration, "random", out var normalizedKey));
        Assert.Equal(TelegramDeviceProfileCatalog.RandomProfileKey, normalizedKey);
        Assert.DoesNotContain(profiles, profile => profile.Key == TelegramDeviceProfileCatalog.RandomProfileKey);
    }

    [Fact]
    public void ConfiguredProfileOverridesBuiltInValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:DefaultDeviceProfileKey"] = "custom-android",
                ["Telegram:DeviceProfiles:0:Key"] = "custom-android",
                ["Telegram:DeviceProfiles:0:Name"] = "Custom Android",
                ["Telegram:DeviceProfiles:0:Family"] = "android",
                ["Telegram:DeviceProfiles:0:AppVersion"] = "99.1",
                ["Telegram:DeviceProfiles:0:DeviceModel"] = "Custom Phone",
                ["Telegram:DeviceProfiles:0:SystemVersion"] = "Android 99",
                ["Telegram:DeviceProfiles:0:SystemLangCode"] = "zh-CN",
                ["Telegram:DeviceProfiles:0:LangCode"] = "zh",
                ["Telegram:DeviceProfiles:0:Enabled"] = "true"
            })
            .Build();

        var profile = TelegramDeviceProfileCatalog.ResolveClientProfile(
            configuration,
            6,
            "custom-android",
            "stable-key");

        Assert.Equal("99.1", profile.AppVersion);
        Assert.Equal("Custom Phone", profile.DeviceModel);
        Assert.Equal("Android 99", profile.SystemVersion);
        Assert.Equal("zh-CN", profile.SystemLangCode);
        Assert.Equal("zh", profile.LangCode);
    }

    [Fact]
    public void DisabledOrUnknownProfileFallsBackToDefaultStableProfile()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:DeviceProfiles:0:Key"] = "disabled",
                ["Telegram:DeviceProfiles:0:Enabled"] = "false"
            })
            .Build();

        var profile = TelegramDeviceProfileCatalog.ResolveClientProfile(
            configuration,
            6,
            "disabled",
            "stable-key");

        Assert.False(string.IsNullOrWhiteSpace(profile.DeviceModel));
        Assert.False(string.IsNullOrWhiteSpace(profile.SystemVersion));
    }
}
