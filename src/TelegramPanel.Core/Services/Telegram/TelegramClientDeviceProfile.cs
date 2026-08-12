namespace TelegramPanel.Core.Services.Telegram;

public readonly record struct TelegramClientDeviceProfile(
    string AppVersion,
    string DeviceModel,
    string SystemVersion,
    string SystemLangCode,
    string LangCode)
{
    private static readonly TelegramClientDeviceProfile[] AndroidProfiles =
    {
        new("12.7.3", "OnePlus OnePlus 7T", "Android 12", "en-US", "en"),
        new("12.7.3", "Samsung SM-G991B", "Android 14", "en-US", "en"),
        new("12.7.2", "Google Pixel 7", "Android 14", "en-US", "en"),
        new("12.6.4", "Xiaomi 2210132C", "Android 13", "en-US", "en"),
        new("12.5.3", "OPPO CPH2451", "Android 13", "en-US", "en"),
        new("12.4.1", "vivo V2244A", "Android 14", "en-US", "en"),
    };

    private static readonly TelegramClientDeviceProfile[] MacOsProfiles =
    {
        new("10.15.4", "MacBook Pro", "macOS 14.6", "en-US", "en"),
        new("10.15.4", "MacBook Air", "macOS 13.6", "en-US", "en"),
        new("10.14.5", "iMac", "macOS 14.5", "en-US", "en"),
    };

    private static readonly TelegramClientDeviceProfile[] DesktopProfiles =
    {
        new("5.16.4 x64", "PC 64bit", "Windows 11", "en-US", "en"),
        new("5.16.4 x64", "PC 64bit", "Windows 10", "en-US", "en"),
        new("5.15.4 x64", "PC 64bit", "GNU/Linux 12 (bookworm)", "en-US", "en"),
    };

    public static TelegramClientDeviceProfile ForStableKey(int apiId, string stableKey)
    {
        var profiles = GetProfilesForApiId(apiId);
        return PickProfile(profiles, stableKey);
    }

    public static TelegramClientDeviceProfile ForStableKey(string stableKey) => PickProfile(AndroidProfiles, stableKey);

    private static TelegramClientDeviceProfile[] GetProfilesForApiId(int apiId) => apiId switch
    {
        6 => AndroidProfiles,
        2834 => MacOsProfiles,
        2040 => DesktopProfiles,
        _ => AndroidProfiles
    };

    private static TelegramClientDeviceProfile PickProfile(TelegramClientDeviceProfile[] profiles, string stableKey)
    {
        if (string.IsNullOrWhiteSpace(stableKey))
            stableKey = "telegram-panel";

        var hash = StableHash(stableKey.Trim());
        var index = hash % profiles.Length;
        return profiles[index];
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619;
            }
            return (int)(hash & 0x7fffffff);
        }
    }

    public string? GetConfigValue(string what) => what switch
    {
        "app_version" => AppVersion,
        "device_model" => DeviceModel,
        "system_version" => SystemVersion,
        "system_lang_code" => SystemLangCode,
        "lang_code" => LangCode,
        _ => null
    };
}
