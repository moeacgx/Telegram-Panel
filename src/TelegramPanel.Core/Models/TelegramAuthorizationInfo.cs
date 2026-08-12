namespace TelegramPanel.Core.Models;

/// <summary>
/// 在线设备 / 会话信息（来自 account.getAuthorizations）
/// </summary>
public record TelegramAuthorizationInfo(
    long Hash,
    bool Current,
    int ApiId,
    string? AppName,
    string? AppVersion,
    string? DeviceModel,
    string? Platform,
    string? SystemVersion,
    string? Ip,
    string? Country,
    string? Region,
    DateTime? CreatedAtUtc,
    DateTime? LastActiveAtUtc
)
{
    public string ApiFamily => ApiId switch
    {
        6 => "官方 Android API",
        2834 => "官方 macOS API",
        2040 => "官方 Desktop API",
        _ => "自建/第三方 API"
    };

    public string ApiDisplayName
    {
        get
        {
            var app = string.IsNullOrWhiteSpace(AppName) ? "UnknownApp" : AppName.Trim();
            var platform = string.IsNullOrWhiteSpace(Platform) ? null : Platform.Trim();
            return platform is null ? app : $"{app} / {platform}";
        }
    }

    public string DeviceDisplayName => string.IsNullOrWhiteSpace(DeviceModel) ? "UnknownDevice" : DeviceModel.Trim();

    public string Title => $"{(string.IsNullOrWhiteSpace(AppName) ? "UnknownApp" : AppName.Trim())} - {DeviceDisplayName}";

    public string ApiDescription => ApiId switch
    {
        6 => "Telegram 会显示为 Telegram Android；设备画像应使用 Android 手机与 Android 系统。",
        2834 => "Telegram 会显示为 Telegram macOS；设备画像应使用 Mac 设备与 macOS 系统。",
        2040 => "Telegram 会显示为 Telegram Desktop；设备画像应使用 PC 与 Windows/Linux 系统。",
        _ => "应用名和平台来自 my.telegram.org 上该 ApiId 的注册信息；面板只能控制设备型号、系统版本和 App 版本。"
    };
}

