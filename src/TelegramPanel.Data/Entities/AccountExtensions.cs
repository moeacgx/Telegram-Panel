namespace TelegramPanel.Data.Entities;

/// <summary>
/// Account 实体扩展方法
/// </summary>
public static class AccountExtensions
{
    /// <summary>
    /// 获取登录小时数的格式化字符串
    /// </summary>
    public static string GetLoginHoursFormatted(this Account account)
    {
        if (account.LastLoginAt == null)
            return "未知";

        var hours = (DateTime.UtcNow - account.LastLoginAt.Value).TotalHours;
        return hours.ToString("F1");
    }

    /// <summary>
    /// 获取登录小时数
    /// </summary>
    public static double? GetLoginHours(this Account account)
    {
        if (account.LastLoginAt == null)
            return null;

        return (DateTime.UtcNow - account.LastLoginAt.Value).TotalHours;
    }
}
