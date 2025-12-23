namespace TelegramPanel.Data.Entities;

/// <summary>
/// Account 实体扩展方法
/// </summary>
public static class AccountExtensions
{
    /// <summary>
    /// 获取风控检查用的参考时间（优先 LastLoginAt，其次 CreatedAt/LastSyncAt 作为估算兜底）
    /// </summary>
    public static DateTime? GetRiskReferenceAtUtc(this Account account)
    {
        if (account.LastLoginAt != null)
            return account.LastLoginAt.Value;

        // 兼容历史数据：早期导入的账号可能没有写入 LastLoginAt
        if (account.CreatedAt != default)
            return account.CreatedAt;

        if (account.LastSyncAt != default)
            return account.LastSyncAt;

        return null;
    }

    /// <summary>
    /// 风控参考时间是否为估算值（非真实登录时间）
    /// </summary>
    public static bool IsRiskReferenceEstimated(this Account account)
        => account.LastLoginAt == null && account.GetRiskReferenceAtUtc() != null;

    /// <summary>
    /// 获取风控检查用的参考小时数
    /// </summary>
    public static double? GetRiskReferenceHours(this Account account)
    {
        var at = account.GetRiskReferenceAtUtc();
        if (at == null)
            return null;

        return (DateTime.UtcNow - at.Value).TotalHours;
    }

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
