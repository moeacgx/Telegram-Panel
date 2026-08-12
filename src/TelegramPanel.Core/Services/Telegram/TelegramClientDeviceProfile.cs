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

    public static TelegramClientDeviceProfile ForStableKey(string stableKey)
    {
        if (string.IsNullOrWhiteSpace(stableKey))
            stableKey = "telegram-panel";

        var hash = StableHash(stableKey.Trim());
        var index = hash % AndroidProfiles.Length;
        return AndroidProfiles[index];
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
