using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Mail;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
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
            var raw = (linkOrUsername ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return (false, "链接/用户名为空", null);

            var url = NormalizeTelegramJoinUrl(raw);

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var chat = await client.AnalyzeInviteLink(url, join: true);
            cancellationToken.ThrowIfCancellationRequested();

            var title = chat switch
            {
                TL.Channel c => c.title,
                TL.Chat c => c.title,
                _ => null
            };

            return (true, null, title);
        }
        catch (RpcException ex) when (ex.Code == 400 && string.Equals(ex.Message, "USER_ALREADY_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
        {
            return (true, null, "已在群组/频道中");
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
            var raw = (linkOrUsername ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return (false, "链接/用户名为空", null);

            var url = NormalizeTelegramJoinUrl(raw);

            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // 解析目标（不加入）
            var chat = await client.AnalyzeInviteLink(url, join: false);
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

            await client.LeaveChat(peer);
            return (true, null, title);
        }
        catch (RpcException ex) when (ex.Code == 400 && string.Equals(ex.Message, "USER_NOT_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
        {
            return (true, null, "未在群组/频道中");
        }
        catch (Exception ex)
        {
            var (summary, details) = MapTelegramException(ex);
            var msg = string.IsNullOrWhiteSpace(details) ? summary : $"{summary}：{details}";
            return (false, msg, null);
        }
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

            // 先把原图读入内存，然后做“居中裁剪为正方形 + 缩放到 512x512 + JPEG 压缩”，再上传给 Telegram。
            // 这样可以避免：原图过大、长宽比异常、某些上传流读取不稳定等问题。
            await using var raw = new MemoryStream();
            if (fileStream.CanSeek)
                fileStream.Position = 0;

            await fileStream.CopyToAsync(raw, cancellationToken);
            raw.Position = 0;

            using var image = await Image.LoadAsync(raw, cancellationToken);
            image.Mutate(x => x.AutoOrient());

            const int targetSize = 512;
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Crop,
                Size = new Size(targetSize, targetSize)
            }));

            await using var encoded = new MemoryStream();
            await image.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = 85 }, cancellationToken);
            encoded.Position = 0;

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

            var converted = await SessionDataConverter.TryConvertSqliteSessionFromJsonAsync(
                phone: account.Phone,
                apiId: account.ApiId,
                apiHash: account.ApiHash,
                sqliteSessionPath: absoluteSessionPath,
                logger: _logger
            );

            if (!converted.Ok)
            {
                throw new InvalidOperationException(
                    $"该账号的 Session 文件为 SQLite 格式：{account.SessionPath}，无法自动转换为可用 session。" +
                    $"原因：{converted.Reason}。建议：重新导入包含 session_string 的 json，或到【账号-手机号登录】重新登录生成新的 sessions/*.session。");
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

        if (msg.Contains("EMAIL_HASH_EXPIRED", StringComparison.OrdinalIgnoreCase))
            return (
                "邮箱验证码已过期（EMAIL_HASH_EXPIRED）",
                "请点击“重发验证码”，并使用最新邮件中的验证码。" + Environment.NewLine + msg);

        if (msg.Contains("EMAIL_UNCONFIRMED", StringComparison.OrdinalIgnoreCase))
            return (
                "邮箱未确认（EMAIL_UNCONFIRMED）",
                "请在面板输入邮箱收到的验证码进行确认；如提示过期请重发并使用最新验证码。" + Environment.NewLine + msg);

        if (msg.Contains("EMAIL_INVALID", StringComparison.OrdinalIgnoreCase))
            return ("邮箱无效（EMAIL_INVALID）", msg);

        if (msg.Contains("EMAIL_NOT_ALLOWED", StringComparison.OrdinalIgnoreCase))
            return ("邮箱不允许使用（EMAIL_NOT_ALLOWED）", msg);

        if (msg.Contains("FROZEN_METHOD_INVALID", StringComparison.OrdinalIgnoreCase))
            return ("账号被冻结（FROZEN_METHOD_INVALID）", "Telegram 提示该账号/ApiId 的某些接口被冻结（常见为创建频道接口）。" + Environment.NewLine + msg);

        if (msg.Contains("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase))
            return ("Session 失效（AUTH_KEY_UNREGISTERED）", msg);

        if (msg.Contains("SESSION_PASSWORD_NEEDED", StringComparison.OrdinalIgnoreCase))
            return ("需要两步验证密码（SESSION_PASSWORD_NEEDED）", msg);

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
