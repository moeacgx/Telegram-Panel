using TelegramPanel.Core.Models;

namespace TelegramPanel.Core.Interfaces;

/// <summary>
/// 群组服务接口
/// </summary>
public interface IGroupService
{
    /// <summary>
    /// 获取账号创建的所有群组
    /// </summary>
    Task<List<GroupInfo>> GetOwnedGroupsAsync(int accountId);

    /// <summary>
    /// 获取群组详情
    /// </summary>
    Task<GroupInfo?> GetGroupInfoAsync(int accountId, long groupId);

    /// <summary>
    /// 导出加入链接：公开群组返回 t.me 链接；否则导出邀请链接。
    /// </summary>
    Task<string> ExportJoinLinkAsync(int accountId, long groupId);

    /// <summary>
    /// 获取群组管理员列表（需要权限）
    /// </summary>
    Task<List<ChannelAdminInfo>> GetAdminsAsync(int accountId, long groupId);
}
