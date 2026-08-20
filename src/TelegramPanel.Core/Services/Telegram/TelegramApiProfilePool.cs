using System.Threading;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Utils;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Telegram;

public sealed record TelegramApiCredentials(int ApiId, string ApiHash, string? ProfileName = null);

public sealed record TelegramApiProfile(
    string Name,
    int ApiId,
    string ApiHash,
    bool Enabled = true,
    int Weight = 1,
    string? Notes = null);

public sealed class TelegramApiProfilePool
{
    public const int OfficialAndroidApiId = 6;
    public const string OfficialAndroidApiHash = "eb06d4abfb49dc3eeb1aeb98ae0f581e";
    public const string OfficialAndroidApiName = "Telegram 官方 Android API";

    private const int MaxWeight = 1000;
    private readonly IConfiguration _configuration;
    private int _roundRobinCursor = -1;


    public TelegramApiProfilePool(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public IReadOnlyList<TelegramApiProfile> GetConfiguredProfiles() => ReadConfiguredProfiles(_configuration);

    public IReadOnlyList<TelegramApiProfile> GetEnabledProfiles() => GetEnabledPoolProfiles(_configuration);

    public bool HasUsableApi() => GetEnabledPoolProfiles(_configuration).Count > 0;

    public Task<TelegramApiCredentials> SelectForAccountAsync(
        Account? existingAccount,
        AccountManagementService accountManagement)
    {
        return Task.FromResult(TryGetAccountCredentials(existingAccount, out var existingCredentials)
            ? existingCredentials
            : SelectForNewAccount(Array.Empty<Account>()));
    }

    public Task<TelegramApiCredentials> SelectForNewAccountAsync(AccountManagementService accountManagement)
    {
        return Task.FromResult(SelectForNewAccount(Array.Empty<Account>()));
    }

    public TelegramApiCredentials SelectForNewAccount(IReadOnlyList<Account> existingAccounts)
    {
        var profiles = GetEnabledPoolProfiles(_configuration);
        if (profiles.Count == 0)
            throw new InvalidOperationException("请先在【系统设置】中启用内置官方 API 或至少一个 API 池配置");

        var weightedProfiles = new List<TelegramApiProfile>();
        foreach (var profile in profiles)
        {
            for (var i = 0; i < profile.Weight; i++)
                weightedProfiles.Add(profile);
        }

        var selected = weightedProfiles[GetNextRoundRobinIndex(weightedProfiles.Count)];
        return new TelegramApiCredentials(selected.ApiId, selected.ApiHash, selected.Name);
    }

    public static IReadOnlyList<TelegramApiProfile> ReadConfiguredProfiles(IConfiguration configuration)
    {
        var profiles = new List<TelegramApiProfile>();
        var index = 0;
        foreach (var child in configuration.GetSection("Telegram:ApiProfiles").GetChildren())
        {
            var name = (child["Name"] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = $"API {index + 1}";

            var apiIdText = (child["ApiId"] ?? string.Empty).Trim();
            var apiHash = (child["ApiHash"] ?? string.Empty).Trim();
            var enabledText = (child["Enabled"] ?? string.Empty).Trim();
            var weightText = (child["Weight"] ?? string.Empty).Trim();
            var notes = (child["Notes"] ?? string.Empty).Trim();

            profiles.Add(new TelegramApiProfile(
                name,
                int.TryParse(apiIdText, out var apiId) ? apiId : 0,
                apiHash,
                string.IsNullOrWhiteSpace(enabledText) || bool.TryParse(enabledText, out var enabled) && enabled,
                int.TryParse(weightText, out var weight) ? weight : 1,
                string.IsNullOrWhiteSpace(notes) ? null : notes));
            index++;
        }

        return profiles;
    }


    public static IReadOnlyList<TelegramApiProfile> GetEnabledProfiles(IConfiguration configuration)
    {
        return ReadConfiguredProfiles(configuration)
            .Where(profile => profile.Enabled)
            .Select(profile => TryNormalize(profile, out var normalized) ? normalized : null)
            .Where(profile => profile != null)
            .Cast<TelegramApiProfile>()
            .ToList();
    }

    public static IReadOnlyList<TelegramApiProfile> GetEnabledPoolProfiles(IConfiguration configuration)
    {
        var profiles = new List<TelegramApiProfile>();
        if (IsOfficialApiEnabled(configuration))
        {
            profiles.Add(new TelegramApiProfile(
                OfficialAndroidApiName,
                OfficialAndroidApiId,
                OfficialAndroidApiHash,
                Enabled: true,
                Weight: 1));
        }

        if (TryGetCustomDefault(configuration, out var customDefault)
            && !profiles.Any(profile => SameCredentials(profile, customDefault)))
        {
            profiles.Add(new TelegramApiProfile(
                customDefault.ProfileName ?? "旧版单 API",
                customDefault.ApiId,
                customDefault.ApiHash,
                Enabled: true,
                Weight: 1));
        }

        foreach (var profile in GetEnabledProfiles(configuration))
        {
            var credentials = new TelegramApiCredentials(profile.ApiId, profile.ApiHash, profile.Name);
            if (!profiles.Any(existing => SameCredentials(existing, credentials)))
                profiles.Add(profile);
        }

        return profiles;
    }

    public static bool IsOfficialApiEnabled(IConfiguration configuration)
    {
        var text = (configuration["Telegram:OfficialApiEnabled"] ?? string.Empty).Trim();
        return !bool.TryParse(text, out var enabled) || enabled;
    }


    public static bool TryGetAccountCredentials(Account? account, out TelegramApiCredentials credentials)
    {
        credentials = default!;
        if (account == null)
            return false;

        if (!TryNormalizeCredentials(account.ApiId, account.ApiHash, out var normalizedHash, out _))
            return false;

        credentials = new TelegramApiCredentials(account.ApiId, normalizedHash);
        return true;
    }

    public static bool TryGetGlobalFallback(IConfiguration configuration, out TelegramApiCredentials credentials)
    {
        if (TryGetCustomDefault(configuration, out credentials))
            return true;

        if (IsOfficialApiEnabled(configuration))
        {
            credentials = new TelegramApiCredentials(OfficialAndroidApiId, OfficialAndroidApiHash, OfficialAndroidApiName);
            return true;
        }

        credentials = default!;
        return false;
    }

    private static bool TryGetCustomDefault(IConfiguration configuration, out TelegramApiCredentials credentials)
    {
        credentials = default!;
        var apiIdText = (configuration["Telegram:ApiId"] ?? string.Empty).Trim();
        var apiHashText = (configuration["Telegram:ApiHash"] ?? string.Empty).Trim();
        if ((string.IsNullOrWhiteSpace(apiIdText) || apiIdText == "0") && string.IsNullOrWhiteSpace(apiHashText))
            return false;

        if (!int.TryParse(apiIdText, out var apiId)
            || !TryNormalizeCredentials(apiId, apiHashText, out var apiHash, out _))
            return false;

        credentials = new TelegramApiCredentials(apiId, apiHash, "旧版单 API");
        return true;
    }

    public static bool TrySelectDefault(IConfiguration configuration, out TelegramApiCredentials credentials, out string error)
    {
        var profiles = GetEnabledPoolProfiles(configuration);
        if (profiles.Count > 0)
        {
            var profile = profiles[0];
            credentials = new TelegramApiCredentials(profile.ApiId, profile.ApiHash, profile.Name);
            error = string.Empty;
            return true;
        }

        credentials = default!;
        error = "请先在【系统设置】中启用内置官方 API 或至少一个 API 池配置";
        return false;
    }

    public static bool TryNormalizeCredentials(int apiId, string? apiHash, out string normalizedApiHash, out string? reason)
    {
        normalizedApiHash = string.Empty;
        if (apiId <= 0)
        {
            reason = "ApiId 无效（必须为正整数）";
            return false;
        }

        if (!TelegramApiConfigValidator.TryNormalizeApiHash(apiHash, out normalizedApiHash, out reason))
            return false;

        reason = null;
        return true;
    }

    private static bool TryNormalize(TelegramApiProfile profile, out TelegramApiProfile normalized)
    {
        normalized = profile;
        if (!TryNormalizeCredentials(profile.ApiId, profile.ApiHash, out var apiHash, out _))
            return false;

        normalized = profile with
        {
            Name = string.IsNullOrWhiteSpace(profile.Name) ? $"API {profile.ApiId}" : profile.Name.Trim(),
            ApiHash = apiHash,
            Weight = Math.Clamp(profile.Weight, 1, MaxWeight),
            Notes = string.IsNullOrWhiteSpace(profile.Notes) ? null : profile.Notes.Trim()
        };
        return true;
    }

    private int GetNextRoundRobinIndex(int count)
    {
        var next = Interlocked.Increment(ref _roundRobinCursor);
        return (int)((uint)next % (uint)count);
    }

    private static bool SameCredentials(TelegramApiProfile profile, TelegramApiCredentials credentials) =>
        profile.ApiId == credentials.ApiId
        && string.Equals(profile.ApiHash, credentials.ApiHash, StringComparison.OrdinalIgnoreCase);

    private static bool SameCredentials(TelegramApiProfile profile, TelegramApiProfile other) =>
        profile.ApiId == other.ApiId
        && string.Equals(profile.ApiHash, other.ApiHash, StringComparison.OrdinalIgnoreCase);
}
