using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Data.Entities;
using TL;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 账号诊断 / 系统通知 / 在线设备管理
/// </summary>
public class AccountTelegramToolsService
{
    private const long TelegramSystemUserId = 777000;

    private readonly AccountManagementService _accountManagement;
    private readonly ITelegramClientPool _clientPool;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountTelegramToolsService> _logger;

    public AccountTelegramToolsService(
        AccountManagementService accountManagement,
        ITelegramClientPool clientPool,
        IConfiguration configuration,
        ILogger<AccountTelegramToolsService> logger)
    {
        _accountManagement = accountManagement;
        _clientPool = clientPool;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 刷新账号状态（可选深度探测：检测“创建频道接口是否被冻结”，会创建并删除一个测试频道）
    /// </summary>
    public async Task<TelegramAccountStatusResult> RefreshAccountStatusAsync(int accountId, bool probeCreateChannel = false, CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var users = await client.Users_GetUsers(InputUser.Self);
            cancellationToken.ThrowIfCancellationRequested();
            var self = users.OfType<User>().FirstOrDefault();

            if (self == null)
            {
                var missingProfile = new TelegramAccountStatusResult(
                    Ok: false,
                    Summary: "无法获取账号资料",
                    Details: "Users_GetUsers(Self) 未返回 User",
                    CheckedAtUtc: checkedAt);
                await TryPersistStatusAsync(accountId, missingProfile, cancellationToken: cancellationToken);
                return missingProfile;
            }

            var profile = new TelegramAccountProfile(
                UserId: self.id,
                Phone: self.phone,
                Username: self.MainUsername,
                FirstName: self.first_name,
                LastName: self.last_name,
                IsDeleted: self.flags.HasFlag(User.Flags.deleted),
                IsScam: self.flags.HasFlag(User.Flags.scam),
                IsFake: self.flags.HasFlag(User.Flags.fake),
                IsRestricted: self.flags.HasFlag(User.Flags.restricted),
                IsVerified: self.flags.HasFlag(User.Flags.verified),
                IsPremium: self.flags.HasFlag(User.Flags.premium)
            );

            var account = await _accountManagement.GetAccountAsync(accountId);
            if (account != null)
            {
                profile.ApplyTo(account);
            }

            var summary = "正常";
            if (profile.IsDeleted)
                summary = "账号已注销/被删除";
            else if (profile.IsRestricted)
                summary = "账号受限（Restricted）";

            if (probeCreateChannel)
            {
                var probe = await ProbeCreateChannelCapabilityAsync(client, accountId, cancellationToken);
                if (probe.IsFrozen)
                {
                    var frozen = new TelegramAccountStatusResult(
                        Ok: false,
                        Summary: "创建频道受限（冻结）",
                        Details: $"创建频道探测：{probe.Message}{Environment.NewLine}{BuildProfileDetails(profile)}",
                        CheckedAtUtc: checkedAt,
                        Profile: profile);
                    await TryPersistStatusAsync(accountId, frozen, account, persistProfile: true, cancellationToken: cancellationToken);
                    return frozen;
                }

                if (!probe.Success)
                {
                    var failed = new TelegramAccountStatusResult(
                        Ok: false,
                        Summary: "创建频道探测失败",
                        Details: $"创建频道探测：{probe.Message}{Environment.NewLine}{BuildProfileDetails(profile)}",
                        CheckedAtUtc: checkedAt,
                        Profile: profile);
                    await TryPersistStatusAsync(accountId, failed, account, persistProfile: true, cancellationToken: cancellationToken);
                    return failed;
                }

                // 探测成功，不影响原状态，仅补充详情
                var okWithProbe = new TelegramAccountStatusResult(
                    Ok: true,
                    Summary: summary,
                    Details: $"创建频道探测：可用（已自动清理测试频道）{Environment.NewLine}{BuildProfileDetails(profile)}",
                    CheckedAtUtc: checkedAt,
                    Profile: profile);
                await TryPersistStatusAsync(accountId, okWithProbe, account, persistProfile: true, cancellationToken: cancellationToken);
                return okWithProbe;
            }

            var ok = new TelegramAccountStatusResult(
                Ok: true,
                Summary: summary,
                Details: BuildProfileDetails(profile),
                CheckedAtUtc: checkedAt,
                Profile: profile);
            await TryPersistStatusAsync(accountId, ok, account, persistProfile: true, cancellationToken: cancellationToken);
            return ok;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TelegramAccountStatusResult(
                Ok: false,
                Summary: "已取消",
                Details: "操作已取消（页面关闭/刷新导致取消）",
                CheckedAtUtc: checkedAt);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Blazor 页面刷新/断连时，Scoped 的 DbContext 可能已被释放；把它视为取消而不是错误。
            return new TelegramAccountStatusResult(
                Ok: false,
                Summary: "已取消",
                Details: "页面已关闭/刷新，操作被中断",
                CheckedAtUtc: checkedAt);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            _logger.LogWarning(ex, "RefreshAccountStatus failed for account {AccountId}", accountId);
            var failed = new TelegramAccountStatusResult(
                Ok: false,
                Summary: summary,
                Details: details,
                CheckedAtUtc: checkedAt);
            await TryPersistStatusAsync(accountId, failed, cancellationToken: cancellationToken);
            return failed;
        }
    }

    private async Task TryPersistStatusAsync(
        int accountId,
        TelegramAccountStatusResult result,
        Account? account = null,
        bool persistProfile = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            account ??= await _accountManagement.GetAccountAsync(accountId);
            if (account == null)
                return;

            if (persistProfile && result.Profile != null)
                result.Profile.ApplyTo(account);

            account.TelegramStatusOk = result.Ok;
            account.TelegramStatusSummary = result.Summary;
            account.TelegramStatusDetails = result.Details;
            account.TelegramStatusCheckedAtUtc = result.CheckedAtUtc;

            await _accountManagement.UpdateAccountAsync(account);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // 页面/作用域已销毁导致的 DbContext 释放，忽略即可
        }
        catch (Exception ex)
        {
            // 取消场景不需要噪声日志
            if (!cancellationToken.IsCancellationRequested)
                _logger.LogWarning(ex, "Failed to persist Telegram status cache for account {AccountId}", accountId);
        }
    }

    public async Task<IReadOnlyList<TelegramSystemMessage>> GetLatestSystemMessagesAsync(int accountId, int limit = 20)
    {
        if (limit <= 0) limit = 20;
        if (limit > 100) limit = 100;

        var client = await GetOrCreateConnectedClientAsync(accountId);
        var peer = await TryResolveSystemPeerAsync(client);
        if (peer == null)
            return Array.Empty<TelegramSystemMessage>();

        var history = await client.Messages_GetHistory(peer, limit: limit);
        var list = new List<TelegramSystemMessage>(history.Messages.Length);
        foreach (var msgBase in history.Messages)
        {
            if (msgBase is not Message m)
                continue;

            var text = m.message ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            list.Add(new TelegramSystemMessage(
                Id: m.id,
                DateUtc: m.Date.ToUniversalTime(),
                Text: text.Trim()
            ));
        }

        return list
            .OrderByDescending(x => x.DateUtc ?? DateTime.MinValue)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<TelegramAuthorizationInfo>> GetAuthorizationsAsync(int accountId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var auths = await client.Account_GetAuthorizations();

        var list = new List<TelegramAuthorizationInfo>(auths.authorizations.Length);
        foreach (var a in auths.authorizations)
        {
            list.Add(new TelegramAuthorizationInfo(
                Hash: a.hash,
                Current: a.flags.HasFlag(Authorization.Flags.current),
                ApiId: a.api_id,
                AppName: a.app_name,
                AppVersion: a.app_version,
                DeviceModel: a.device_model,
                Platform: a.platform,
                SystemVersion: a.system_version,
                Ip: a.ip,
                Country: a.country,
                Region: a.region,
                CreatedAtUtc: a.date_created == default ? null : a.date_created.ToUniversalTime(),
                LastActiveAtUtc: a.date_active == default ? null : a.date_active.ToUniversalTime()
            ));
        }

        return list
            .OrderByDescending(x => x.Current)
            .ThenByDescending(x => x.LastActiveAtUtc ?? DateTime.MinValue)
            .ToList();
    }

    public async Task<bool> KickAuthorizationAsync(int accountId, long authorizationHash)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var ok = await client.Account_ResetAuthorization(authorizationHash);
        return ok;
    }

    public async Task<bool> KickAllOtherAuthorizationsAsync(int accountId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var ok = await client.Auth_ResetAuthorizations();
        return ok;
    }

    private async Task<InputPeerUser?> TryResolveSystemPeerAsync(Client client)
    {
        try
        {
            var dialogs = await client.Messages_GetAllDialogs();
            if (!dialogs.users.TryGetValue(TelegramSystemUserId, out var userBase))
                return null;

            if (userBase is not User u || u.access_hash == 0)
                return null;

            return new InputPeerUser(u.id, u.access_hash);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve system peer");
            return null;
        }
    }

    private async Task<Client> GetOrCreateConnectedClientAsync(int accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var existing = _clientPool.GetClient(accountId);
        if (existing?.User != null)
            return existing;

        var account = await _accountManagement.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException($"账号不存在：{accountId}");

        cancellationToken.ThrowIfCancellationRequested();

        var apiId = ResolveApiId(account);
        var apiHash = ResolveApiHash(account);
        var sessionKey = ResolveSessionKey(account, apiHash);

        if (string.IsNullOrWhiteSpace(account.SessionPath))
            throw new InvalidOperationException("账号缺少 SessionPath，无法创建 Telegram 客户端");

        var absoluteSessionPath = Path.GetFullPath(account.SessionPath);
        if (File.Exists(absoluteSessionPath) && SessionDataConverter.LooksLikeSqliteSession(absoluteSessionPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ok = await SessionDataConverter.TryConvertSqliteSessionFromJsonAsync(
                phone: account.Phone,
                apiId: account.ApiId,
                apiHash: account.ApiHash,
                sqliteSessionPath: absoluteSessionPath,
                logger: _logger
            );

            if (!ok)
            {
                throw new InvalidOperationException(
                    $"该账号的 Session 文件为 SQLite 格式：{account.SessionPath}，无法自动转换为可用 session。" +
                    "建议：重新导入包含 session_string 的 json，或到【账号-手机号登录】重新登录生成新的 sessions/*.session。");
            }
        }

        await _clientPool.RemoveClientAsync(accountId);
        cancellationToken.ThrowIfCancellationRequested();

        var client = await _clientPool.GetOrCreateClientAsync(
            accountId: accountId,
            apiId: apiId,
            apiHash: apiHash,
            sessionPath: account.SessionPath,
            sessionKey: sessionKey,
            phoneNumber: account.Phone,
            userId: account.UserId > 0 ? account.UserId : null);

        try
        {
            await client.ConnectAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (client.User == null && (client.UserId != 0 || account.UserId != 0))
                await client.LoginUserIfNeeded(reloginOnFailedResume: false);
        }
        catch (Exception ex)
        {
            if (LooksLikeSessionApiMismatchOrCorrupted(ex))
            {
                throw new InvalidOperationException(
                    "该账号的 Session 文件无法解析（通常是 ApiId/ApiHash 与生成 session 时不一致，或 session 文件已损坏）。" +
                    "请到【账号-手机号登录】重新登录生成新的 sessions/*.session 后再试。",
                    ex);
            }

            throw new InvalidOperationException($"Telegram 会话加载失败：{ex.Message}", ex);
        }

        if (client.User == null)
            throw new InvalidOperationException("账号未登录或 session 已失效，请重新登录生成新的 session");

        return client;
    }

    private int ResolveApiId(Account account)
    {
        if (int.TryParse(_configuration["Telegram:ApiId"], out var globalApiId) && globalApiId > 0)
            return globalApiId;
        if (account.ApiId > 0)
            return account.ApiId;
        throw new InvalidOperationException("未配置全局 ApiId，且账号缺少 ApiId");
    }

    private string ResolveApiHash(Account account)
    {
        var global = _configuration["Telegram:ApiHash"];
        if (!string.IsNullOrWhiteSpace(global))
            return global.Trim();
        if (!string.IsNullOrWhiteSpace(account.ApiHash))
            return account.ApiHash.Trim();
        throw new InvalidOperationException("未配置全局 ApiHash，且账号缺少 ApiHash");
    }

    private static string ResolveSessionKey(Account account, string apiHash)
    {
        return !string.IsNullOrWhiteSpace(account.ApiHash) ? account.ApiHash.Trim() : apiHash.Trim();
    }

    private static bool LooksLikeSessionApiMismatchOrCorrupted(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Can't read session block", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Use the correct api_hash", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Use the correct api_hash/id/key", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildProfileDetails(TelegramAccountProfile profile)
    {
        var flags = new List<string>();
        if (profile.IsPremium) flags.Add("Premium");
        if (profile.IsVerified) flags.Add("Verified");
        if (profile.IsRestricted) flags.Add("Restricted");
        if (profile.IsScam) flags.Add("Scam");
        if (profile.IsFake) flags.Add("Fake");
        if (profile.IsDeleted) flags.Add("Deleted");

        var flagText = flags.Count == 0 ? "无" : string.Join(", ", flags);
        return $"昵称：{profile.DisplayName}；用户名：{profile.Username ?? "-"}；UserId：{profile.UserId}；标记：{flagText}";
    }

    private async Task<CreateChannelProbeResult> ProbeCreateChannelCapabilityAsync(Client client, int accountId, CancellationToken cancellationToken = default)
    {
        // 注意：这是“深度探测”，会创建并删除一个测试频道。
        var title = $"tp-check-{DateTime.UtcNow:MMddHHmmss}";
        const string about = "Telegram Panel create-channel probe (auto delete)";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            UpdatesBase updates;
            try
            {
                updates = await client.Channels_CreateChannel(title: title, about: about, broadcast: true);
            }
            catch (RpcException ex) when (ex.Code == 420 && string.Equals(ex.Message, "FROZEN_METHOD_INVALID", StringComparison.OrdinalIgnoreCase))
            {
                return new CreateChannelProbeResult(false, true, "Telegram 返回 FROZEN_METHOD_INVALID（创建频道接口被冻结）");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var channel = updates.Chats.Values.OfType<TL.Channel>().FirstOrDefault();
            if (channel == null)
                return new CreateChannelProbeResult(false, false, "创建测试频道失败：未返回 Channel");

            try
            {
                // 立即删除，避免留下垃圾频道
                var input = new InputChannel(channel.id, channel.access_hash);
                await client.Channels_DeleteChannel(input);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Probe channel created but failed to delete (account {AccountId}, channel {ChannelId})", accountId, channel.id);
                return new CreateChannelProbeResult(false, false, $"创建测试频道成功，但删除失败：{ex.Message}（请手动删除频道：{title}）");
            }

            return new CreateChannelProbeResult(true, false, "可用");
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? "未知错误";
            return new CreateChannelProbeResult(false, false, msg);
        }
    }

    private sealed record CreateChannelProbeResult(bool Success, bool IsFrozen, string Message);

    private static (string summary, string details) MapTelegramException(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;

        if (msg.Contains("FROZEN_METHOD_INVALID", StringComparison.OrdinalIgnoreCase))
            return ("接口被冻结（账号/ApiId 受限）", msg);

        if (msg.Contains("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase))
            return ("Session 失效（AUTH_KEY_UNREGISTERED）", msg);

        if (msg.Contains("SESSION_PASSWORD_NEEDED", StringComparison.OrdinalIgnoreCase))
            return ("需要两步验证密码（SESSION_PASSWORD_NEEDED）", msg);

        if (msg.Contains("PHONE_NUMBER_BANNED", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("USER_DEACTIVATED_BAN", StringComparison.OrdinalIgnoreCase))
            return ("账号被封禁/停用", msg);

        if (msg.Contains("Can't read session block", StringComparison.OrdinalIgnoreCase))
            return ("Session 无法读取（ApiHash/Key 不匹配或损坏）", msg);

        return ("连接失败", msg);
    }
}
