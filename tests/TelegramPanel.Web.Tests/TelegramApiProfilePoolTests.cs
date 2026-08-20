using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Web.Api;
using TelegramPanel.Data.Entities;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class TelegramApiProfilePoolTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccc";

    [Fact]
    public void SelectForNewAccount_RoundRobinsAcrossEnabledPool()
    {
        var pool = CreatePool(true, ("primary", 1001, HashA, true, 1), ("secondary", 1002, HashB, true, 1));

        var first = pool.SelectForNewAccount(Array.Empty<Account>());
        var second = pool.SelectForNewAccount(Array.Empty<Account>());
        var third = pool.SelectForNewAccount(Array.Empty<Account>());

        Assert.Equal(TelegramApiProfilePool.OfficialAndroidApiId, first.ApiId);
        Assert.Equal(1001, second.ApiId);
        Assert.Equal(1002, third.ApiId);
    }

    [Fact]
    public void SelectForNewAccount_SkipsDisabledProfiles()
    {
        var pool = CreatePool(("disabled", 1001, HashA, false, 1), ("enabled", 1002, HashB, true, 1));

        var selected = pool.SelectForNewAccount(Array.Empty<Account>());

        Assert.Equal(1002, selected.ApiId);
        Assert.Equal(HashB, selected.ApiHash);
    }

    [Fact]
    public void SelectForAccount_PreservesExistingAccountApi()
    {
        var existing = new Account { ApiId = 2001, ApiHash = HashC };

        var preserved = TelegramApiProfilePool.TryGetAccountCredentials(existing, out var credentials);

        Assert.True(preserved);
        Assert.Equal(2001, credentials.ApiId);
        Assert.Equal(HashC, credentials.ApiHash);
    }

    [Fact]
    public void SelectForNewAccount_IncludesLegacySingleApiInPool()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:OfficialApiEnabled"] = "false",
                ["Telegram:ApiId"] = "3001",
                ["Telegram:ApiHash"] = HashA
            })
            .Build();
        var pool = new TelegramApiProfilePool(configuration);

        var selected = pool.SelectForNewAccount(Array.Empty<Account>());

        Assert.Equal(3001, selected.ApiId);
        Assert.Equal(HashA, selected.ApiHash);
        Assert.Equal("旧版单 API", selected.ProfileName);
    }

    [Fact]
    public void SelectForNewAccount_UsesBuiltInOfficialAndroidApiWhenUnset()
    {
        var configuration = new ConfigurationBuilder().Build();
        var pool = new TelegramApiProfilePool(configuration);

        var selected = pool.SelectForNewAccount(Array.Empty<Account>());

        Assert.Equal(TelegramApiProfilePool.OfficialAndroidApiId, selected.ApiId);
        Assert.Equal(TelegramApiProfilePool.OfficialAndroidApiHash, selected.ApiHash);
        Assert.Equal(TelegramApiProfilePool.OfficialAndroidApiName, selected.ProfileName);
    }

    [Fact]
    public void SelectForNewAccount_ThrowsWhenOfficialAndProfilesDisabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:OfficialApiEnabled"] = "false"
            })
            .Build();
        var pool = new TelegramApiProfilePool(configuration);

        var error = Assert.Throws<InvalidOperationException>(() => pool.SelectForNewAccount(Array.Empty<Account>()));

        Assert.Contains("启用内置官方 API", error.Message);
    }

    [Fact]
    public void ReadTelegramApiRuntimeStatus_ExposesBuiltInOfficialFallback()
    {
        var configuration = new ConfigurationBuilder().Build();

        var status = PanelAdminApiEndpoints.ReadTelegramApiRuntimeStatus(configuration);

        Assert.True(status.HasUsableApi);
        Assert.Equal(TelegramApiProfilePool.OfficialAndroidApiId.ToString(), status.EffectiveApiId);
        Assert.Equal("built_in_official", status.EffectiveApiSource);
        Assert.Equal(TelegramApiProfilePool.OfficialAndroidApiName, status.EffectiveApiName);
    }

    [Fact]
    public void ReadTelegramApiRuntimeStatus_UsesEnabledPoolTopItem()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:OfficialApiEnabled"] = "false",
                ["Telegram:ApiProfiles:0:Name"] = "pool-a",
                ["Telegram:ApiProfiles:0:ApiId"] = "4001",
                ["Telegram:ApiProfiles:0:ApiHash"] = HashA,
                ["Telegram:ApiProfiles:0:Enabled"] = "true"
            })
            .Build();

        var status = PanelAdminApiEndpoints.ReadTelegramApiRuntimeStatus(configuration);

        Assert.True(status.HasUsableApi);
        Assert.Equal("4001", status.EffectiveApiId);
        Assert.Equal("api_profile", status.EffectiveApiSource);
        Assert.Equal("pool-a", status.EffectiveApiName);
    }

    [Fact]
    public void NormalizeTelegramApiDefaultInput_TreatsZeroWithoutHashAsProfileOnly()
    {
        var result = PanelAdminApiEndpoints.NormalizeTelegramApiDefaultInput("0", " ");

        Assert.False(result.HasDefaultApi);
        Assert.Equal(string.Empty, result.ApiId);
        Assert.Equal(string.Empty, result.ApiHash);
    }


    private static TelegramApiProfilePool CreatePool(params (string Name, int ApiId, string ApiHash, bool Enabled, int Weight)[] profiles)
    {
        return CreatePool(false, profiles);
    }

    private static TelegramApiProfilePool CreatePool(bool officialApiEnabled, params (string Name, int ApiId, string ApiHash, bool Enabled, int Weight)[] profiles)
    {
        var values = new Dictionary<string, string?>
        {
            ["Telegram:OfficialApiEnabled"] = officialApiEnabled.ToString()
        };
        for (var i = 0; i < profiles.Length; i++)
        {
            var profile = profiles[i];
            values[$"Telegram:ApiProfiles:{i}:Name"] = profile.Name;
            values[$"Telegram:ApiProfiles:{i}:ApiId"] = profile.ApiId.ToString();
            values[$"Telegram:ApiProfiles:{i}:ApiHash"] = profile.ApiHash;
            values[$"Telegram:ApiProfiles:{i}:Enabled"] = profile.Enabled.ToString();
            values[$"Telegram:ApiProfiles:{i}:Weight"] = profile.Weight.ToString();
        }

        return new TelegramApiProfilePool(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
}
