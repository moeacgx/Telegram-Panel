using Microsoft.Extensions.Configuration;

namespace TelegramPanel.Core.Models;

/// <summary>
/// 出站代理类型。
/// </summary>
public static class OutboundProxyKinds
{
    public const string Manual = "manual";
    public const string Resin = "resin";
    public const string Warp = "warp";
    public const string WireGuardWarp = "wireguard_warp";

    public static bool IsSupported(string? value) =>
        value is Manual or Resin or Warp or WireGuardWarp;
}

/// <summary>
/// Telegram 支持的代理协议。
/// </summary>
public static class OutboundProxyProtocols
{
    public const string Http = "http";
    public const string Socks5 = "socks5";
    public const string MtProto = "mtproto";

    public static bool IsSupported(string? value) =>
        value is Http or Socks5 or MtProto;
}

/// <summary>
/// 连接代理所需的运行时参数。
/// </summary>
public sealed record ProxyConnectionOptions(
    int ProxyId,
    string Name,
    string Kind,
    string Protocol,
    string Host,
    int Port,
    string? Username,
    string? Password,
    string? Secret);

/// <summary>
/// Resin 临时 Lease 创建时的控制面快照，确保并发编辑或删除代理后仍能用原凭据回收。
/// </summary>
public sealed record ResinLeaseControlSnapshot(
    int ProxyId,
    string? AdminUrl,
    string? AdminToken,
    string? Platform);

/// <summary>
/// 账号的最终代理路由。正常解析结果会把全局代理固化到 Proxy；
/// UseGlobalProxy 仅保留给显式调用方的兼容输入，消费者仍必须以闭锁方式解析它。
/// </summary>
public sealed record AccountProxyResolution(
    ProxyConnectionOptions? Proxy,
    bool UseGlobalProxy);

/// <summary>
/// 公网出口检测结果。
/// </summary>
public sealed record EgressProbeResult(
    bool Success,
    string? Ip,
    string? Country,
    string? City,
    string? Isp,
    string? WarpStatus,
    int? LatencyMs,
    DateTime CheckedAtUtc,
    string? Error);

/// <summary>
/// 代理保存输入。
/// </summary>
public sealed record OutboundProxyInput(
    string? Name,
    string? Kind,
    string? Protocol,
    string? Host,
    int Port,
    string? Username,
    string? Password,
    string? Secret,
    string? ResinPlatform,
    string? ResinAdminUrl,
    string? ResinAdminToken,
    bool IsEnabled = true,
    bool TestAfterSave = false,
    bool ClearPassword = false,
    bool ClearResinAdminToken = false,
    int? CategoryId = null);

/// <summary>
/// 代理分类保存输入。
/// </summary>
public sealed record ProxyCategoryInput(
    string? Name,
    string? Color,
    string? Description);

/// <summary>
/// 账号代理绑定输入。
/// </summary>
public sealed record AccountProxyBindingInput(
    string Strategy,
    int? ProxyId = null,
    int? ExpectedProxyId = null,
    // 导入/登录首连使用的冻结快照。正式绑定在代理变更锁内复核，
    // 防止首条请求走旧出口而落库时绑定到已被编辑的新出口。
    ProxyConnectionOptions? ExpectedConnection = null,
    bool? ExpectedUseGlobalProxy = null,
    bool AcceptWarpTerms = false,
    string? WarpRequestId = null);

/// <summary>
/// 单个账号代理操作结果。
/// </summary>
public sealed record AccountProxyOperationResult(
    int AccountId,
    string? Phone,
    bool Success,
    string Summary,
    string? Error,
    int? ProxyId = null);

/// <summary>
/// 批量账号代理操作结果。
/// </summary>
public sealed record AccountProxyBatchResult(
    int Success,
    int Failed,
    IReadOnlyList<AccountProxyOperationResult> Items);

/// <summary>
/// WARP 运行环境状态。
/// </summary>
public sealed record WarpRuntimeStatus(
    bool PlatformSupported,
    bool Enabled,
    bool DockerAvailable,
    string? DockerVersion,
    string? Error,
    string Image,
    string Network,
    string ProxyHostMode,
    string DefaultProtocol,
    WarpMaintenanceRuntimeStatus? Maintenance = null);

/// <summary>
/// WARP 自动巡检和恢复配置。健康出口默认不主动重启，避免无故更换账号 IP；
/// 如确需参考出口池的定时轮换行为，可显式启用 ScheduledRefreshEnabled。
/// </summary>
public sealed record WarpMaintenanceOptions(
    bool Enabled,
    int InitialDelaySeconds,
    int HealthCheckIntervalMinutes,
    int FailureThreshold,
    int RecoveryCooldownMinutes,
    int RecoveryProbeAttempts,
    int RecoveryProbeDelaySeconds,
    bool ScheduledRefreshEnabled,
    int ScheduledRefreshIntervalMinutes)
{
    public static WarpMaintenanceOptions From(IConfiguration configuration) => new(
        ReadBool(configuration, "Proxy:Warp:Maintenance:Enabled", true),
        ReadInt(configuration, "Proxy:Warp:Maintenance:InitialDelaySeconds", 30, 0, 3600),
        ReadInt(configuration, "Proxy:Warp:Maintenance:HealthCheckIntervalMinutes", 5, 1, 1440),
        ReadInt(configuration, "Proxy:Warp:Maintenance:FailureThreshold", 2, 1, 10),
        ReadInt(configuration, "Proxy:Warp:Maintenance:RecoveryCooldownMinutes", 30, 1, 1440),
        ReadInt(configuration, "Proxy:Warp:Maintenance:RecoveryProbeAttempts", 6, 1, 20),
        ReadInt(configuration, "Proxy:Warp:Maintenance:RecoveryProbeDelaySeconds", 5, 1, 60),
        ReadBool(configuration, "Proxy:Warp:Maintenance:ScheduledRefreshEnabled", false),
        ReadInt(configuration, "Proxy:Warp:Maintenance:ScheduledRefreshIntervalMinutes", 720, 60, 10080));

    private static bool ReadBool(
        IConfiguration configuration,
        string key,
        bool fallback) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;

    private static int ReadInt(
        IConfiguration configuration,
        string key,
        int fallback,
        int min,
        int max) =>
        int.TryParse(configuration[key], out var value) && value >= min && value <= max
            ? value
            : fallback;
}

/// <summary>
/// 后台 WARP 维护任务的可观测状态。
/// </summary>
public sealed record WarpMaintenanceRuntimeStatus(
    bool Enabled,
    bool Running,
    int HealthCheckIntervalMinutes,
    int FailureThreshold,
    int RecoveryCooldownMinutes,
    bool ScheduledRefreshEnabled,
    int ScheduledRefreshIntervalMinutes,
    DateTime? LastRunAtUtc,
    DateTime? NextRunAtUtc,
    string? LastError,
    int CheckedCount,
    int HealthyCount,
    int RecoveredCount,
    int FailedCount);

/// <summary>
/// 单个受管 WARP 的巡检或刷新结果。
/// </summary>
public sealed record WarpMaintenanceResult(
    int ProxyId,
    string Name,
    bool Success,
    bool Restarted,
    bool Recovered,
    string RuntimeStatus,
    string Summary,
    string? Error);

public sealed record WarpMaintenanceBatchResult(
    int Checked,
    int Healthy,
    int Recovered,
    int Failed,
    IReadOnlyList<WarpMaintenanceResult> Items);


/// <summary>
/// 代理出口检测端点配置。定时巡检使用轻量 ProbeUrl；手动检测和 WARP 元数据仍使用 Trace 兼容的 MetadataUrl。
/// </summary>
public sealed record ProxyEgressProbeOptions(
    string ProbeUrl,
    string MetadataUrl)
{
    public const string DefaultProbeUrl = "https://208.67.222.222/";
    public const string DefaultMetadataUrl = "https://cloudflare.com/cdn-cgi/trace";

    public static ProxyEgressProbeOptions From(IConfiguration? configuration) => new(
        ReadHttpUrl(configuration, "Proxy:Egress:ProbeUrl", DefaultProbeUrl),
        ReadHttpUrl(configuration, "Proxy:Egress:MetadataUrl", DefaultMetadataUrl));

    private static string ReadHttpUrl(
        IConfiguration? configuration,
        string key,
        string fallback)
    {
        var value = configuration == null || string.IsNullOrWhiteSpace(configuration[key])
            ? fallback
            : configuration[key]!.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https"
            ? value
            : fallback;
    }
}

/// <summary>
/// 普通代理和 Resin 代理的轻量出口健康巡检配置。巡检不重启或修改代理资源。
/// </summary>
public sealed record ProxyEgressMaintenanceOptions(
    bool Enabled,
    int InitialDelaySeconds,
    int IntervalMinutes)
{
    public static ProxyEgressMaintenanceOptions From(IConfiguration configuration) => new(
        ReadBool(configuration, "Proxy:Egress:Maintenance:Enabled", true),
        ReadInt(configuration, "Proxy:Egress:Maintenance:InitialDelaySeconds", 30, 0, 3600),
        ReadInt(configuration, "Proxy:Egress:Maintenance:IntervalMinutes", 5, 1, 1440));

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;

    private static int ReadInt(
        IConfiguration configuration,
        string key,
        int fallback,
        int min,
        int max) =>
        int.TryParse(configuration[key], out var value) && value >= min && value <= max
            ? value
            : fallback;
}

public sealed record ProxyEgressMaintenanceItem(
    int ProxyId,
    string Name,
    bool Success,
    string? EgressIp,
    string? Error);

public sealed record ProxyEgressMaintenanceBatchResult(
    int Checked,
    int Succeeded,
    int Failed,
    IReadOnlyList<ProxyEgressMaintenanceItem> Items);
