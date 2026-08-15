using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Mail;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
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
    private readonly IAccountProxyResolver _proxyResolver;
    private readonly ILogger<AccountTelegramToolsService> _logger;
    private readonly TelegramAccountUpdateHub _updateHub;
    private readonly ISessionPathResolver _sessionPathResolver;

    public AccountTelegramToolsService(
        AccountManagementService accountManagement,
        ITelegramClientPool clientPool,
        IConfiguration configuration,
        IAccountProxyResolver proxyResolver,
        ILogger<AccountTelegramToolsService> logger,
        TelegramAccountUpdateHub updateHub,
        ISessionPathResolver sessionPathResolver)
    {
        _accountManagement = accountManagement;
        _clientPool = clientPool;
        _configuration = configuration;
        _proxyResolver = proxyResolver;
        _logger = logger;
        _updateHub = updateHub;
        _sessionPathResolver = sessionPathResolver;
    }

    /// <summary>
    /// 刷新账号状态（可选深度探测：检测“创建频道接口是否被冻结”，会创建并删除一个测试频道）
    /// </summary>
    public async Task<TelegramAccountStatusResult> RefreshAccountStatusAsync(int accountId, bool probeCreateChannel = false, CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTime.UtcNow;
        try
        {
            var (client, users) = await TelegramTransientConnectionRetry.ExecuteAsync(
                async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentClient = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var currentUsers = await ExecuteTelegramRequestAsync(
                        accountId,
                        "拉取账号资料",
                        () => currentClient.Users_GetUsers(InputUser.Self),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    return (currentClient, currentUsers);
                },
                () => _clientPool.RemoveClientAsync(accountId),
                cancellationToken,
                ex => _logger.LogWarning(
                    "Transient Telegram connection failure while refreshing status for account {AccountId}; rebuilding client once ({ErrorType})",
                    accountId,
                    ex.GetBaseException().GetType().Name));

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
                        Summary: "账号被冻结（创建频道接口受限）",
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
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return new TelegramAccountStatusResult(
                Ok: false,
                Summary: "已取消",
                Details: "操作已取消（页面关闭/刷新导致取消）",
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

    public async Task<IReadOnlyList<TelegramSystemMessage>> GetSystemMessagesInWindowAsync(
        int accountId,
        DateTime fromUtc,
        DateTime toUtc,
        int maxMessages = 300,
        CancellationToken cancellationToken = default)
    {
        maxMessages = Math.Clamp(maxMessages, 20, 1000);
        if (fromUtc.Kind == DateTimeKind.Unspecified)
            fromUtc = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        else
            fromUtc = fromUtc.ToUniversalTime();
        if (toUtc.Kind == DateTimeKind.Unspecified)
            toUtc = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);
        else
            toUtc = toUtc.ToUniversalTime();
        if (toUtc < fromUtc)
            (fromUtc, toUtc) = (toUtc, fromUtc);

        var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var peer = await TryResolveSystemPeerAsync(client);
        if (peer == null)
            return Array.Empty<TelegramSystemMessage>();

        const int pageSize = 100;
        var offsetId = 0;
        var scanned = 0;
        var list = new List<TelegramSystemMessage>();

        while (scanned < maxMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = Math.Min(pageSize, maxMessages - scanned);
            var history = await ExecuteTelegramRequestAsync(
                accountId,
                "读取 777000 系统通知窗口",
                () => client.Messages_GetHistory(peer, offset_id: offsetId, limit: limit),
                cancellationToken,
                resetClientOnTimeout: true);

            if (history.Messages == null || history.Messages.Length == 0)
                break;

            scanned += history.Messages.Length;
            var oldestSeenUtc = DateTime.MaxValue;
            foreach (var msgBase in history.Messages)
            {
                if (msgBase is not Message message)
                    continue;

                var dateUtc = message.Date.ToUniversalTime();
                if (dateUtc < oldestSeenUtc)
                    oldestSeenUtc = dateUtc;

                var text = message.message ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (dateUtc >= fromUtc && dateUtc <= toUtc)
                {
                    list.Add(new TelegramSystemMessage(
                        Id: message.id,
                        DateUtc: dateUtc,
                        Text: text.Trim()));
                }
            }

            if (oldestSeenUtc < fromUtc)
                break;

            var nextOffsetId = history.Messages
                .Select(GetTelegramMessageId)
                .Where(id => id > 0)
                .DefaultIfEmpty(0)
                .Min();
            if (nextOffsetId <= 0 || nextOffsetId == offsetId || history.Messages.Length < limit)
                break;

            offsetId = nextOffsetId;
        }

        return list
            .OrderByDescending(x => x.DateUtc ?? DateTime.MinValue)
            .ToList();
    }

    public async Task EnsureEstimatedRegistrationAsync(int accountId, CancellationToken cancellationToken = default)
    {
        try
        {
            var account = await _accountManagement.GetAccountAsync(accountId);
            if (account == null)
                return;

            if (account.EstimatedRegistrationAt.HasValue || account.EstimatedRegistrationCheckedAtUtc.HasValue)
                return;

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            await TryPopulateEstimatedRegistrationAsync(account, client, accountId, cancellationToken);

            if (account.EstimatedRegistrationAt.HasValue || account.EstimatedRegistrationCheckedAtUtc.HasValue)
                await _accountManagement.UpdateAccountAsync(account);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EnsureEstimatedRegistrationAsync skipped for account {AccountId}", accountId);
        }
    }

    private async Task TryPopulateEstimatedRegistrationAsync(
        Account account,
        Client client,
        int accountId,
        CancellationToken cancellationToken)
    {
        if (account.EstimatedRegistrationAt.HasValue || account.EstimatedRegistrationCheckedAtUtc.HasValue)
            return;

        var (checkedSuccessfully, estimatedAtUtc) = await TryGetEstimatedRegistrationFromSystemMessagesAsync(client, accountId, cancellationToken);
        if (!checkedSuccessfully)
            return;

        if (estimatedAtUtc.HasValue)
            account.EstimatedRegistrationAt = estimatedAtUtc.Value;

        account.EstimatedRegistrationCheckedAtUtc = DateTime.UtcNow;
    }

    private async Task<(bool CheckedSuccessfully, DateTime? EstimatedAtUtc)> TryGetEstimatedRegistrationFromSystemMessagesAsync(
        Client client,
        int accountId,
        CancellationToken cancellationToken)
    {
        try
        {
            var peer = await TryResolveSystemPeerAsync(client);
            if (peer == null)
                return (true, null);

            const int pageSize = 100;
            const int maxPages = 200;
            var offsetId = 0;
            DateTime? earliest = null;

            for (var page = 0; page < maxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var history = await ExecuteTelegramRequestAsync(
                    accountId,
                    "读取 777000 系统通知历史",
                    () => client.Messages_GetHistory(peer, offset_id: offsetId, limit: pageSize),
                    cancellationToken,
                    resetClientOnTimeout: true);

                if (history.Messages == null || history.Messages.Length == 0)
                    break;

                foreach (var msgBase in history.Messages)
                {
                    if (msgBase is not Message message)
                        continue;

                    if (string.IsNullOrWhiteSpace(message.message))
                        continue;

                    var messageUtc = message.Date.ToUniversalTime();
                    if (!earliest.HasValue || messageUtc < earliest.Value)
                        earliest = messageUtc;
                }

                var nextOffsetId = history.Messages
                    .Select(GetTelegramMessageId)
                    .Where(id => id > 0)
                    .DefaultIfEmpty(0)
                    .Min();

                if (nextOffsetId <= 0 || nextOffsetId == offsetId || history.Messages.Length < pageSize)
                    break;

                offsetId = nextOffsetId;
            }

            return (true, earliest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to estimate registration time from 777000 for account {AccountId}", accountId);
            return (false, null);
        }
    }

    private static int GetTelegramMessageId(MessageBase msgBase) => msgBase switch
    {
        Message message => message.id,
        MessageService service => service.id,
        _ => 0
    };

    public async Task<IReadOnlyList<TelegramAuthorizationInfo>> GetAuthorizationsAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var auths = await TelegramTransientConnectionRetry.ExecuteAsync(
            async () =>
            {
                var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
                return await ExecuteTelegramRequestAsync(
                    accountId,
                    "读取在线设备",
                    () => client.Account_GetAuthorizations(),
                    cancellationToken,
                    resetClientOnTimeout: false);
            },
            () => _clientPool.RemoveClientAsync(accountId),
            cancellationToken,
            ex => _logger.LogWarning(
                "Transient Telegram connection failure while reading devices for account {AccountId}; rebuilding client once ({ErrorType})",
                accountId,
                ex.GetBaseException().GetType().Name));

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

    /// <summary>
    /// 修改 Telegram 两步验证（二级密码）。
    /// </summary>
    public async Task<(bool Success, string? Error)> ChangeTwoFactorPasswordAsync(
        int accountId,
        string? currentPassword,
        string newPassword,
        string? hint = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newPassword))
                return (false, "新二级密码不能为空");

            currentPassword = (currentPassword ?? string.Empty).Trim();
            newPassword = newPassword.Trim();
            hint = (hint ?? string.Empty).Trim();

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // 参考 WTelegramClient 官方示例：Account_UpdatePasswordSettings 需要 SRP 校验值（旧密码）与新密码 settings
            var accountPwd = await client.Account_GetPassword();
            cancellationToken.ThrowIfCancellationRequested();

            // 若账号已开启两步验证但未提供旧密码，则直接提示
            TL.InputCheckPasswordSRP? oldCheck = null;
            if (accountPwd.current_algo != null)
            {
                if (string.IsNullOrWhiteSpace(currentPassword))
                    return (false, "该账号已开启两步验证，请填写原二级密码");

                oldCheck = await WTelegram.Client.InputCheckPassword(accountPwd, currentPassword);
            }

            // 让 InputCheckPassword 生成 new_password_hash（需要将 current_algo 置空）
            accountPwd.current_algo = null;
            var newPasswordHash = await WTelegram.Client.InputCheckPassword(accountPwd, newPassword);

            var settings = new TL.Account_PasswordInputSettings
            {
                flags = TL.Account_PasswordInputSettings.Flags.has_new_algo,
                new_algo = accountPwd.new_algo,
                new_password_hash = newPasswordHash?.A,
                hint = hint
            };

            await client.Account_UpdatePasswordSettings(oldCheck, settings);
            return (true, null);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg);
        }
    }

    /// <summary>
    /// 忘记二级密码：向 Telegram 发起“重置两步验证密码”申请（通常需要等待 7 天）。
    /// </summary>
    public async Task<(bool Success, string? Error, DateTimeOffset? WaitUntilUtc)> RequestTwoFactorPasswordResetAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var result = await client.Account_ResetPassword();
            cancellationToken.ThrowIfCancellationRequested();

            switch (result)
            {
                case TL.Account_ResetPasswordOk:
                    return (true, "二级密码已重置成功（现在可以直接重新设置二级密码）", null);

                case TL.Account_ResetPasswordRequestedWait wait:
                    {
                        var untilUtc = ToUtcDateTimeOffset(wait.until_date);
                        return (true, $"已提交重置申请，请等待至 {untilUtc:yyyy-MM-dd HH:mm:ss} UTC 后再完成重置/重新设置二级密码", untilUtc);
                    }

                case TL.Account_ResetPasswordFailedWait failed:
                    {
                        var retryUtc = ToUtcDateTimeOffset(failed.retry_date);
                        return (false, $"近期有被取消的重置申请，需等待至 {retryUtc:yyyy-MM-dd HH:mm:ss} UTC 后才能再次申请", retryUtc);
                    }

                default:
                    return (false, $"未知返回类型：{result.GetType().Name}", null);
            }
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    private static DateTimeOffset ToUtcDateTimeOffset(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        else
            value = value.ToUniversalTime();

        return new DateTimeOffset(value);
    }

    /// <summary>
    /// 获取两步验证找回邮箱状态（是否已绑定、是否存在待确认的邮箱）。
    /// </summary>
    public async Task<(bool Success, string? Error, bool HasTwoFactorPassword, bool HasRecoveryEmail, string? UnconfirmedEmailPattern)>
        GetTwoFactorRecoveryEmailStatusAsync(int accountId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var pwd = await client.Account_GetPassword();
            var hasPassword = pwd.current_algo != null;
            var hasRecoveryEmail = pwd.flags.HasFlag(TL.Account_Password.Flags.has_recovery);
            var unconfirmed = pwd.flags.HasFlag(TL.Account_Password.Flags.has_email_unconfirmed_pattern)
                ? (pwd.email_unconfirmed_pattern ?? "").Trim()
                : null;

            if (string.IsNullOrWhiteSpace(unconfirmed))
                unconfirmed = null;

            return (true, null, hasPassword, hasRecoveryEmail, unconfirmed);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, false, false, null);
        }
    }

    /// <summary>
    /// 绑定/换绑两步验证找回邮箱（会发送验证码到邮箱，需调用 ConfirmTwoFactorRecoveryEmailAsync 确认）。
    /// </summary>
    public async Task<(bool Success, string? Error, string? EmailPattern)> SetTwoFactorRecoveryEmailAsync(
        int accountId,
        string? currentPassword,
        string email,
        CancellationToken cancellationToken = default)
    {
        try
        {
            email = (email ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(email))
                return (false, "邮箱不能为空", null);

            try
            {
                _ = new MailAddress(email);
            }
            catch
            {
                return (false, "邮箱格式不正确", null);
            }

            currentPassword = (currentPassword ?? string.Empty).Trim();

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var pwd = await client.Account_GetPassword();
            cancellationToken.ThrowIfCancellationRequested();

            if (pwd.current_algo == null)
                return (false, "该账号未开启两步验证，无法绑定找回邮箱，请先设置二级密码", null);

            if (string.IsNullOrWhiteSpace(currentPassword))
                return (false, "请填写原二级密码", null);

            var oldCheck = await WTelegram.Client.InputCheckPassword(pwd, currentPassword);

            var settings = new TL.Account_PasswordInputSettings
            {
                flags = TL.Account_PasswordInputSettings.Flags.has_email,
                email = email
            };

            await client.Account_UpdatePasswordSettings(oldCheck, settings);

            // 更新后可通过 getPassword 获取“待确认邮箱”掩码信息
            var after = await client.Account_GetPassword();
            var pattern = after.flags.HasFlag(TL.Account_Password.Flags.has_email_unconfirmed_pattern)
                ? (after.email_unconfirmed_pattern ?? "").Trim()
                : null;

            if (string.IsNullOrWhiteSpace(pattern))
                pattern = null;

            return (true, null, pattern);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    /// <summary>
    /// 确认两步验证找回邮箱验证码。
    /// </summary>
    public async Task<(bool Success, string? Error)> ConfirmTwoFactorRecoveryEmailAsync(
        int accountId,
        string code,
        CancellationToken cancellationToken = default)
    {
        try
        {
            code = (code ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code))
                return (false, "验证码不能为空");

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _ = await client.Account_ConfirmPasswordEmail(code);
            return (true, null);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg);
        }
    }

    /// <summary>
    /// 重发两步验证找回邮箱验证码（需要先设置邮箱）。
    /// </summary>
    public async Task<(bool Success, string? Error, string? EmailPattern, int? CodeLength)> ResendTwoFactorRecoveryEmailAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var ok = await client.Account_ResendPasswordEmail();
            if (!ok)
                return (false, "重发失败", null, null);

            var pwd = await client.Account_GetPassword();
            var pattern = pwd.flags.HasFlag(TL.Account_Password.Flags.has_email_unconfirmed_pattern)
                ? (pwd.email_unconfirmed_pattern ?? "").Trim()
                : null;

            if (string.IsNullOrWhiteSpace(pattern))
                pattern = null;

            // 该 API 不返回验证码长度，仅返回邮箱掩码信息
            return (true, null, pattern, null);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null, null);
        }
    }

    /// <summary>
    /// 取消待确认的找回邮箱验证码。
    /// </summary>
    public async Task<(bool Success, string? Error)> CancelTwoFactorRecoveryEmailAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _ = await client.Account_CancelPasswordEmail();
            return (true, null);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg);
        }
    }

    /// <summary>
    /// 获取登录邮箱状态（仅返回掩码 Pattern，不返回真实邮箱）。
    /// </summary>
    public async Task<(bool Success, string? Error, bool HasLoginEmail, string? LoginEmailPattern)>
        GetLoginEmailStatusAsync(int accountId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var pwd = await client.Account_GetPassword();
            var hasLoginEmail = pwd.flags.HasFlag(TL.Account_Password.Flags.has_login_email_pattern);
            var pattern = hasLoginEmail ? (pwd.login_email_pattern ?? "").Trim() : null;
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = null;

            return (true, null, hasLoginEmail, pattern);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, false, null);
        }
    }

    /// <summary>
    /// 发送登录邮箱验证码（用于“登录邮箱变更/设置”）。
    /// 注意：部分账号可能无法在“已登录状态”下新增登录邮箱（需要登录流程触发的 setup）。
    /// </summary>
    public async Task<(bool Success, string? Error, string? EmailPattern)> SetLoginEmailAsync(
        int accountId,
        string email,
        CancellationToken cancellationToken = default)
    {
        try
        {
            email = (email ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(email))
                return (false, "邮箱不能为空", null);

            try
            {
                _ = new MailAddress(email);
            }
            catch
            {
                return (false, "邮箱格式不正确", null);
            }

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var sent = await client.Account_SendVerifyEmailCode(new EmailVerifyPurposeLoginChange(), email);
            var pattern = (sent.email_pattern ?? "").Trim();
            if (string.IsNullOrWhiteSpace(pattern))
                pattern = null;

            return (true, null, pattern);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    /// <summary>
    /// 确认登录邮箱验证码。
    /// </summary>
    public async Task<(bool Success, string? Error)> ConfirmLoginEmailAsync(
        int accountId,
        string code,
        CancellationToken cancellationToken = default)
    {
        try
        {
            code = (code ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code))
                return (false, "请填写邮箱验证码");

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _ = await client.Account_VerifyEmail(new EmailVerifyPurposeLoginChange(), new EmailVerificationCode { code = code });
            return (true, null);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg);
        }
    }

    /// <summary>
    /// 更新当前账号的昵称/简介（Bio）。
    /// 注意：用户名与头像分开使用 UpdateUsernameAsync / UpdateProfilePhotoAsync。
    /// </summary>
    public async Task<(bool Success, string? Error)> UpdateUserProfileAsync(
        int accountId,
        string? nickname,
        string? bio,
        CancellationToken cancellationToken = default)
    {
        try
        {
            nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
            bio = bio == null ? null : bio.Trim();

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // account.updateProfile 的字段是可选的：传 null 表示不修改该字段
            string? firstName = null;
            string? lastName = null;
            if (nickname != null)
            {
                firstName = nickname;
                lastName = string.Empty;
            }

            await client.Account_UpdateProfile(firstName, lastName, bio);
            return (true, null);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg);
        }
    }

    /// <summary>
    /// 更新当前账号用户名（t.me/xxx）。传空字符串表示清空用户名。
    /// </summary>
    public async Task<(bool Success, string? Error, string? Username)> UpdateUsernameAsync(
        int accountId,
        string? username,
        CancellationToken cancellationToken = default)
    {
        try
        {
            username = (username ?? string.Empty).Trim().TrimStart('@');

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var result = await client.Account_UpdateUsername(username);

            // result 可能是 User 或 bool，统一从输入回填即可
            var normalized = string.IsNullOrWhiteSpace(username) ? null : username;
            return (true, null, normalized);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    /// <summary>
    /// 通过链接/用户名加入群组或订阅频道（支持 https://t.me/xxx、t.me/+hash、@username、username、tg://join?invite=hash 等）。
    /// </summary>
    public async Task<(bool Success, string? Error, string? JoinedTitle)> JoinChatOrChannelAsync(
        int accountId,
        string linkOrUsername,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await TelegramTransientConnectionRetry.ExecuteAsync<(
                bool Success,
                string? Error,
                string? JoinedTitle)>(
                async () =>
                {
                    var raw = (linkOrUsername ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(raw))
                        return (false, "链接/用户名为空", null);

                    var url = NormalizeTelegramJoinUrl(raw);

                    var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var chat = await ExecuteTelegramRequestAsync(
                        accountId,
                        "加入/订阅群组或频道",
                        () => client.AnalyzeInviteLink(url, join: true),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    cancellationToken.ThrowIfCancellationRequested();

                    var title = chat switch
                    {
                        TL.Channel c => c.title,
                        TL.Chat c => c.title,
                        _ => null
                    };

                    return (true, null, title);
                },
                () => _clientPool.RemoveClientAsync(accountId),
                cancellationToken,
                ex => _logger.LogWarning(
                    "Transient Telegram connection failure while joining chat/channel for account {AccountId}; rebuilding client once ({ErrorType})",
                    accountId,
                    ex.GetBaseException().GetType().Name));
        }
        catch (RpcException ex) when (ex.Code == 400 && string.Equals(ex.Message, "USER_ALREADY_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
        {
            return (true, null, "已在群组/频道中");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    /// <summary>
    /// 通过链接/用户名退出群组或取消订阅频道（支持 https://t.me/xxx、t.me/+hash、@username、username、tg://join?invite=hash 等）。
    /// </summary>
    public async Task<(bool Success, string? Error, string? LeftTitle)> LeaveChatOrChannelAsync(
        int accountId,
        string linkOrUsername,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await TelegramTransientConnectionRetry.ExecuteAsync<(
                bool Success,
                string? Error,
                string? LeftTitle)>(
                async () =>
                {
                    var raw = (linkOrUsername ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(raw))
                        return (false, "链接/用户名为空", null);

                    var url = NormalizeTelegramJoinUrl(raw);

                    var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    // 解析目标（不加入）
                    var chat = await ExecuteTelegramRequestAsync(
                        accountId,
                        "解析退出/退订目标",
                        () => client.AnalyzeInviteLink(url, join: false),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    cancellationToken.ThrowIfCancellationRequested();

                    var title = chat switch
                    {
                        TL.Channel c => c.title,
                        TL.Chat c => c.title,
                        _ => null
                    };

                    var peer = chat switch
                    {
                        TL.Channel c => c.ToInputPeer(),
                        TL.Chat c => c.ToInputPeer(),
                        _ => null
                    };

                    if (peer == null)
                        return (false, "无法解析目标群组/频道", null);

                    await ExecuteTelegramRequestAsync(
                        accountId,
                        "退出/退订群组或频道",
                        () => client.LeaveChat(peer),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    return (true, null, title);
                },
                () => _clientPool.RemoveClientAsync(accountId),
                cancellationToken,
                ex => _logger.LogWarning(
                    "Transient Telegram connection failure while leaving chat/channel for account {AccountId}; rebuilding client once ({ErrorType})",
                    accountId,
                    ex.GetBaseException().GetType().Name));
        }
        catch (RpcException ex) when (ex.Code == 400 && string.Equals(ex.Message, "USER_NOT_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
        {
            return (true, null, "未在群组/频道中");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    /// <summary>
    /// 启用外部 Bot（向 Bot 发送 /start，可带参数）。
    /// 支持：@xxxbot、xxxbot、https://t.me/xxxbot、tg://resolve?domain=xxxbot&start=abc
    /// </summary>
    public async Task<(bool Success, string? Error, string? BotUsername)> StartExternalBotAsync(
        int accountId,
        string botLinkOrUsername,
        string? startParameter = null,
        CancellationToken cancellationToken = default,
        bool assumeBotUsername = false)
    {
        try
        {
            return await TelegramTransientConnectionRetry.ExecuteAsync<(
                bool Success,
                string? Error,
                string? BotUsername)>(
                async () =>
                {
                    var (username, startFromLink) = NormalizeTelegramBotUsername(botLinkOrUsername, assumeBotUsername);
                    var normalizedManualStart = NormalizeBotStartParameter(startParameter);
                    var finalStart = string.IsNullOrWhiteSpace(normalizedManualStart) ? startFromLink : normalizedManualStart;

                    if (finalStart.Length > 64)
                        return (false, "启动参数过长（最多 64 字符）", null);

                    var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var resolved = await ExecuteTelegramRequestAsync(
                        accountId,
                        "解析 Bot 用户名",
                        () => client.Contacts_ResolveUsername(username),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    var user = resolved.User;
                    if (user.access_hash == 0)
                        return (false, "无法获取 Bot access_hash", null);

                    var inputUser = new InputUser(user.id, user.access_hash);
                    var randomId = Random.Shared.NextInt64();
                    if (string.IsNullOrWhiteSpace(finalStart))
                    {
                        var inputPeer = new InputPeerUser(user.id, user.access_hash);
                        await ExecuteTelegramRequestAsync(
                            accountId,
                            "启用 Bot",
                            () => client.SendMessageAsync(inputPeer, "/start"),
                            cancellationToken,
                            resetClientOnTimeout: false);
                    }
                    else
                    {
                        await ExecuteTelegramRequestAsync(
                            accountId,
                            "启用 Bot",
                            () => client.Messages_StartBot(
                                bot: inputUser,
                                peer: new InputPeerSelf(),
                                random_id: randomId,
                                start_param: finalStart),
                            cancellationToken,
                            resetClientOnTimeout: false);
                    }

                    return (true, null, "@" + username);
                },
                () => _clientPool.RemoveClientAsync(accountId),
                cancellationToken,
                ex => _logger.LogWarning(
                    "Transient Telegram connection failure while starting bot for account {AccountId}; rebuilding client once ({ErrorType})",
                    accountId,
                    ex.GetBaseException().GetType().Name));
        }
        catch (RpcException ex) when (ex.Code == 400 && string.Equals(ex.Message, "BOT_APP_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "目标不是可启动的 Bot（BOT_APP_INVALID）", null);
        }
        catch (RpcException ex) when (ex.Code == 400 && string.Equals(ex.Message, "PEER_FLOOD", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "触发风控（PEER_FLOOD），请降低频率后重试", null);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    /// <summary>
    /// 停用外部 Bot（通过拉黑 Bot 实现）。
    /// 支持：@xxxbot、xxxbot、https://t.me/xxxbot、tg://resolve?domain=xxxbot
    /// </summary>
    public async Task<(bool Success, string? Error, string? BotUsername)> StopExternalBotAsync(
        int accountId,
        string botLinkOrUsername,
        CancellationToken cancellationToken = default,
        bool assumeBotUsername = false)
    {
        try
        {
            return await TelegramTransientConnectionRetry.ExecuteAsync<(
                bool Success,
                string? Error,
                string? BotUsername)>(
                async () =>
                {
                    var (username, _) = NormalizeTelegramBotUsername(botLinkOrUsername, assumeBotUsername);

                    var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var resolved = await ExecuteTelegramRequestAsync(
                        accountId,
                        "解析 Bot 用户名",
                        () => client.Contacts_ResolveUsername(username),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    var user = resolved.User;
                    if (user.access_hash == 0)
                        return (false, "无法获取 Bot access_hash", null);

                    await ExecuteTelegramRequestAsync(
                        accountId,
                        "停用 Bot",
                        () => client.Contacts_Block(new InputPeerUser(user.id, user.access_hash)),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    return (true, null, "@" + username);
                },
                () => _clientPool.RemoveClientAsync(accountId),
                cancellationToken,
                ex => _logger.LogWarning(
                    "Transient Telegram connection failure while stopping bot for account {AccountId}; rebuilding client once ({ErrorType})",
                    accountId,
                    ex.GetBaseException().GetType().Name));
        }
        catch (RpcException ex) when (ex.Code == 400 && string.Equals(ex.Message, "USER_NOT_MUTUAL_CONTACT", StringComparison.OrdinalIgnoreCase))
        {
            // 某些账号状态下会返回该错误，按“已停用”处理可避免批量任务中断。
            return (true, null, null);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    public sealed record ResolvedChatTarget(InputPeer Peer, string Title, string CanonicalId);

    /// <summary>
    /// 解析群组/频道/Bot 目标，支持：
    /// - 用户名/链接：@username、username、https://t.me/xxx、t.me/xxx、tg://join?invite=hash、tg://resolve?domain=xxxbot
    /// - 频道/群组 ID：123456、-123456、-1001234567890
    /// - Bot 用户名/链接：@xxxbot、xxxbot、https://t.me/xxxbot?start=abc
    /// </summary>
    public async Task<(bool Success, string? Error, ResolvedChatTarget? Target)> ResolveChatTargetAsync(
        int accountId,
        string target,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await TelegramTransientConnectionRetry.ExecuteAsync<(
                bool Success,
                string? Error,
                ResolvedChatTarget? Target)>(
                async () =>
                {
                    var raw = (target ?? string.Empty).Trim();
                    if (raw.Length == 0)
                        return (false, "目标为空", null);

                    var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (TryParseChatIdCandidate(raw, out var normalizedId))
                    {
                        var resolvedById = await TryResolveChatByIdFromDialogsAsync(
                            client,
                            accountId,
                            normalizedId,
                            cancellationToken);
                        if (resolvedById != null)
                            return (true, null, resolvedById);

                        return (false, $"未找到 chatId={raw} 对应的群组/频道（请确认该账号已加入目标）", null);
                    }

                    if (TryNormalizeTelegramBotUsername(raw, out var botUsername))
                    {
                        var botTarget = await TryResolveBotChatTargetAsync(
                            client,
                            accountId,
                            botUsername,
                            cancellationToken);
                        if (botTarget != null)
                            return (true, null, botTarget);
                    }

                    var url = NormalizeTelegramJoinUrl(raw);
                    var chat = await ExecuteTelegramRequestAsync(
                        accountId,
                        "解析群组/频道/Bot 目标",
                        () => client.AnalyzeInviteLink(url, join: false),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    cancellationToken.ThrowIfCancellationRequested();

                    var peer = chat switch
                    {
                        TL.Channel c => c.ToInputPeer(),
                        TL.Chat c => c.ToInputPeer(),
                        _ => null
                    };

                    if (peer == null)
                        return (false, "无法解析目标群组/频道/Bot", null);

                    return chat switch
                    {
                        TL.Channel channel => (true, null, new ResolvedChatTarget(peer, NormalizeChatTitle(channel.title, channel.id.ToString(CultureInfo.InvariantCulture)), BuildChannelBotApiChatId(channel.id).ToString(CultureInfo.InvariantCulture))),
                        TL.Chat basic => (true, null, new ResolvedChatTarget(peer, NormalizeChatTitle(basic.title, basic.id.ToString(CultureInfo.InvariantCulture)), basic.id.ToString(CultureInfo.InvariantCulture))),
                        _ => (true, null, new ResolvedChatTarget(peer, raw, raw))
                    };
                },
                () => _clientPool.RemoveClientAsync(accountId),
                cancellationToken,
                ex => _logger.LogWarning(
                    "Transient Telegram connection failure while resolving chat/channel/bot target for account {AccountId}; rebuilding client once ({ErrorType})",
                    accountId,
                    ex.GetBaseException().GetType().Name));
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    /// <summary>
    /// 向已解析的群组/频道/Bot 目标发送文本消息。
    /// </summary>
    public async Task<(bool Success, string? Error, int? MessageId)> SendMessageToResolvedChatAsync(
        int accountId,
        ResolvedChatTarget target,
        string message,
        int? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var text = (message ?? string.Empty).Trim();
            if (text.Length == 0)
                return (false, "消息内容为空", null);

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var sent = await client.SendMessageAsync(target.Peer, text, null, replyToMessageId ?? 0);
            return (true, null, sent.id);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    /// <summary>
    /// 向已解析的群组/频道/Bot 目标发送图片，可附带纯文本 caption。
    /// </summary>
    public async Task<(bool Success, string? Error, int? MessageId)> SendPhotoToResolvedChatAsync(
        int accountId,
        ResolvedChatTarget target,
        Stream imageStream,
        string fileName,
        string? caption = null,
        int? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (imageStream == null)
                return (false, "图片内容为空", null);

            var text = (caption ?? string.Empty).Trim();
            if (text.Length > 1024)
                return (false, "图片说明文字超过 Telegram 1024 字符限制", null);

            var uploadName = NormalizeUploadFileName(fileName);
            if (imageStream.CanSeek)
                imageStream.Position = 0;

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var uploaded = await client.UploadFileAsync(imageStream, uploadName);
            cancellationToken.ThrowIfCancellationRequested();

            var sent = await client.SendMediaAsync(
                target.Peer,
                text.Length == 0 ? null : text,
                uploaded,
                "photo",
                replyToMessageId ?? 0);

            return (true, null, sent.id);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    public async Task<(bool Success, string? Error, TelegramVerificationMessageCandidate? Candidate)> WaitForBotVerificationMessageAsync(
        int accountId,
        ResolvedChatTarget target,
        int sentMessageId,
        string? currentUsername,
        int timeoutSeconds,
        Func<TelegramAccountMessageUpdate, bool>? messageFilter = null,
        IReadOnlyCollection<string>? allowedSenderUsernames = null,
        bool restrictToAllowedUsernames = false,
        bool stopOnUnmatchedMention = false,
        CancellationToken cancellationToken = default)
    {
        if (timeoutSeconds < 3)
            timeoutSeconds = 3;
        if (timeoutSeconds > 300)
            timeoutSeconds = 300;

        try
        {
            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            var waitStartedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
            var update = await _updateHub.WaitForAsync(
                accountId,
                x => IsCandidateVerificationMessage(
                    x,
                    target,
                    currentUsername,
                    sentMessageId,
                    messageFilter,
                    allowedSenderUsernames,
                    restrictToAllowedUsernames,
                    stopOnUnmatchedMention),
                waitStartedAt,
                TimeSpan.FromSeconds(timeoutSeconds),
                cancellationToken);

            if (update == null)
                return (false, $"等待验证消息超时（{timeoutSeconds} 秒）", null);

            if (messageFilter != null
                && stopOnUnmatchedMention
                && !messageFilter(update)
                && IsMentionOrReply(update, currentUsername, sentMessageId))
            {
                return (false, "验证消息未命中关键词/正则，已跳过", null);
            }

            var candidate = await BuildVerificationCandidateAsync(
                client,
                update.Message,
                currentUsername,
                sentMessageId,
                cancellationToken);

            return candidate == null
                ? (false, "匹配到的验证消息为空，无法执行 AI 识别", null)
                : (true, null, candidate);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
    }

    public async Task<(bool Success, string? Error)> ClickInlineButtonAsync(
        int accountId,
        ResolvedChatTarget target,
        int messageId,
        byte[] callbackData,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (callbackData == null || callbackData.Length == 0)
                return (false, "按钮缺少 callback_data");

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _ = await client.Messages_GetBotCallbackAnswer(target.Peer, messageId, callbackData, null, false);
            return (true, null);
        }
        catch (Exception ex) when (IsBotCallbackTimeout(ex))
        {
            _logger.LogInformation(
                ex,
                "Telegram bot callback timed out after click, treat as delivered: accountId={AccountId}, chat={ChatId}, messageId={MessageId}",
                accountId,
                target.CanonicalId,
                messageId);
            return (true, null);
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg);
        }
    }

    private async Task<ResolvedChatTarget?> TryResolveBotChatTargetAsync(
        Client client,
        int accountId,
        string username,
        CancellationToken cancellationToken)
    {
        var resolved = await ExecuteTelegramRequestAsync(
            accountId,
            "解析 Bot 私聊目标",
            () => client.Contacts_ResolveUsername(username),
            cancellationToken,
            resetClientOnTimeout: false);
        cancellationToken.ThrowIfCancellationRequested();

        var user = resolved.User;
        if (user == null || !user.IsBot || user.access_hash == 0)
            return null;

        var title = string.IsNullOrWhiteSpace(user.username) ? $"Bot {user.id}" : $"@{user.username}";
        return new ResolvedChatTarget(
            new InputPeerUser(user.id, user.access_hash),
            title,
            $"user:{user.id.ToString(CultureInfo.InvariantCulture)}");
    }

    private async Task<ResolvedChatTarget?> TryResolveChatByIdFromDialogsAsync(
        Client client,
        int accountId,
        long normalizedId,
        CancellationToken cancellationToken)
    {
        var dialogs = await ExecuteTelegramRequestAsync(
            accountId,
            "读取账号群组/频道列表",
            () => client.Messages_GetAllDialogs(),
            cancellationToken,
            resetClientOnTimeout: false);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var chat in dialogs.chats.Values)
        {
            switch (chat)
            {
                case TL.Channel channel when channel.IsActive:
                    {
                        var rawId = channel.id;
                        var botApiId = BuildChannelBotApiChatId(rawId);
                        if (normalizedId != rawId && normalizedId != botApiId)
                            continue;

                        return new ResolvedChatTarget(
                            channel.ToInputPeer(),
                            NormalizeChatTitle(channel.title, rawId.ToString(CultureInfo.InvariantCulture)),
                            botApiId.ToString(CultureInfo.InvariantCulture));
                    }
                case TL.Chat basic when basic.IsActive:
                    {
                        var rawId = basic.id;
                        var negativeId = -rawId;
                        if (normalizedId != rawId && normalizedId != negativeId)
                            continue;

                        return new ResolvedChatTarget(
                            basic.ToInputPeer(),
                            NormalizeChatTitle(basic.title, rawId.ToString(CultureInfo.InvariantCulture)),
                            rawId.ToString(CultureInfo.InvariantCulture));
                    }
            }
        }

        return null;
    }

    private bool IsCandidateVerificationMessage(
        TelegramAccountMessageUpdate update,
        ResolvedChatTarget target,
        string? currentUsername,
        int sentMessageId,
        Func<TelegramAccountMessageUpdate, bool>? messageFilter,
        IReadOnlyCollection<string>? allowedSenderUsernames,
        bool restrictToAllowedUsernames,
        bool stopOnUnmatchedMention)
    {
        if (!IsSamePeer(target.Peer, update.Message.peer_id))
            return false;

        if (restrictToAllowedUsernames)
        {
            if (!IsSenderInAllowedUsernames(update, allowedSenderUsernames))
                return false;
        }
        else
        {
            if (!update.SenderIsBot)
                return false;
        }

        if (messageFilter != null)
        {
            if (messageFilter(update))
                return true;

            if (stopOnUnmatchedMention && IsMentionOrReply(update, currentUsername, sentMessageId))
                return true;

            return false;
        }

        if (!IsMentionOrReply(update, currentUsername, sentMessageId))
            return false;

        return LooksLikeVerificationChallenge(update);
    }

    private static bool IsMentionOrReply(
        TelegramAccountMessageUpdate update,
        string? currentUsername,
        int sentMessageId)
    {
        var mentionsAccount = ContainsUsernameMention(update.Message.message, currentUsername);
        var replyToSent = update.ReplyToMessageId == sentMessageId;
        return mentionsAccount || replyToSent;
    }

    private static bool IsSenderInAllowedUsernames(
        TelegramAccountMessageUpdate update,
        IReadOnlyCollection<string>? allowedUsernames)
    {
        if (allowedUsernames == null || allowedUsernames.Count == 0)
            return false;

        var candidates = new[]
        {
            update.SenderUsername,
            update.SenderChatUsername,
            update.SenderPostAuthor
        };

        foreach (var candidate in candidates)
        {
            var normalized = (candidate ?? string.Empty).Trim().TrimStart('@');
            if (normalized.Length == 0)
                continue;

            foreach (var allowed in allowedUsernames)
            {
                if (string.IsNullOrWhiteSpace(allowed))
                    continue;

                var normalizedAllowed = allowed.Trim().TrimStart('@');
                if (normalizedAllowed.Length == 0)
                    continue;

                if (string.Equals(normalizedAllowed, normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        foreach (var allowed in allowedUsernames)
        {
            if (!TryParseAllowedSenderId(allowed, out var id, out var kind))
                continue;

            if (kind == AllowedSenderIdKind.User)
            {
                if (update.SenderUserId.HasValue && update.SenderUserId.Value == id)
                    return true;
            }
            else if (kind == AllowedSenderIdKind.Chat)
            {
                if (update.SenderChatId.HasValue && update.SenderChatId.Value == id)
                    return true;
            }
            else
            {
                if ((update.SenderUserId.HasValue && update.SenderUserId.Value == id)
                    || (update.SenderChatId.HasValue && update.SenderChatId.Value == id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private enum AllowedSenderIdKind
    {
        Any = 0,
        User = 1,
        Chat = 2
    }

    private static bool TryParseAllowedSenderId(
        string? raw,
        out long id,
        out AllowedSenderIdKind kind)
    {
        id = 0;
        kind = AllowedSenderIdKind.Any;

        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
            return false;

        if (value.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
        {
            kind = AllowedSenderIdKind.User;
            value = value[5..].Trim();
        }
        else if (value.StartsWith("chat:", StringComparison.OrdinalIgnoreCase)
                 || value.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
        {
            kind = AllowedSenderIdKind.Chat;
            value = value.Contains(':')
                ? value[(value.IndexOf(':') + 1)..].Trim()
                : string.Empty;
        }
        else if (value.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
        {
            kind = AllowedSenderIdKind.Any;
            value = value[3..].Trim();
        }

        if (value.StartsWith("-100", StringComparison.Ordinal) && value.Length > 4)
        {
            if (long.TryParse(value[4..], out var parsedChatId))
            {
                id = parsedChatId;
                if (kind == AllowedSenderIdKind.Any)
                    kind = AllowedSenderIdKind.Chat;
                return true;
            }
        }

        if (long.TryParse(value, out var parsed))
        {
            id = parsed;
            return true;
        }

        return false;
    }

    private async Task<TelegramVerificationMessageCandidate?> BuildVerificationCandidateAsync(
        Client client,
        Message message,
        string? currentUsername,
        int sentMessageId,
        CancellationToken cancellationToken)
    {
        var buttons = ExtractInlineButtons(message);
        var imageJpegBytes = await TryDownloadVerificationImageAsync(client, message, cancellationToken);
        var text = (message.message ?? string.Empty).Trim();

        if (buttons.Count == 0 && text.Length == 0 && (imageJpegBytes == null || imageJpegBytes.Length == 0))
            return null;

        return new TelegramVerificationMessageCandidate(
            MessageId: message.id,
            Text: text.Length == 0 ? null : text,
            ImageJpegBytes: imageJpegBytes,
            Buttons: buttons,
            MentionsAccount: ContainsUsernameMention(message.message, currentUsername),
            IsReplyToSentMessage: message.ReplyHeader?.reply_to_msg_id == sentMessageId,
            DateUtc: message.Date.ToUniversalTime());
    }

    private static bool IsSamePeer(InputPeer targetPeer, Peer actualPeer)
    {
        return (targetPeer, actualPeer) switch
        {
            (InputPeerChannel targetChannel, PeerChannel actualChannel) => targetChannel.channel_id == actualChannel.channel_id,
            (InputPeerChat targetChat, PeerChat actualChat) => targetChat.chat_id == actualChat.chat_id,
            (InputPeerUser targetUser, PeerUser actualUser) => targetUser.user_id == actualUser.user_id,
            _ => false
        };
    }

    private static bool ContainsUsernameMention(string? text, string? currentUsername)
    {
        var username = (currentUsername ?? string.Empty).Trim().TrimStart('@');
        if (username.Length == 0)
            return false;

        var messageText = (text ?? string.Empty).Trim();
        if (messageText.Length == 0)
            return false;

        return messageText.Contains($"@{username}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeVerificationChallenge(TelegramAccountMessageUpdate update)
    {
        if (update.Buttons.Count > 0 || update.HasVisualMedia)
            return true;

        var text = update.Text;
        if (text.Length == 0)
            return false;

        if (ContainsAny(text, "垃圾广告", "广告", "不予处理", "已删除", "违规", "封禁")
            && !ContainsAny(text, "验证", "验证码", "校验", "captcha"))
        {
            return false;
        }

        if (ContainsAny(text,
                "验证",
                "验证码",
                "校验",
                "请选择",
                "点击",
                "按钮",
                "完成验证",
                "请回复",
                "答案",
                "算式",
                "等于多少",
                "reply",
                "captcha"))
        {
            return true;
        }

        return LooksLikeMathChallenge(text);
    }

    private static bool LooksLikeMathChallenge(string text)
    {
        var digitCount = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
                digitCount++;
        }

        if (digitCount < 2)
            return false;

        return text.IndexOf('+') >= 0
               || text.IndexOf('-') >= 0
               || text.IndexOf('*') >= 0
               || text.IndexOf('/') >= 0
               || text.Contains("×", StringComparison.Ordinal)
               || text.Contains("÷", StringComparison.Ordinal)
               || text.Contains("＝", StringComparison.Ordinal)
               || text.IndexOf('=') >= 0;
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword)
                && text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<TelegramInlineButtonOption> ExtractInlineButtons(Message message)
    {
        if (message.reply_markup is not ReplyInlineMarkup markup)
            return new List<TelegramInlineButtonOption>();

        var result = new List<TelegramInlineButtonOption>();
        var index = 0;
        foreach (var row in markup.rows ?? Array.Empty<KeyboardButtonRow>())
        {
            var buttons = row?.buttons;
            if (buttons == null || buttons.Length == 0)
                continue;

            foreach (var button in buttons)
            {
                if (button is KeyboardButtonCallback callback && callback.data is { Length: > 0 })
                {
                    result.Add(new TelegramInlineButtonOption(index, callback.text ?? string.Empty, callback.data));
                    index++;
                }
            }
        }

        return result;
    }

    private async Task<byte[]?> TryDownloadVerificationImageAsync(Client client, Message message, CancellationToken cancellationToken)
    {
        try
        {
            return message.media switch
            {
                MessageMediaPhoto { photo: Photo photo } => await DownloadPhotoAsJpegAsync(client, photo, cancellationToken),
                MessageMediaDocument { document: Document document } => await DownloadDocumentPreviewAsJpegAsync(client, document, cancellationToken),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to download verification image from Telegram message {MessageId}", message.id);
            return null;
        }
    }

    private async Task<byte[]?> DownloadPhotoAsJpegAsync(Client client, Photo photo, CancellationToken cancellationToken)
    {
        await using var raw = new MemoryStream();
        await client.DownloadFileAsync(photo, raw, (PhotoSizeBase?)null);
        raw.Position = 0;

        await using var jpeg = await TelegramImageProcessor.PrepareStoredImageJpegAsync(raw, cancellationToken: cancellationToken);
        return jpeg.ToArray();
    }

    private async Task<byte[]?> DownloadDocumentPreviewAsJpegAsync(Client client, Document document, CancellationToken cancellationToken)
    {
        await using var raw = new MemoryStream();

        var thumb = document.thumbs?.OfType<PhotoSizeBase>().LastOrDefault();
        if (thumb != null)
        {
            await client.DownloadFileAsync(document, raw, thumb);
        }
        else if ((document.mime_type ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            await client.DownloadFileAsync(document, raw, (PhotoSizeBase?)null);
        }
        else
        {
            return null;
        }

        raw.Position = 0;
        await using var jpeg = await TelegramImageProcessor.PrepareStoredImageJpegAsync(raw, cancellationToken: cancellationToken);
        return jpeg.ToArray();
    }

    private static bool TryParseChatIdCandidate(string raw, out long normalizedId)
    {
        normalizedId = 0;
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        if (s.StartsWith("+", StringComparison.Ordinal))
            return false;

        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;

        if (parsed < 0 && s.StartsWith("-100", StringComparison.Ordinal))
        {
            var suffix = s[4..];
            if (suffix.Length > 0 && long.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelId) && channelId > 0)
            {
                normalizedId = parsed;
                return true;
            }
        }

        normalizedId = parsed;
        return true;
    }

    private static long BuildChannelBotApiChatId(long channelId)
    {
        var text = "-100" + channelId.ToString(CultureInfo.InvariantCulture);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return channelId;
    }

    private static string NormalizeChatTitle(string? title, string fallback)
    {
        var text = (title ?? string.Empty).Trim();
        return text.Length == 0 ? fallback : text;
    }

    private static string NormalizeTelegramJoinUrl(string input)
    {
        var s = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException("链接/用户名为空", nameof(input));

        // tg://join?invite=xxxx
        if (s.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
        {
            var inviteKey = "invite=";
            var idx = s.IndexOf(inviteKey, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var hash = s[(idx + inviteKey.Length)..];
                var amp = hash.IndexOf('&');
                if (amp >= 0)
                    hash = hash[..amp];
                hash = hash.Trim();
                if (!string.IsNullOrWhiteSpace(hash))
                    return $"https://t.me/+{hash}";
            }
        }

        // 直接是 t.me/xxx
        if (s.StartsWith("t.me/", StringComparison.OrdinalIgnoreCase) || s.StartsWith("telegram.me/", StringComparison.OrdinalIgnoreCase))
            return "https://" + s;

        // @username / username
        if (s.StartsWith("@", StringComparison.Ordinal))
            s = s.TrimStart('@');

        if (!s.Contains("://", StringComparison.Ordinal))
            return $"https://t.me/{s}";

        return s;
    }

    private static (string Username, string StartFromLink) NormalizeTelegramBotUsername(string input, bool assumeBotUsername = false)
    {
        var s = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException("Bot 用户名为空", nameof(input));

        string startFromLink = string.Empty;

        // tg://resolve?domain=xxxbot&start=abc
        if (s.StartsWith("tg://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(s, UriKind.Absolute, out var tgUri))
        {
            var query = ParseQueryString(tgUri.Query);
            if (query.TryGetValue("domain", out var domain) && !string.IsNullOrWhiteSpace(domain))
                s = domain.Trim();
            if (query.TryGetValue("start", out var start) && !string.IsNullOrWhiteSpace(start))
                startFromLink = NormalizeBotStartParameter(start);
        }

        // https://t.me/xxxbot?start=abc 或 t.me/xxxbot?start=abc
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("t.me/", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("telegram.me/", StringComparison.OrdinalIgnoreCase))
        {
            var url = s.Contains("://", StringComparison.Ordinal) ? s : "https://" + s;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new ArgumentException("Bot 链接格式无效", nameof(input));

            var path = (uri.AbsolutePath ?? string.Empty).Trim('/');
            var firstSeg = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(firstSeg))
                throw new ArgumentException("Bot 链接中缺少用户名", nameof(input));

            s = firstSeg;

            var query = ParseQueryString(uri.Query);
            if (query.TryGetValue("start", out var start) && !string.IsNullOrWhiteSpace(start))
                startFromLink = NormalizeBotStartParameter(start);
        }

        s = s.Trim().TrimStart('@');

        // 支持：@username?start=abc（无 http/tg 协议）
        var question = s.IndexOf('?');
        if (question >= 0)
        {
            var query = ParseQueryString(s[(question + 1)..]);
            if (query.TryGetValue("start", out var start) && !string.IsNullOrWhiteSpace(start))
                startFromLink = NormalizeBotStartParameter(start);

            s = s[..question];
        }

        var slash = s.IndexOf('/');
        if (slash >= 0)
            s = s[..slash];

        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException("Bot 用户名为空", nameof(input));

        if (s.StartsWith("+", StringComparison.Ordinal))
            throw new ArgumentException("邀请链接不是 Bot 用户名，请输入 @xxxbot 或 t.me/xxxbot", nameof(input));

        if (!System.Text.RegularExpressions.Regex.IsMatch(s, "^[A-Za-z0-9_]{5,64}$"))
            throw new ArgumentException("Bot 用户名格式无效", nameof(input));

        // 常规情况：要求以 bot 结尾
        // 例外：
        // 1) 显式给了 start 参数（常见于 t.me/xxx?start=abc 或 @xxx?start=abc）
        // 2) 调用方明确“按 Bot 处理”
        if (!s.EndsWith("bot", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(startFromLink)
            && !assumeBotUsername)
            throw new ArgumentException("目标看起来不是 Bot 用户名（需以 bot 结尾）", nameof(input));

        return (s, startFromLink);
    }

    internal static bool TryNormalizeTelegramBotUsername(string? input, out string username)
    {
        username = string.Empty;
        try
        {
            var normalized = NormalizeTelegramBotUsername(input ?? string.Empty);
            username = normalized.Username;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeBotStartParameter(string? input)
    {
        var s = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        if (s.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
            s = s[6..].Trim();

        if (s.StartsWith("@", StringComparison.Ordinal))
        {
            var idx = s.IndexOf(' ');
            s = idx > 0 ? s[(idx + 1)..].Trim() : string.Empty;
        }

        return s;
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
            return map;

        var raw = query.StartsWith("?", StringComparison.Ordinal) ? query[1..] : query;
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx < 0)
            {
                var kOnly = Uri.UnescapeDataString(part).Trim();
                if (!string.IsNullOrWhiteSpace(kOnly))
                    map[kOnly] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(part[..idx]).Trim();
            var val = Uri.UnescapeDataString(part[(idx + 1)..]).Trim();
            if (!string.IsNullOrWhiteSpace(key))
                map[key] = val;
        }

        return map;
    }

    /// <summary>
    /// 更新当前账号头像（静态图片）。
    /// </summary>
    public async Task<(bool Success, string? Error)> UpdateProfilePhotoAsync(
        int accountId,
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (fileStream == null)
                return (false, "头像文件为空");

            fileName = (fileName ?? "avatar.jpg").Trim();
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "avatar.jpg";

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await using var encoded = await TelegramImageProcessor.PrepareAvatarJpegAsync(fileStream, cancellationToken);
            var inputFile = await client.UploadFileAsync(encoded, "avatar.jpg");
            cancellationToken.ThrowIfCancellationRequested();

            if (inputFile == null)
                return (false, "头像上传失败：上传结果为空");

            await client.Photos_UploadProfilePhoto(inputFile, video: null, video_start_ts: null, video_emoji_markup: null, bot: null, fallback: false);
            return (true, null);
        }
        catch (UnknownImageFormatException)
        {
            return (false, "头像上传失败：不支持的图片格式（建议使用 JPG/PNG）");
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg);
        }
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

    private static string NormalizeUploadFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Trim());
        return string.IsNullOrWhiteSpace(name) ? "image.jpg" : name;
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

        var absoluteSessionPath = _sessionPathResolver.Resolve(account.SessionPath);
        // 先释放可能仍会保存旧 Session 的客户端，避免其在转换完成后覆盖新文件。
        await _clientPool.RemoveClientAsync(accountId);
        if (File.Exists(absoluteSessionPath) && SessionDataConverter.LooksLikeSqliteSession(absoluteSessionPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var proxyResolution = await _proxyResolver.ResolveAsync(accountId, cancellationToken);
            var proxy = proxyResolution.Proxy
                ?? (proxyResolution.UseGlobalProxy
                    ? throw new InvalidOperationException(
                        "全局代理路由未在 Session 转换前解析，已阻止降级为直连")
                    : null);

            var converted = await SessionDataConverter.TryConvertSqliteSessionFromJsonAsync(
                phone: account.Phone,
                apiId: apiId,
                apiHash: apiHash,
                sqliteSessionPath: absoluteSessionPath,
                logger: _logger,
                proxy: proxy,
                cancellationToken: cancellationToken
            );

            if (!converted.Ok)
            {
                throw new InvalidOperationException(
                    $"该账号的 Session 文件为 SQLite 格式：{account.SessionPath}，无法自动转换为可用 session。" +
                    $"原因：{converted.Reason}。建议：重新导入包含 session_string 的 json，或到【账号-手机号登录】重新登录生成新的 sessions/*.session。");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = await _clientPool.GetOrCreateClientAsync(
            accountId: accountId,
            apiId: apiId,
            apiHash: apiHash,
            sessionPath: absoluteSessionPath,
            sessionKey: sessionKey,
            phoneNumber: account.Phone,
            userId: account.UserId > 0 ? account.UserId : null);

        try
        {
            await ExecuteTelegramRequestAsync(
                accountId,
                "连接 Telegram",
                () => client.ConnectAsync(),
                cancellationToken,
                resetClientOnTimeout: true);
            cancellationToken.ThrowIfCancellationRequested();
            if (client.User == null && (client.UserId != 0 || account.UserId != 0))
            {
                await ExecuteTelegramRequestAsync(
                    accountId,
                    "恢复 Telegram 登录状态",
                    () => client.LoginUserIfNeeded(reloginOnFailedResume: false),
                    cancellationToken,
                    resetClientOnTimeout: true);
            }
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

    private TimeSpan GetTelegramRequestTimeout()
    {
        var seconds = int.TryParse(_configuration["Telegram:RequestTimeoutSeconds"], out var parsedSeconds)
            ? parsedSeconds
            : 90;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 600));
    }

    private async Task ExecuteTelegramRequestAsync(
        int accountId,
        string operation,
        Func<Task> action,
        CancellationToken cancellationToken,
        bool resetClientOnTimeout)
    {
        var timeout = GetTelegramRequestTimeout();

        try
        {
            await action().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Telegram request timed out after {TimeoutSeconds}s for account {AccountId}: {Operation}",
                timeout.TotalSeconds,
                accountId,
                operation);

            if (resetClientOnTimeout)
                await _clientPool.RemoveClientAsync(accountId);

            throw new TimeoutException($"Telegram 请求超时：{operation} 超过 {timeout.TotalSeconds:0} 秒，可能是 Session 失效、账号受限、网络异常或代理异常");
        }
    }

    private async Task<T> ExecuteTelegramRequestAsync<T>(
        int accountId,
        string operation,
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        bool resetClientOnTimeout)
    {
        var timeout = GetTelegramRequestTimeout();

        try
        {
            return await action().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Telegram request timed out after {TimeoutSeconds}s for account {AccountId}: {Operation}",
                timeout.TotalSeconds,
                accountId,
                operation);

            if (resetClientOnTimeout)
                await _clientPool.RemoveClientAsync(accountId);

            throw new TimeoutException($"Telegram 请求超时：{operation} 超过 {timeout.TotalSeconds:0} 秒，可能是 Session 失效、账号受限、网络异常或代理异常");
        }
    }

    private int ResolveApiId(Account account)
    {
        if (account.ApiId > 0)
            return account.ApiId;
        if (int.TryParse(_configuration["Telegram:ApiId"], out var globalApiId) && globalApiId > 0)
            return globalApiId;
        throw new InvalidOperationException("账号缺少 ApiId，且未配置全局 ApiId");
    }

    private string ResolveApiHash(Account account)
    {
        if (!string.IsNullOrWhiteSpace(account.ApiHash))
            return account.ApiHash.Trim();
        var global = _configuration["Telegram:ApiHash"];
        if (!string.IsNullOrWhiteSpace(global))
            return global.Trim();
        throw new InvalidOperationException("账号缺少 ApiHash，且未配置全局 ApiHash");
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
                updates = await ExecuteTelegramRequestAsync(
                    accountId,
                    "创建测试频道探测账号状态",
                    () => client.Channels_CreateChannel(title: title, about: about, broadcast: true),
                    cancellationToken,
                    resetClientOnTimeout: true);
            }
            catch (RpcException ex) when (ex.Code == 420 && string.Equals(ex.Message, "FROZEN_METHOD_INVALID", StringComparison.OrdinalIgnoreCase))
            {
                return new CreateChannelProbeResult(false, true, "账号/ApiId 受限：Telegram 返回 FROZEN_METHOD_INVALID（创建频道接口被冻结）");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var channel = updates.Chats.Values.OfType<TL.Channel>().FirstOrDefault();
            if (channel == null)
                return new CreateChannelProbeResult(false, false, "创建测试频道失败：未返回 Channel");

            try
            {
                // 立即删除，避免留下垃圾频道
                var input = new InputChannel(channel.id, channel.access_hash);
                await ExecuteTelegramRequestAsync(
                    accountId,
                    $"删除测试频道({channel.id})",
                    () => client.Channels_DeleteChannel(input),
                    cancellationToken,
                    resetClientOnTimeout: false);
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

    /// <summary>
    /// 将 Telegram 异常映射为可读的摘要和详情。
    /// </summary>
    private static bool IsBotCallbackTimeout(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("BOT_RESPONSE_TIMEOUT", StringComparison.OrdinalIgnoreCase);
    }

    public static (string summary, string details) MapTelegramException(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;

        if (ex is TimeoutException
            || msg.Contains("请求超时", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return ("请求超时", msg);

        if (msg.Contains("EMAIL_HASH_EXPIRED", StringComparison.OrdinalIgnoreCase))
            return (
                "邮箱验证码已过期（EMAIL_HASH_EXPIRED）",
                "请点击“重发验证码”，并使用最新邮件中的验证码。" + Environment.NewLine + msg);

        if (msg.Contains("EMAIL_NOT_SETUP", StringComparison.OrdinalIgnoreCase))
            return ("登录邮箱未启用（EMAIL_NOT_SETUP）", "该账号未处于可设置/可变更登录邮箱的状态（通常需要登录流程触发设置）。" + Environment.NewLine + msg);

        if (msg.Contains("EMAIL_UNCONFIRMED", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(msg, "(EMAIL_UNCONFIRMED(?:_[A-Z0-9]+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var code = m.Success ? m.Groups[1].Value.ToUpperInvariant() : "EMAIL_UNCONFIRMED";
            return (
                $"邮箱未确认（{code}）",
                "请在面板输入邮箱收到的验证码进行确认；如提示过期请重发并使用最新验证码。" + Environment.NewLine + msg);
        }

        if (msg.Contains("EMAIL_TOKEN_INVALID", StringComparison.OrdinalIgnoreCase))
            return ("邮箱验证码错误（EMAIL_TOKEN_INVALID）", "验证码不正确或不是最新验证码。请点击“重发验证码”，并使用最新邮件中的验证码。" + Environment.NewLine + msg);

        if (msg.Contains("EMAIL_INVALID", StringComparison.OrdinalIgnoreCase))
            return ("邮箱无效（EMAIL_INVALID）", msg);

        if (msg.Contains("EMAIL_NOT_ALLOWED", StringComparison.OrdinalIgnoreCase))
            return ("邮箱不允许使用（EMAIL_NOT_ALLOWED）", msg);

        if (msg.Contains("FROZEN_METHOD_INVALID", StringComparison.OrdinalIgnoreCase))
            return ("账号被冻结（FROZEN_METHOD_INVALID）", "Telegram 提示该账号/ApiId 的某些接口被冻结（常见为创建频道接口）。" + Environment.NewLine + msg);

        if (msg.Contains("CHANNELS_TOO_MUCH", StringComparison.OrdinalIgnoreCase))
            return ("频道/群组数量已达上限（CHANNELS_TOO_MUCH）", "该账号已达到 Telegram 允许创建的频道或超级群组数量上限，请清理不需要的频道/群组或更换账号后重试。" + Environment.NewLine + msg);

        if (msg.Contains("USER_RESTRICTED", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase))
            return ("账号受限，无法创建频道/群组", "Telegram 限制了该账号的操作权限，请在官方客户端确认账号状态，或更换正常账号后重试。" + Environment.NewLine + msg);

        if (msg.Contains("API_ID_INVALID", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("API_ID_PUBLISHED_FLOOD", StringComparison.OrdinalIgnoreCase))
            return ("Telegram API 配置无效", "请在系统设置中核对 ApiId/ApiHash，并使用与 Session 匹配的 API 配置后重试。" + Environment.NewLine + msg);

        if (msg.Contains("USERNAME_INVALID", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("USERNAME_OCCUPIED", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("USERNAME_NOT_MODIFIED", StringComparison.OrdinalIgnoreCase))
            return ("公开用户名不可用", "请使用符合 Telegram 规则且未被占用的用户名（仅字母、数字和下划线，且以字母开头）。" + Environment.NewLine + msg);

        if (msg.Contains("CHAT_ADMIN_REQUIRED", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("RIGHT_FORBIDDEN", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("CHAT_WRITE_FORBIDDEN", StringComparison.OrdinalIgnoreCase))
            return ("账号权限不足", "当前执行账号没有完成该操作所需的权限，请使用频道/群组创建者账号重试。" + Environment.NewLine + msg);

        if (msg.Contains("PEER_FLOOD", StringComparison.OrdinalIgnoreCase))
            return ("账号触发 Telegram 风控（PEER_FLOOD）", "请暂停批量操作，等待风控解除后再试，必要时更换账号。" + Environment.NewLine + msg);

        if (msg.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase))
            return ("触发限流（FLOOD_WAIT）", msg);

        if (msg.Contains("CHANNEL_MONOFORUM_UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
            return ("群组接口不支持（CHANNEL_MONOFORUM_UNSUPPORTED）", msg);

        if (msg.Contains("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase))
            return ("Session 失效（AUTH_KEY_UNREGISTERED）", msg);

        if (msg.Contains("session 已失效", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("session已失效", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("账号未登录", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
            return ("Session 失效", msg);

        if (msg.Contains("AUTH_KEY_DUPLICATED", StringComparison.OrdinalIgnoreCase))
            return ("Session 冲突（AUTH_KEY_DUPLICATED）", "该 Session 可能在其他设备/应用上同时使用，导致密钥冲突。" + Environment.NewLine + msg);

        if (msg.Contains("SESSION_REVOKED", StringComparison.OrdinalIgnoreCase))
            return ("Session 已被撤销（SESSION_REVOKED）", "该 Session 已被注销或撤销，需要重新登录。" + Environment.NewLine + msg);

        if (msg.Contains("SESSION_PASSWORD_NEEDED", StringComparison.OrdinalIgnoreCase))
            return ("需要两步验证密码（SESSION_PASSWORD_NEEDED）", msg);

        if (msg.Contains("CODE_INVALID", StringComparison.OrdinalIgnoreCase))
            return ("验证码错误（CODE_INVALID）", "验证码不正确或不是最新验证码。请点击“重发验证码”，并使用最新邮件中的验证码。" + Environment.NewLine + msg);

        if (msg.Contains("PHOTO_FILE_MISSING", StringComparison.OrdinalIgnoreCase))
            return ("头像上传失败（PHOTO_FILE_MISSING）", msg);

        if (msg.Contains("PHONE_NUMBER_BANNED", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("USER_DEACTIVATED_BAN", StringComparison.OrdinalIgnoreCase))
            return ("账号被封禁/停用", msg);

        if (msg.Contains("Can't read session block", StringComparison.OrdinalIgnoreCase))
            return ("Session 无法读取（ApiHash/Key 不匹配或损坏）", msg);

        return ("连接失败", msg);
    }
}
