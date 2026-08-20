using Microsoft.Extensions.Configuration;

namespace TelegramPanel.Core.Services.Telegram;

public sealed record TelegramDeviceProfileDefinition(
    string Key,
    string Name,
    string Family,
    string AppVersion,
    string DeviceModel,
    string SystemVersion,
    string SystemLangCode = "en-US",
    string LangCode = "en",
    bool Enabled = true,
    bool BuiltIn = false,
    string? Notes = null)
{
    public TelegramClientDeviceProfile ToClientProfile() => new(
        AppVersion,
        DeviceModel,
        SystemVersion,
        SystemLangCode,
        LangCode);
}

public static class TelegramDeviceProfileCatalog
{
    public const string DefaultProfileKey = "android-default";
    public const string RandomProfileKey = "random";

    private static readonly TelegramDeviceProfileDefinition[] BuiltInProfiles =
    {
        new("android-default", "Android 默认指纹", "android", "12.7.3", "Samsung SM-G991B", "Android 14", BuiltIn: true, Notes: "适合统一使用安卓设备画像的账号。"),
        new("ios-default", "iOS 默认指纹", "ios", "10.15.0", "iPhone 15", "iOS 17.5", BuiltIn: true, Notes: "iOS 设备画像；不会改变当前 API 配置。"),
        new("macos-default", "macOS 默认指纹", "macos", "10.15.4", "MacBook Pro", "macOS 14.6", BuiltIn: true),
        new("windows-default", "Windows 默认指纹", "windows", "5.16.4 x64", "PC 64bit", "Windows 11", BuiltIn: true),
    };

    public static IReadOnlyList<TelegramDeviceProfileDefinition> ReadProfiles(IConfiguration configuration)
    {
        var configured = new List<TelegramDeviceProfileDefinition>();
        foreach (var child in configuration.GetSection("Telegram:DeviceProfiles").GetChildren())
        {
            var key = NormalizeKey(child["Key"]);
            if (string.IsNullOrWhiteSpace(key) || IsRandomProfileKey(key))
                continue;

            var fallback = BuiltInProfiles.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            configured.Add(new TelegramDeviceProfileDefinition(
                key,
                NormalizeText(child["Name"], fallback?.Name ?? key),
                NormalizeText(child["Family"], fallback?.Family ?? "custom"),
                NormalizeText(child["AppVersion"], fallback?.AppVersion ?? "5.16.4"),
                NormalizeText(child["DeviceModel"], fallback?.DeviceModel ?? "PC 64bit"),
                NormalizeText(child["SystemVersion"], fallback?.SystemVersion ?? "Windows 11"),
                NormalizeText(child["SystemLangCode"], fallback?.SystemLangCode ?? "en-US"),
                NormalizeText(child["LangCode"], fallback?.LangCode ?? "en"),
                !bool.TryParse(child["Enabled"], out var enabled) || enabled,
                fallback?.BuiltIn == true,
                NormalizeNullable(child["Notes"])));
        }

        foreach (var builtIn in BuiltInProfiles)
        {
            if (configured.All(x => !string.Equals(x.Key, builtIn.Key, StringComparison.OrdinalIgnoreCase)))
                configured.Insert(0, builtIn);
        }

        return configured
            .Where(x => x.Enabled)
            .OrderBy(x => x.BuiltIn ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ResolveDefaultKey(IConfiguration configuration)
    {
        var requested = NormalizeKey(configuration["Telegram:DefaultDeviceProfileKey"]);
        if (string.IsNullOrWhiteSpace(requested))
            return DefaultProfileKey;
        return IsRandomProfileKey(requested) ? RandomProfileKey : requested;
    }

    public static TelegramDeviceProfileDefinition? Find(IConfiguration configuration, string? key)
    {
        var normalized = NormalizeKey(key);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = ResolveDefaultKey(configuration);
        if (IsRandomProfileKey(normalized))
            return null;
        return ReadProfiles(configuration).FirstOrDefault(x => string.Equals(x.Key, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static TelegramClientDeviceProfile ResolveClientProfile(
        IConfiguration configuration,
        int apiId,
        string? profileKey,
        string stableKey)
    {
        var definition = Find(configuration, profileKey);
        return definition?.ToClientProfile() ?? TelegramClientDeviceProfile.ForStableKey(apiId, stableKey);
    }

    public static string NormalizeKey(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsRandomProfileKey(string? value) =>
        string.Equals(NormalizeKey(value), RandomProfileKey, StringComparison.Ordinal);

    public static bool TryNormalizeSelectableKey(IConfiguration configuration, string? key, out string? normalizedKey)
    {
        var requested = NormalizeKey(key);
        if (string.IsNullOrWhiteSpace(requested))
        {
            normalizedKey = null;
            return true;
        }

        if (IsRandomProfileKey(requested))
        {
            normalizedKey = RandomProfileKey;
            return true;
        }

        var profile = Find(configuration, requested);
        if (profile == null)
        {
            normalizedKey = null;
            return false;
        }

        normalizedKey = profile.Key;
        return true;
    }

    private static string NormalizeText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
