namespace TelegramPanel.Data.Entities;

/// <summary>
/// 出站代理实体。
/// </summary>
public class OutboundProxy
{
    public int Id { get; set; }
    public int? CategoryId { get; set; }
    public string Name { get; set; } = null!;
    public string Kind { get; set; } = "manual";
    public string? ManagedWgcfProfile { get; set; }
    public string Protocol { get; set; } = "socks5";
    public string Host { get; set; } = null!;
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Secret { get; set; }
    public string? ResinPlatform { get; set; }
    public string? ResinAdminUrl { get; set; }
    public string? ResinAdminToken { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string TestStatus { get; set; } = "unknown";
    public string? LastError { get; set; }
    public int? LastLatencyMs { get; set; }
    public string? EgressIp { get; set; }
    public string? EgressCountry { get; set; }
    public string? EgressCity { get; set; }
    public string? EgressIsp { get; set; }
    public DateTime? LastTestedAtUtc { get; set; }
    public DateTime? FirstBoundAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // 导航属性
    public ProxyCategory? Category { get; set; }
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
    public WarpProfile? WarpProfile { get; set; }
}
