namespace TelegramPanel.Core.Services;

/// <summary>
/// 风控警告对话框的用户操作
/// </summary>
public enum RiskWarningAction
{
    /// <summary>
    /// 继续操作（包含风险账号）
    /// </summary>
    Continue,

    /// <summary>
    /// 排除风险账号后继续
    /// </summary>
    ExcludeRisky
}
