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

    // 导航属性
    public AccountCategory? Category { get; set; }
    public ICollection<Channel> Channels { get; set; } = new List<Channel>();
    public ICollection<Group> Groups { get; set; } = new List<Group>();
}
