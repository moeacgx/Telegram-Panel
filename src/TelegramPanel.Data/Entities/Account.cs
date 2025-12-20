namespace TelegramPanel.Data.Entities;

/// <summary>
/// 账号实体
/// </summary>
public class Account
{
    public int Id { get; set; }
    public string Phone { get; set; } = null!;
    public long UserId { get; set; }
    /// <summary>
    /// 账号昵称（Telegram 显示名称）
    /// </summary>
    public string? Nickname { get; set; }
    public string? Username { get; set; }
    public string SessionPath { get; set; } = null!;
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public int? CategoryId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSyncAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 最后一次登录 Telegram 的时间（UTC），用于风控检查
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Telegram 状态检测结果摘要（用于页面刷新后仍可展示上次检测结论）
    /// </summary>
    public string? TelegramStatusSummary { get; set; }

    /// <summary>
    /// Telegram 状态检测详情（错误码/原因等）
    /// </summary>
    public string? TelegramStatusDetails { get; set; }

    /// <summary>
    /// Telegram 状态检测是否成功（Ok）
    /// </summary>
    public bool? TelegramStatusOk { get; set; }

    /// <summary>
    /// Telegram 状态检测时间（UTC）
    /// </summary>
    public DateTime? TelegramStatusCheckedAtUtc { get; set; }

    // 导航属性
    public AccountCategory? Category { get; set; }
    public ICollection<Channel> Channels { get; set; } = new List<Channel>();
    public ICollection<AccountChannel> AccountChannels { get; set; } = new List<AccountChannel>();
    public ICollection<Group> Groups { get; set; } = new List<Group>();
}
