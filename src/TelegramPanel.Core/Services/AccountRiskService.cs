using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 账号风控检查服务
/// </summary>
public class AccountRiskService
{
    /// <summary>
    /// 风控阈值：登录后需满 24 小时才能进行敏感操作
    /// </summary>
    private const double RISK_THRESHOLD_HOURS = 24.0;

    /// <summary>
    /// 检查单个账号的登录时长是否满足风控要求
    /// </summary>
    public RiskCheckResult CheckLoginDuration(Account account)
    {
        var referenceAt = account.GetRiskReferenceAtUtc();
        var loginHours = account.GetRiskReferenceHours();

        if (referenceAt == null || loginHours == null)
        {
            // 兜底：完全缺少可参考的时间，视为高风险
            return new RiskCheckResult
            {
                IsRisky = true,
                Message = "账号未记录登录时间",
                DetailedMessage = "该账号尚未记录登录时间（可能是刚导入的新账号），建议等待 24 小时后再进行敏感操作"
            };
        }

        // 兼容历史数据：LastLoginAt 为空时，使用导入时间（CreatedAt）作为估算，避免永久误报
        var isEstimated = account.IsRiskReferenceEstimated();
        if (isEstimated && loginHours >= RISK_THRESHOLD_HOURS)
        {
            return new RiskCheckResult
            {
                IsRisky = false,
                Message = "账号登录时长已满足要求（按导入时间估算）",
                DetailedMessage = $"当前已导入时长：{loginHours.Value:F1} 小时"
            };
        }

        if (loginHours.Value < RISK_THRESHOLD_HOURS)
        {
            var remainingHours = RISK_THRESHOLD_HOURS - loginHours.Value;
            return new RiskCheckResult
            {
                IsRisky = true,
                Message = isEstimated
                    ? $"账号未记录登录时间（按导入时间估算：{loginHours.Value:F1} 小时）"
                    : $"账号登录时长不足 24 小时（当前：{loginHours.Value:F1} 小时）",
                DetailedMessage = isEstimated
                    ? $"该账号尚未记录登录时间（可能是较早导入的账号），已按导入时间估算；建议等待 {remainingHours:F1} 小时后再进行敏感操作，以降低被 Telegram 风控的风险"
                    : $"建议等待 {remainingHours:F1} 小时后再进行敏感操作，以降低被 Telegram 风控的风险"
            };
        }

        return new RiskCheckResult
        {
            IsRisky = false,
            Message = "账号登录时长已满足要求",
            DetailedMessage = $"当前登录时长：{loginHours.Value:F1} 小时"
        };
    }

    /// <summary>
    /// 批量检查账号的登录时长
    /// </summary>
    public BatchRiskCheckResult CheckBatchAccounts(IEnumerable<Account> accounts)
    {
        var accountList = accounts.ToList();
        var riskyAccounts = new List<Account>();
        var safeAccounts = new List<Account>();

        foreach (var account in accountList)
        {
            var check = CheckLoginDuration(account);
            if (check.IsRisky)
                riskyAccounts.Add(account);
            else
                safeAccounts.Add(account);
        }

        return new BatchRiskCheckResult
        {
            TotalCount = accountList.Count,
            RiskyCount = riskyAccounts.Count,
            SafeCount = safeAccounts.Count,
            RiskyAccounts = riskyAccounts,
            SafeAccounts = safeAccounts,
            HasRiskyAccounts = riskyAccounts.Count > 0
        };
    }
}

/// <summary>
/// 单账号风控检查结果
/// </summary>
public class RiskCheckResult
{
    /// <summary>
    /// 是否存在风险
    /// </summary>
    public bool IsRisky { get; set; }

    /// <summary>
    /// 风险消息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 详细说明
    /// </summary>
    public string DetailedMessage { get; set; } = string.Empty;
}

/// <summary>
/// 批量账号风控检查结果
/// </summary>
public class BatchRiskCheckResult
{
    /// <summary>
    /// 总账号数
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// 风险账号数
    /// </summary>
    public int RiskyCount { get; set; }

    /// <summary>
    /// 安全账号数
    /// </summary>
    public int SafeCount { get; set; }

    /// <summary>
    /// 风险账号列表
    /// </summary>
    public List<Account> RiskyAccounts { get; set; } = new();

    /// <summary>
    /// 安全账号列表
    /// </summary>
    public List<Account> SafeAccounts { get; set; } = new();

    /// <summary>
    /// 是否存在风险账号
    /// </summary>
    public bool HasRiskyAccounts { get; set; }

    /// <summary>
    /// 获取风险摘要信息
    /// </summary>
    public string GetRiskySummary()
    {
        if (RiskyCount == 0)
            return "所有账号均已满足登录时长要求";

        var riskyPhones = string.Join("、", RiskyAccounts.Select(a =>
        {
            var hours = a.GetRiskReferenceHours();
            if (!hours.HasValue)
                return a.Phone;

            var tag = a.IsRiskReferenceEstimated() ? "导入" : "登录";
            return $"{a.Phone}({tag}{hours.Value:F1}h)";
        }));

        return $"以下账号登录时长不足 24 小时：{riskyPhones}";
    }
}
