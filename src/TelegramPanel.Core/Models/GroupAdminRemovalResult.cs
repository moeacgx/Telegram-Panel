namespace TelegramPanel.Core.Models;

/// <summary>
/// 群组管理员移除结果。管理员降权和踢出是两个 Telegram 操作，后一步失败时需要保留部分成功状态。
/// </summary>
public sealed record GroupAdminRemovalResult(
    bool Succeeded,
    bool AdminRightsRemoved,
    bool MemberRemoved,
    bool TargetAlreadyAbsent,
    string Message);
