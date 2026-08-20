using System.Security.Claims;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using TelegramPanel.Web.ExternalApi;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Core.Utils;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;
using TelegramPanel.Web.Modules;
using TelegramPanel.Web.Services;
using Regex = System.Text.RegularExpressions.Regex;

namespace TelegramPanel.Web.Api;

public static class PanelAdminApiEndpoints
{
    private const string AccountRiskConfirmationRequiredCode = "ACCOUNT_RISK_CONFIRMATION_REQUIRED";
    internal const long AccountImportZipMaxFileSize = 200L * 1024 * 1024;
    internal const long AccountImportZipMaxRequestSize = AccountImportZipMaxFileSize + 1024 * 1024;

    public static void MapPanelAdminApi(this WebApplication app, bool requireAdminAuth)
    {
        var api = app.MapGroup("/api/panel");

        api.MapPost("/auth/login", LoginAsync)
            .DisableAntiforgery()
            .AllowAnonymous();

        api.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new OperationResultDto(true, "已退出登录"));
        }).AllowAnonymous();

        api.MapGet("/auth/me", (HttpContext http, AdminCredentialStore credentialStore) =>
        {
            if (!credentialStore.Enabled)
                return Results.Ok(new AuthMeDto(true, "admin", false, false, VersionService.Version));

            var authenticated = http.User.Identity?.IsAuthenticated == true;
            return Results.Ok(new AuthMeDto(
                authenticated,
                authenticated ? http.User.Identity?.Name : null,
                credentialStore.MustChangePassword,
                credentialStore.Enabled,
                VersionService.Version));
        });

        var secured = api.MapGroup("");
        if (requireAdminAuth)
            secured.RequireAuthorization();

        secured.MapProxyManagementApi();

        secured.MapGet("/summary", GetSummaryAsync);

        secured.MapGet("/account-categories", async (AccountCategoryManagementService categories) =>
        {
            var items = (await categories.GetAllCategoriesAsync())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToDto)
                .ToList();
            return Results.Ok(items);
        });
        secured.MapPost("/account-categories", CreateAccountCategoryAsync);
        secured.MapPut("/account-categories/{id:int}", UpdateAccountCategoryAsync);
        secured.MapDelete("/account-categories/{id:int}", DeleteAccountCategoryAsync);
        secured.MapPost("/account-categories/{id:int}/assignments", SaveAccountCategoryAssignmentsAsync);

        secured.MapGet("/accounts", GetAccountsAsync);
        secured.MapGet("/accounts/{id:int}", GetAccountAsync);
        secured.MapPut("/accounts/{id:int}", UpdateAccountAsync);
        secured.MapPatch("/accounts/{id:int}/active", SetAccountActiveAsync);
        secured.MapDelete("/accounts/{id:int}", DeleteAccountAsync);
        secured.MapPost("/accounts/{id:int}/telegram-status", RefreshTelegramStatusAsync);
        secured.MapPost("/accounts/telegram-status", BatchRefreshTelegramStatusAsync);
        secured.MapPost("/accounts/batch/category", BatchSetAccountCategoryAsync);
        secured.MapPost("/accounts/batch/delete", BatchDeleteAccountsAsync);
        secured.MapPost("/accounts/batch/kick-devices", BatchKickAllOtherDevicesAsync);
        secured.MapPost("/accounts/batch/avatar", BatchUpdateAvatarAsync).DisableAntiforgery();
        secured.MapPost("/accounts/cleanup-waste", CleanupWasteAccountsAsync);
        secured.MapGet("/accounts/{id:int}/system-messages", GetSystemMessagesAsync);
        secured.MapGet("/accounts/{id:int}/devices", GetDevicesAsync);
        secured.MapPost("/accounts/{id:int}/devices/{hash:long}/kick", KickDeviceAsync);
        secured.MapPost("/accounts/{id:int}/devices/kick-all", KickAllOtherDevicesAsync);
        secured.MapGet("/accounts/{id:int}/channels", GetAccountChannelsAsync);
        secured.MapGet("/accounts/{id:int}/groups", GetAccountGroupsAsync);
        secured.MapPost("/accounts/chat-membership", ChangeChatMembershipAsync);
        secured.MapPost("/accounts/chat-membership/risk-check", CheckChatMembershipRiskAsync);
        secured.MapPost("/accounts/{id:int}/profile", UpdateProfileAsync).DisableAntiforgery();
        secured.MapPost("/accounts/batch/profile", BatchUpdateProfileAsync);
        secured.MapPost("/accounts/two-factor/password", ChangeTwoFactorPasswordAsync);
        secured.MapPost("/accounts/two-factor/password/reset", RequestTwoFactorPasswordResetAsync);
        secured.MapGet("/accounts/{id:int}/two-factor/recovery-email", GetTwoFactorRecoveryEmailAsync);
        secured.MapPost("/accounts/{id:int}/two-factor/recovery-email", SetTwoFactorRecoveryEmailAsync);
        secured.MapPost("/accounts/{id:int}/two-factor/recovery-email/confirm", ConfirmTwoFactorRecoveryEmailAsync);
        secured.MapPost("/accounts/{id:int}/two-factor/recovery-email/resend", ResendTwoFactorRecoveryEmailAsync);
        secured.MapPost("/accounts/{id:int}/two-factor/recovery-email/cancel", CancelTwoFactorRecoveryEmailAsync);
        secured.MapGet("/accounts/{id:int}/login-email", GetLoginEmailAsync);
        secured.MapPost("/accounts/{id:int}/login-email", SetLoginEmailAsync);
        secured.MapPost("/accounts/{id:int}/login-email/confirm", ConfirmLoginEmailAsync);
        secured.MapPost("/accounts/batch/recovery-email", BatchChangeTwoFactorRecoveryEmailAsync);
        ConfigureAccountImportZipLimits(
            secured.MapPost("/accounts/import/zip", ImportAccountsZipAsync)
                .DisableAntiforgery());
        secured.MapPost("/accounts/import/session-files", ImportAccountsSessionFilesAsync).DisableAntiforgery();
        secured.MapPost("/accounts/import/string-session", ImportAccountsStringSessionAsync);
        secured.MapPost("/accounts/login/start", StartAccountLoginAsync);
        secured.MapPost("/accounts/login/qr/start", StartAccountQrLoginAsync);
        secured.MapPost("/accounts/login/qr/poll", PollAccountQrLoginAsync);
        secured.MapPost("/accounts/login/qr/password", SubmitAccountQrLoginPasswordAsync);
        secured.MapPost("/accounts/login/qr/cancel", CancelAccountQrLoginAsync);
        secured.MapPost("/accounts/login/code", SubmitAccountLoginCodeAsync);
        secured.MapPost("/accounts/login/resend", ResendAccountLoginCodeAsync);
        secured.MapPost("/accounts/login/password", SubmitAccountLoginPasswordAsync);
        secured.MapPost("/accounts/login/reset", ResetAccountLoginAsync);

        secured.MapGet("/operation-accounts", GetOperationAccountsAsync);

        secured.MapGet("/settings", GetSettingsAsync);
        secured.MapGet("/settings/device-profiles", GetDeviceProfilesAsync);
        secured.MapPost("/version-info/check", CheckVersionInfoAsync);
        secured.MapPost("/version-info/apply", ApplyVersionUpdateAsync);
        secured.MapPost("/version-info/mode", SaveUpdateModeAsync);
        secured.MapPost("/system/restart", RestartSystemAsync);
        secured.MapPost("/settings/telegram-api", SaveTelegramApiSettingsAsync);
        secured.MapGet("/settings/global-proxy", GetGlobalProxySettingsAsync);
        secured.MapPost("/settings/global-proxy", SaveGlobalProxySettingsEndpointAsync);
        secured.MapPost("/settings/cloud-mail", SaveCloudMailSettingsAsync);
        secured.MapPost("/settings/cloud-mail/token", GenerateCloudMailTokenAsync);
        secured.MapPost("/settings/ai", SaveAiSettingsAsync);
        secured.MapPost("/settings/ai/test", TestAiSettingsAsync);
        secured.MapPost("/settings/batch", SaveBatchSettingsAsync);
        secured.MapPost("/settings/time-zone", SaveTimeZoneSettingsAsync);
        secured.MapPost("/settings/sync", SaveSyncSettingsAsync);
        secured.MapPost("/settings/sync-now", StartSyncNowAsync);
        secured.MapPost("/settings/bot-auto-sync", SaveBotAutoSyncSettingsAsync);
        secured.MapPost("/settings/telegram-status", SaveTelegramStatusSettingsAsync);
        secured.MapPost("/settings/logging", SaveLoggingSettingsAsync);
        secured.MapGet("/settings/bucket-backup", GetBucketBackupSettings);
        secured.MapPost("/settings/bucket-backup", SaveBucketBackupSettingsAsync);
        secured.MapPost("/settings/bucket-backup/run", RunBucketBackupAsync);
        secured.MapPost("/settings/cache/clear", ClearCacheAsync);
        secured.MapPost("/settings/username", ChangeAdminUsernameAsync);
        secured.MapPost("/settings/password", ChangeAdminPasswordAsync);
        secured.MapPost("/settings/password/verify", VerifyAdminPasswordAsync);

        secured.MapGet("/channels", GetChannelsPageAsync);
        secured.MapGet("/channels/{id:int}", GetChannelDetailAsync);
        secured.MapGet("/channels/{id:int}/admins", GetChannelAdminsAsync);
        secured.MapPost("/channels", CreateChannelAsync);
        secured.MapPut("/channels/{id:int}", UpdateChannelAsync).DisableAntiforgery();
        secured.MapPatch("/channels/{id:int}/group", SetChannelGroupAsync);
        secured.MapDelete("/channels/{id:int}", DeleteChannelAsync);
        secured.MapPost("/channels/batch/group", BatchSetChannelGroupAsync);
        secured.MapPost("/channels/batch/delete", BatchDeleteChannelsAsync);
        secured.MapPost("/channels/batch/invite", BatchInviteChannelsAsync);
        secured.MapPost("/channels/batch/admins", BatchSetChannelAdminsAsync);
        secured.MapPost("/channels/batch/kick", BatchKickChannelUsersAsync);
        secured.MapPost("/channels/sync", SyncChannelsAsync);
        secured.MapPost("/channels/{id:int}/export-link", ExportChannelLinkAsync);
        secured.MapPost("/channels/{id:int}/leave", LeaveChannelAsync);
        secured.MapPost("/channels/{id:int}/disband", DisbandChannelAsync);
        secured.MapPost("/channels/{id:int}/transfer-owner", TransferChannelOwnerAsync);

        secured.MapGet("/channel-groups", GetChannelGroupsAsync);
        secured.MapPost("/channel-groups", CreateChannelGroupAsync);
        secured.MapPut("/channel-groups/{id:int}", UpdateChannelGroupAsync);
        secured.MapDelete("/channel-groups/{id:int}", DeleteChannelGroupAsync);
        secured.MapPost("/channel-groups/{id:int}/assignments", SaveChannelGroupAssignmentsAsync);

        secured.MapGet("/groups", GetGroupsPageAsync);
        secured.MapGet("/groups/{id:int}", GetGroupDetailAsync);
        secured.MapGet("/groups/{id:int}/admins", GetGroupAdminsAsync);
        secured.MapPost("/groups", CreateGroupAsync);
        secured.MapPut("/groups/{id:int}", UpdateGroupAsync).DisableAntiforgery();
        secured.MapPatch("/groups/{id:int}/category", SetGroupCategoryAsync);
        secured.MapDelete("/groups/{id:int}", DeleteGroupAsync);
        secured.MapPost("/groups/batch/category", BatchSetGroupCategoryAsync);
        secured.MapPost("/groups/batch/delete", BatchDeleteGroupsAsync);
        secured.MapPost("/groups/batch/invite", BatchInviteGroupsAsync);
        secured.MapPost("/groups/batch/admins", BatchSetGroupAdminsAsync);
        secured.MapPost("/groups/batch/kick", BatchKickGroupUsersAsync);
        secured.MapPost("/groups/sync", SyncGroupsAsync);
        secured.MapPost("/groups/{id:int}/export-link", ExportGroupLinkAsync);
        secured.MapPost("/groups/{id:int}/leave", LeaveGroupAsync);
        secured.MapPost("/groups/{id:int}/disband", DisbandGroupAsync);
        secured.MapPost("/groups/{id:int}/transfer-owner", TransferGroupOwnerAsync);

        secured.MapGet("/group-categories", GetGroupCategoriesAsync);
        secured.MapPost("/group-categories", CreateGroupCategoryAsync);
        secured.MapPut("/group-categories/{id:int}", UpdateGroupCategoryAsync);
        secured.MapDelete("/group-categories/{id:int}", DeleteGroupCategoryAsync);
        secured.MapPost("/group-categories/{id:int}/assignments", SaveGroupCategoryAssignmentsAsync);

        secured.MapGet("/bots", GetBotsAsync);
        secured.MapPost("/bots", CreateBotAsync);
        secured.MapPut("/bots/{id:int}", UpdateBotAsync);
        secured.MapPatch("/bots/{id:int}/active", SetBotActiveAsync);
        secured.MapDelete("/bots/{id:int}", DeleteBotAsync);
        secured.MapGet("/bot-channel-categories", GetBotChannelCategoriesAsync);
        secured.MapPost("/bot-channel-categories", CreateBotChannelCategoryAsync);
        secured.MapPut("/bot-channel-categories/{id:int}", UpdateBotChannelCategoryAsync);
        secured.MapDelete("/bot-channel-categories/{id:int}", DeleteBotChannelCategoryAsync);
        secured.MapGet("/bot-channels", GetBotChannelsPageAsync);
        secured.MapGet("/bot-channels/{id:int}", GetBotChannelDetailAsync);
        secured.MapGet("/bot-channels/{id:int}/admins", GetBotChannelAdminsAsync);
        secured.MapPut("/bot-channels/{id:int}", UpdateBotChannelAsync).DisableAntiforgery();
        secured.MapPatch("/bot-channels/{id:int}/category", SetBotChannelCategoryAsync);
        secured.MapPost("/bot-channels/batch/category", BatchSetBotChannelCategoryAsync);
        secured.MapPost("/bot-channels/batch/delete", BatchDeleteBotChannelsAsync);
        secured.MapPost("/bot-channels/batch/status", CheckBotChannelStatusAsync);
        secured.MapPost("/bot-channels/batch/invite", InviteBotChannelMembersAsync);
        secured.MapPost("/bot-channels/batch/ban", BanBotChannelMembersAsync);
        secured.MapGet("/bot-channels/presets/invite", GetChannelInvitePresetsAsync);
        secured.MapPost("/bot-channels/presets/invite", SaveChannelInvitePresetAsync);
        secured.MapDelete("/bot-channels/presets/invite/{name}", DeleteChannelInvitePresetAsync);
        secured.MapGet("/bot-channels/presets/admin-usernames", GetChannelAdminPresetsAsync);
        secured.MapPost("/bot-channels/presets/admin-usernames", SaveChannelAdminPresetAsync);
        secured.MapDelete("/bot-channels/presets/admin-usernames/{name}", DeleteChannelAdminPresetAsync);
        secured.MapGet("/bot-channels/presets/admin-user-ids", GetBotAdminPresetsAsync);
        secured.MapPost("/bot-channels/presets/admin-user-ids", SaveBotAdminPresetAsync);
        secured.MapDelete("/bot-channels/presets/admin-user-ids/{name}", DeleteBotAdminPresetAsync);
        secured.MapGet("/bot-channels/admin-defaults/account", GetChannelAdminDefaultsAsync);
        secured.MapPost("/bot-channels/admin-defaults/account", SaveChannelAdminDefaultsAsync);
        secured.MapGet("/bot-channels/admin-defaults/bot", GetBotChannelAdminDefaultsAsync);
        secured.MapPost("/bot-channels/admin-defaults/bot", SaveBotChannelAdminDefaultsAsync);
        secured.MapPost("/bot-channels/tasks/admins-by-account", CreateBotAdminsByAccountTaskAsync);
        secured.MapPost("/bot-channels/tasks/admins-by-bot", CreateBotAdminsByBotTaskAsync);
        secured.MapPost("/bot-channels/sync", SyncBotChannelsAsync);
        secured.MapPost("/bot-channels/{id:int}/export-link", ExportBotChannelLinkAsync);

        secured.MapGet("/tasks", GetTasksAsync);
        secured.MapGet("/tasks/{id:int}", GetTaskAsync);
        secured.MapPost("/tasks", CreateTaskAsync);
        secured.MapPatch("/tasks/{id:int}", UpdateTaskAsync);
        secured.MapPost("/tasks/assets/avatar", UploadTaskAvatarAssetAsync).DisableAntiforgery();
        secured.MapPost("/tasks/cleanup", CleanupTasksAsync);
        secured.MapPost("/tasks/{id:int}/pause", PauseBatchTaskAsync);
        secured.MapPost("/tasks/{id:int}/resume", ResumeBatchTaskAsync);
        secured.MapPost("/tasks/{id:int}/cancel", CancelBatchTaskAsync);
        secured.MapDelete("/tasks/{id:int}", DeleteBatchTaskAsync);

        secured.MapGet("/scheduled-tasks/{id:int}", GetScheduledTaskAsync);
        secured.MapPost("/scheduled-tasks", CreateScheduledTaskAsync);
        secured.MapPut("/scheduled-tasks/{id:int}", UpdateScheduledTaskAsync);
        secured.MapPost("/scheduled-tasks/{id:int}/run-now", RunScheduledTaskNowAsync);
        secured.MapPost("/scheduled-tasks/{id:int}/pause", async (int id, ScheduledTaskService scheduledTasks) =>
        {
            await scheduledTasks.PauseAsync(id);
            return Results.Ok(new OperationResultDto(true, "计划任务已暂停"));
        });
        secured.MapPost("/scheduled-tasks/{id:int}/resume", async (int id, ScheduledTaskService scheduledTasks) =>
        {
            await scheduledTasks.ResumeAsync(id);
            return Results.Ok(new OperationResultDto(true, "计划任务已恢复"));
        });
        secured.MapDelete("/scheduled-tasks/{id:int}", async (int id, ScheduledTaskService scheduledTasks) =>
        {
            await scheduledTasks.DeleteAsync(id);
            return Results.Ok(new OperationResultDto(true, "计划任务已删除"));
        });

        secured.MapGet("/data-dictionaries", GetDataDictionariesAsync);
        secured.MapPost("/data-dictionaries/text", SaveTextDictionaryAsync);
        secured.MapPost("/data-dictionaries/image", SaveImageDictionaryAsync).DisableAntiforgery();
        secured.MapPatch("/data-dictionaries/{id:int}/enabled", SetDictionaryEnabledAsync);
        secured.MapPost("/data-dictionaries/{id:int}/reset-queue", async (int id, DataDictionaryService dictionaries) =>
        {
            await dictionaries.ResetQueueAsync(id);
            return Results.Ok(new OperationResultDto(true, "轮询游标已重置"));
        });
        secured.MapDelete("/data-dictionaries/{id:int}", async (int id, DataDictionaryService dictionaries) =>
        {
            await dictionaries.DeleteAsync(id);
            return Results.Ok(new OperationResultDto(true, "字典已删除"));
        });

        secured.MapGet("/modules", GetModulesAsync);
        secured.MapPost("/modules/install", InstallModuleAsync).DisableAntiforgery();
        secured.MapPost("/modules/{id}/enable", EnableModuleAsync);
        secured.MapPost("/modules/{id}/disable", DisableModuleAsync);
        secured.MapPost("/modules/{id}/prune", PruneModuleVersionsAsync);
        secured.MapDelete("/modules/{id}", RemoveModuleAsync);

        secured.MapGet("/external-apis", GetExternalApisAsync);
        secured.MapPost("/external-apis", SaveExternalApiAsync);
        secured.MapDelete("/external-apis/{id}", DeleteExternalApiAsync);
        secured.MapGet("/external-apis/bots", GetExternalApiBotsAsync);
        secured.MapGet("/external-apis/bots/{botId:int}/chats", GetExternalApiBotChatsAsync);

        secured.MapGet("/module-nav", GetModuleNavAsync);
    }

    internal static RouteHandlerBuilder ConfigureAccountImportZipLimits(
        RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 业务允许 200MB 文件；请求体额外预留 multipart 边界和表单字段空间。
        return builder
            .WithMetadata(new RequestSizeLimitAttribute(AccountImportZipMaxRequestSize))
            .WithFormOptions(
                multipartBodyLengthLimit: AccountImportZipMaxRequestSize);
    }

    internal static FormOptions PrepareAccountImportZipRequest(
        HttpRequest httpRequest)
    {
        ArgumentNullException.ThrowIfNull(httpRequest);

        var requestSizeFeature = httpRequest.HttpContext.Features
            .Get<IHttpMaxRequestBodySizeFeature>();
        if (requestSizeFeature is { IsReadOnly: false })
        {
            requestSizeFeature.MaxRequestBodySize =
                AccountImportZipMaxRequestSize;
        }

        return new FormOptions
        {
            MultipartBodyLengthLimit = AccountImportZipMaxRequestSize
        };
    }

    private static async Task<IResult> LoginAsync(
        LoginRequestDto request,
        HttpContext http,
        AdminCredentialStore credentialStore)
    {
        if (!credentialStore.Enabled)
            return Results.Ok(new OperationResultDto(true, "后台验证未启用"));

        var username = (request.Username ?? string.Empty).Trim();
        var password = (request.Password ?? string.Empty).Trim();
        var ok = await credentialStore.ValidateAsync(username, password, http.RequestAborted);
        if (!ok)
            return Results.Unauthorized();

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

        return Results.Ok(new AuthMeDto(true, username, credentialStore.MustChangePassword, credentialStore.Enabled, VersionService.Version));
    }

    private static async Task<IResult> GetSummaryAsync(
        AccountManagementService accounts,
        ChannelManagementService channels,
        GroupManagementService groups,
        BatchTaskManagementService tasks,
        ScheduledTaskService scheduledTasks,
        DataDictionaryService dictionaries,
        CancellationToken cancellationToken)
    {
        var accountCounts = await accounts.CountDashboardAsync(cancellationToken);
        var channelCount = await channels.GetTotalChannelCountAsync();
        var groupCount = await groups.GetTotalGroupCountAsync();
        var activeTaskItems = (await tasks.GetActiveTasksAsync(cancellationToken)).ToList();
        var recentTasks = (await tasks.GetRecentTasksAsync(10)).ToList();
        var activeTaskCount = activeTaskItems.Count;
        var enabledScheduledTaskCount = await scheduledTasks.CountEnabledAsync(cancellationToken);
        var dictionaryCount = await dictionaries.CountAsync(cancellationToken);

        return Results.Ok(new DashboardSummaryDto(
            accountCounts.Total,
            channelCount,
            groupCount,
            activeTaskCount,
            enabledScheduledTaskCount,
            dictionaryCount,
            accountCounts.Normal,
            accountCounts.Limited,
            accountCounts.Invalid,
            activeTaskItems.Take(20).Select(ToTaskListDto).ToList(),
            recentTasks.Select(ToTaskListDto).ToList()));
    }

    private static async Task<IResult> CreateAccountCategoryAsync(
        SaveAccountCategoryRequestDto request,
        AccountCategoryManagementService categoryManagement)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能为空"));

        var existing = await categoryManagement.GetCategoryByNameAsync(name);
        if (existing != null)
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在"));

        var category = await categoryManagement.CreateCategoryAsync(new AccountCategory
        {
            Name = name,
            Color = NormalizeNullable(request.Color) ?? "#1976d2",
            Description = NormalizeNullable(request.Description),
            ExcludeFromOperations = request.ExcludeFromOperations,
            CreatedAt = DateTime.UtcNow
        });

        return Results.Ok(ToDto(category));
    }

    private static async Task<IResult> UpdateAccountCategoryAsync(
        int id,
        SaveAccountCategoryRequestDto request,
        AccountCategoryManagementService categoryManagement)
    {
        var category = await categoryManagement.GetCategoryAsync(id);
        if (category == null)
            return Results.NotFound(new OperationResultDto(false, "分类不存在"));

        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能为空"));

        var existing = await categoryManagement.GetCategoryByNameAsync(name);
        if (existing != null && existing.Id != id)
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在"));

        category.Name = name;
        category.Color = NormalizeNullable(request.Color);
        category.Description = NormalizeNullable(request.Description);
        category.ExcludeFromOperations = request.ExcludeFromOperations;
        await categoryManagement.UpdateCategoryAsync(category);

        return Results.Ok(ToDto(category));
    }

    private static async Task<IResult> DeleteAccountCategoryAsync(
        int id,
        AccountCategoryManagementService categoryManagement,
        AccountManagementService accountManagement,
        CancellationToken cancellationToken)
    {
        var category = await categoryManagement.GetCategoryAsync(id);
        if (category == null)
            return Results.NotFound(new OperationResultDto(false, "分类不存在"));

        var accounts = await accountManagement.QueryAccountsAsync(id, null, false, cancellationToken);
        foreach (var account in accounts)
            await accountManagement.UpdateAccountCategoryAsync(account.Id, null);

        await categoryManagement.DeleteCategoryAsync(id);
        return Results.Ok(new OperationResultDto(true, "分类已删除"));
    }

    private static async Task<IResult> SaveAccountCategoryAssignmentsAsync(
        int id,
        SaveCategoryAssignmentsRequestDto request,
        AccountCategoryManagementService categoryManagement,
        AccountManagementService accountManagement,
        CancellationToken cancellationToken)
    {
        var category = await categoryManagement.GetCategoryAsync(id);
        if (category == null)
            return Results.NotFound(new OperationResultDto(false, "分类不存在"));

        var selectedIds = NormalizeIds(request.AccountIds).ToHashSet();
        var currentlyInCategory = await accountManagement.QueryAccountsAsync(id, null, false, cancellationToken);
        foreach (var account in currentlyInCategory.Where(x => !selectedIds.Contains(x.Id)))
            await accountManagement.UpdateAccountCategoryAsync(account.Id, null);

        foreach (var accountId in selectedIds)
            await accountManagement.UpdateAccountCategoryAsync(accountId, id);

        return Results.Ok(new OperationResultDto(true, "分类绑定已保存"));
    }

    private static async Task<IResult> GetAccountsAsync(
        int? categoryId,
        string? search,
        bool? onlyWaste,
        int? page,
        int? pageSize,
        AccountManagementService accountManagement,
        CancellationToken cancellationToken)
    {
        var pageIndex = Math.Max(0, (page ?? 1) - 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 200);
        var category = categoryId is > 0 ? categoryId : null;
        var (items, total) = await accountManagement.QueryAccountsPagedAsync(
            category,
            search,
            pageIndex,
            size,
            onlyWaste == true,
            cancellationToken);

        return Results.Ok(new PagedResultDto<AccountListItemDto>(
            items.Select(ToDto).ToList(),
            total,
            pageIndex + 1,
            size));
    }

    private static async Task<IResult> GetAccountAsync(
        int id,
        AccountManagementService accountManagement)
    {
        var account = await accountManagement.GetAccountAsync(id);
        return account == null ? Results.NotFound(new OperationResultDto(false, "账号不存在")) : Results.Ok(ToDetailDto(account));
    }

    private static async Task<IResult> UpdateAccountAsync(
        int id,
        UpdateAccountRequestDto request,
        AccountManagementService accountManagement,
        IConfiguration configuration)
    {
        var account = await accountManagement.GetAccountAsync(id);
        if (account == null)
            return Results.NotFound(new OperationResultDto(false, "账号不存在"));

        account.Remark = NormalizeRemark(request.Remark);
        account.TwoFactorPassword = NormalizeNullable(request.TwoFactorPassword);
        if (request.CategoryId.HasValue)
            account.CategoryId = request.CategoryId.Value <= 0 ? null : request.CategoryId.Value;
        if (request.DeviceProfileKey != null)
        {
            var profileKey = NormalizeNullable(request.DeviceProfileKey);
            if (!string.IsNullOrWhiteSpace(profileKey)
                && TelegramDeviceProfileCatalog.Find(configuration, profileKey) == null)
                return Results.BadRequest(new OperationResultDto(false, "设备指纹不存在或已停用"));
            account.DeviceProfileKey = profileKey;
        }
        await accountManagement.UpdateAccountAsync(account);
        return Results.Ok(ToDetailDto(account));
    }

    private static async Task<IResult> SetAccountActiveAsync(
        int id,
        SetActiveRequestDto request,
        AccountManagementService accountManagement)
    {
        await accountManagement.SetAccountActiveStatusAsync(id, request.IsActive);
        return Results.Ok(new OperationResultDto(true, request.IsActive ? "账号已启用" : "账号已停用"));
    }

    private static async Task<IResult> DeleteAccountAsync(
        int id,
        AccountManagementService accountManagement)
    {
        await accountManagement.DeleteAccountAsync(id);
        return Results.Ok(new OperationResultDto(true, "账号已删除"));
    }

    private static async Task<IResult> RefreshTelegramStatusAsync(
        int id,
        RefreshTelegramStatusRequestDto? request,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var status = await accountTools.RefreshAccountStatusAsync(
            id,
            probeCreateChannel: request?.ProbeCreateChannel == true,
            cancellationToken: cancellationToken);
        return Results.Ok(ToDto(status));
    }

    private static async Task<IResult> BatchRefreshTelegramStatusAsync(
        BatchAccountIdsRequestDto request,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            try
            {
                var status = await accountTools.RefreshAccountStatusAsync(
                    id,
                    probeCreateChannel: request.ProbeCreateChannel == true,
                    cancellationToken: cancellationToken);
                results.Add(new AccountOperationItemDto(id, null, status.Ok, status.Summary, status.Details));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(id, null, false, "刷新失败", ex.Message));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> BatchSetAccountCategoryAsync(
        BatchSetAccountCategoryRequestDto request,
        AccountManagementService accountManagement)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var categoryId = request.CategoryId is > 0 ? request.CategoryId : null;
        foreach (var id in ids)
            await accountManagement.UpdateAccountCategoryAsync(id, categoryId);

        return Results.Ok(new OperationResultDto(true, $"分类已更新：{ids.Count} 个账号"));
    }

    private static async Task<IResult> BatchDeleteAccountsAsync(
        BatchAccountIdsRequestDto request,
        AccountManagementService accountManagement)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            try
            {
                var account = await accountManagement.GetAccountAsync(id);
                var phone = account?.DisplayPhone;
                await accountManagement.DeleteAccountAsync(id);
                results.Add(new AccountOperationItemDto(id, phone, true, "已删除", null));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(id, null, false, "删除失败", ex.Message));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> BatchKickAllOtherDevicesAsync(
        BatchAccountIdsRequestDto request,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            var account = await accountManagement.GetAccountAsync(id);
            try
            {
                var ok = await accountTools.KickAllOtherAuthorizationsAsync(id);
                results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, ok, ok ? "已踢出" : "返回 false", ok ? null : "Telegram 返回 false"));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, false, "踢出失败", ex.Message));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> CleanupWasteAccountsAsync(
        CleanupWasteAccountsRequestDto request,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var scope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
        var targets = new List<Account>();
        if (scope == "selected")
        {
            var ids = NormalizeIds(request.AccountIds);
            foreach (var id in ids)
            {
                var account = await accountManagement.GetAccountAsync(id);
                if (account != null)
                    targets.Add(account);
            }
        }
        else if (scope == "filtered")
        {
            targets = (await accountManagement.QueryAccountsAsync(
                    request.CategoryId is > 0 ? request.CategoryId : null,
                    request.Search,
                    onlyWaste: true,
                    cancellationToken))
                .ToList();
        }
        else if (scope == "all")
        {
            targets = (await accountManagement.QueryAccountsAsync(null, null, false, cancellationToken)).ToList();
        }
        else
        {
            return Results.BadRequest(new OperationResultDto(false, "清理范围无效"));
        }

        var deleted = 0;
        var skipped = 0;
        var results = new List<AccountOperationItemDto>();
        foreach (var account in targets.GroupBy(x => x.Id).Select(x => x.First()))
        {
            try
            {
                var status = await accountTools.RefreshAccountStatusAsync(
                    account.Id,
                    probeCreateChannel: request.ProbeCreateChannel == true,
                    cancellationToken: cancellationToken);
                if (!TelegramAccountWasteJudge.TryGetWasteReason(status, out var reason))
                {
                    skipped++;
                    results.Add(new AccountOperationItemDto(account.Id, account.DisplayPhone, true, "跳过", "未判定为废号"));
                    continue;
                }

                await accountManagement.PurgeAccountAsync(account.Id, cancellationToken);
                deleted++;
                results.Add(new AccountOperationItemDto(account.Id, account.DisplayPhone, true, "已清理", reason));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(account.Id, account.DisplayPhone, false, "清理失败", ex.Message));
            }
        }

        var failed = results.Count(x => !x.Success);
        return Results.Ok(new CleanupWasteResultDto(deleted, skipped, failed, results));
    }

    private static async Task<IResult> GetSystemMessagesAsync(
        int id,
        int? limit,
        AccountTelegramToolsService accountTools)
    {
        var messages = await accountTools.GetLatestSystemMessagesAsync(id, Math.Clamp(limit ?? 30, 1, 100));
        return Results.Ok(messages.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetDevicesAsync(
        int id,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        try
        {
            var devices = await accountTools.GetAuthorizationsAsync(id, cancellationToken);
            return CreateDeviceQuerySuccess(devices);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateDeviceQueryFailure(ex);
        }
    }

    internal static IResult CreateDeviceQuerySuccess(
        IReadOnlyList<TelegramAuthorizationInfo> devices) =>
        Results.Ok(devices.Select(ToDto).ToList());

    internal static IResult CreateDeviceQueryFailure(Exception exception)
    {
        var (summary, _) = AccountTelegramToolsService.MapTelegramException(exception);
        return Results.Json(
            new OperationResultDto(
                false,
                $"在线设备读取失败：{summary}。请检查账号登录状态和代理可用性后重试；瞬时连接故障会自动刷新一次连接。",
                "TELEGRAM_DEVICE_QUERY_FAILED"),
            statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> KickDeviceAsync(
        int id,
        long hash,
        AccountTelegramToolsService accountTools)
    {
        var ok = await accountTools.KickAuthorizationAsync(id, hash);
        return Results.Ok(new OperationResultDto(ok, ok ? "已踢出该设备" : "踢出失败"));
    }

    private static async Task<IResult> KickAllOtherDevicesAsync(
        int id,
        AccountTelegramToolsService accountTools)
    {
        var ok = await accountTools.KickAllOtherAuthorizationsAsync(id);
        return Results.Ok(new OperationResultDto(ok, ok ? "已踢出所有其他设备" : "踢出失败"));
    }

    private static async Task<IResult> GetAccountChannelsAsync(
        int id,
        ChannelManagementService channelManagement)
    {
        var memberships = (await channelManagement.GetAccountChannelMembershipsAsync(id))
            .Where(x => x.Channel != null)
            .OrderByDescending(x => x.IsCreator)
            .ThenByDescending(x => x.IsAdmin)
            .ThenByDescending(x => x.SyncedAt)
            .Select(ToDto)
            .ToList();
        return Results.Ok(memberships);
    }

    private static async Task<IResult> GetAccountGroupsAsync(
        int id,
        GroupManagementService groupManagement)
    {
        var memberships = (await groupManagement.GetAccountGroupMembershipsAsync(id))
            .Where(x => x.Group != null)
            .OrderByDescending(x => x.IsCreator)
            .ThenByDescending(x => x.IsAdmin)
            .ThenByDescending(x => x.SyncedAt)
            .Select(ToDto)
            .ToList();
        return Results.Ok(memberships);
    }

    private static async Task<IResult> ChangeChatMembershipAsync(
        ChatMembershipRequestDto request,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var links = (request.Links ?? Array.Empty<string>())
            .SelectMany(x => (x ?? string.Empty).Split(new[] { "\r\n", "\n", "\r", ",", " " }, StringSplitOptions.RemoveEmptyEntries))
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (links.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请填写链接或用户名"));

        var join = string.Equals(request.Operation, "join", StringComparison.OrdinalIgnoreCase);
        var leave = string.Equals(request.Operation, "leave", StringComparison.OrdinalIgnoreCase);
        if (!join && !leave)
            return Results.BadRequest(new OperationResultDto(false, "操作类型无效"));

        var results = new List<AccountOperationItemDto>();
        var delayMs = Math.Clamp(request.DelayMs ?? 0, 0, 60000);
        foreach (var id in ids)
        {
            var account = await accountManagement.GetAccountAsync(id);
            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var target = ParseChatMembershipTarget(link, request.TreatNoBotSuffixAsBot == true);
                    if (target.IsBot)
                    {
                        var (success, error, botUsername) = join
                            ? await accountTools.StartExternalBotAsync(
                                id,
                                target.Input,
                                startParameter: null,
                                cancellationToken: cancellationToken,
                                assumeBotUsername: target.AssumeBotUsername)
                            : await accountTools.StopExternalBotAsync(
                                id,
                                target.Input,
                                cancellationToken: cancellationToken,
                                assumeBotUsername: target.AssumeBotUsername);

                        results.Add(new AccountOperationItemDto(
                            id,
                            account?.DisplayPhone,
                            success,
                            success ? (join ? "Bot 已启用" : "Bot 已停用") : "Bot 操作失败",
                            success ? botUsername : error));
                    }
                    else
                    {
                        var (success, error, title) = join
                            ? await accountTools.JoinChatOrChannelAsync(id, target.Input, cancellationToken)
                            : await accountTools.LeaveChatOrChannelAsync(id, target.Input, cancellationToken);
                        results.Add(new AccountOperationItemDto(
                            id,
                            account?.DisplayPhone,
                            success,
                            success ? (join ? "已加入/订阅" : "已退出/退订") : "操作失败",
                            success ? title : error));
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, false, "操作失败", ex.Message));
                }

                if (delayMs > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs + Random.Shared.Next(0, 350)), cancellationToken);
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> CheckChatMembershipRiskAsync(
        ChatMembershipRiskRequestDto request,
        AccountManagementService accountManagement,
        AccountRiskService riskService,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var accounts = new List<Account>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = await accountManagement.GetAccountAsync(id);
            if (account != null)
                accounts.Add(account);
        }

        var check = riskService.CheckBatchAccounts(accounts);
        var risky = check.RiskyAccounts.Select(ToRiskAccountDto).ToList();
        var safeIds = check.SafeAccounts.Select(x => x.Id).ToList();

        return Results.Ok(new ChatMembershipRiskResultDto(
            check.HasRiskyAccounts,
            check.TotalCount,
            check.RiskyCount,
            check.SafeCount,
            check.RiskyCount > 0 ? $"检测到 {check.RiskyCount} 个风险账号" : "所有账号均已满足登录时长要求",
            check.GetRiskySummary(),
            risky,
            safeIds));
    }

    private static async Task<IResult> UpdateProfileAsync(
        int id,
        HttpRequest httpRequest,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var editNickname = ParseBool(form["editNickname"]);
        var editBio = ParseBool(form["editBio"]);
        var editUsername = ParseBool(form["editUsername"]);
        var editAvatar = ParseBool(form["editAvatar"]);

        if (!editNickname && !editBio && !editUsername && !editAvatar)
            return Results.BadRequest(new OperationResultDto(false, "请选择要修改的资料项"));

        var account = await accountManagement.GetAccountAsync(id);
        if (account == null)
            return Results.NotFound(new OperationResultDto(false, "账号不存在"));

        if (editNickname || editBio)
        {
            var nickname = editNickname ? form["nickname"].ToString() : null;
            var bio = editBio ? form["bio"].ToString() : null;
            if (editNickname && string.IsNullOrWhiteSpace(nickname))
                return Results.BadRequest(new OperationResultDto(false, "请填写昵称"));

            var (ok, err) = await accountTools.UpdateUserProfileAsync(id, nickname, bio, cancellationToken);
            if (!ok)
                return Results.BadRequest(new OperationResultDto(false, err ?? "资料保存失败"));

            if (editNickname)
                account.Nickname = nickname?.Trim();
        }

        if (editUsername)
        {
            var username = form["username"].ToString();
            var (ok, err, normalized) = await accountTools.UpdateUsernameAsync(id, username, cancellationToken);
            if (!ok)
                return Results.BadRequest(new OperationResultDto(false, err ?? "用户名保存失败"));
            account.Username = normalized;
        }

        if (editAvatar)
        {
            var file = form.Files.GetFile("avatar");
            if (file == null)
                return Results.BadRequest(new OperationResultDto(false, "请先选择头像图片"));

            await using var stream = file.OpenReadStream();
            var (ok, err) = await accountTools.UpdateProfilePhotoAsync(id, stream, file.FileName, cancellationToken);
            if (!ok)
                return Results.BadRequest(new OperationResultDto(false, err ?? "头像上传失败"));
        }

        await accountManagement.UpdateAccountAsync(account);
        return Results.Ok(new OperationResultDto(true, "保存成功"));
    }

    private static async Task<IResult> BatchUpdateProfileAsync(
        BatchUpdateProfileRequestDto request,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        TemplateRenderingService templateRendering,
        DataDictionaryService dictionaryService,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var mode = (request.Mode ?? string.Empty).Trim().ToLowerInvariant();
        var results = new List<AccountOperationItemDto>();
        var textDictionaryNames = (await dictionaryService.GetAllAsync(cancellationToken))
            .Where(x => x.IsEnabled && string.Equals(x.Type, DataDictionaryTypes.Text, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nicknameTemplates = mode == "nickname"
            ? ParseTemplateLines(request.NicknameTemplates?.Count > 0 ? string.Join('\n', request.NicknameTemplates) : request.Nickname)
            : new List<string>();
        if (mode == "nickname")
        {
            if (nicknameTemplates.Count == 0)
                return Results.BadRequest(new OperationResultDto(false, "请至少填写一行昵称模板"));

            if (!ValidateTextTemplates(nicknameTemplates, textDictionaryNames, out var nicknameTemplateError))
                return Results.BadRequest(new OperationResultDto(false, nicknameTemplateError ?? "昵称模板不合法"));
        }

        var usernameTemplate = (request.UsernameTemplate ?? string.Empty).Trim();
        if (mode == "username")
        {
            if (string.IsNullOrWhiteSpace(usernameTemplate))
                return Results.BadRequest(new OperationResultDto(false, "用户名模板不能为空"));

            if (ids.Count > 1 && !Regex.IsMatch(usernameTemplate, @"\{(?<name>[a-zA-Z0-9_]+)\}"))
                return Results.BadRequest(new OperationResultDto(false, "批量修改多个账号时，用户名模板至少需要包含一个变量"));

            if (!ValidateTextTemplates(new[] { usernameTemplate }, textDictionaryNames, out var usernameTemplateError, allowBuiltInIndexedTokens: true))
                return Results.BadRequest(new OperationResultDto(false, usernameTemplateError ?? "用户名模板不合法"));
        }

        var bioTemplate = request.Bio ?? string.Empty;
        if (mode == "bio"
            && !ValidateTextTemplates(new[] { bioTemplate }, textDictionaryNames, out var bioTemplateError))
            return Results.BadRequest(new OperationResultDto(false, bioTemplateError ?? "Bio 模板不合法"));

        var index = 0;
        var usedNicknames = new Dictionary<string, int>(StringComparer.Ordinal);
        var generatedUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var account = await accountManagement.GetAccountAsync(id);
            try
            {
                switch (mode)
                {
                    case "nickname":
                        {
                            var template = nicknameTemplates[index % nicknameTemplates.Count];
                            var resolvedNickname = (await templateRendering.RenderTextTemplateAsync(template, cancellationToken)).Trim();
                            if (string.IsNullOrWhiteSpace(resolvedNickname))
                                throw new InvalidOperationException("生成的昵称为空，请检查模板或字典内容");

                            usedNicknames.TryGetValue(resolvedNickname, out var usedCount);
                            usedNicknames[resolvedNickname] = usedCount + 1;
                            var nickname = BuildNickname(
                                resolvedNickname,
                                account?.Phone ?? string.Empty,
                                request.AppendPhoneLast4WhenDuplicate == true && usedCount > 0);

                            var (ok, err) = await accountTools.UpdateUserProfileAsync(id, nickname, null, cancellationToken);
                            if (ok && account != null)
                            {
                                account.Nickname = nickname;
                                await accountManagement.UpdateAccountAsync(account);
                            }
                            results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, ok, ok ? $"昵称已修改：{nickname}" : "修改失败", err));
                            break;
                        }
                    case "bio":
                        {
                            var renderedBio = await templateRendering.RenderTextTemplateAsync(bioTemplate, cancellationToken);
                            var (ok, err) = await accountTools.UpdateUserProfileAsync(id, null, renderedBio, cancellationToken);
                            results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, ok, ok ? "Bio 已修改" : "修改失败", err));
                            break;
                        }
                    case "username":
                        {
                            var indexedTemplate = BuildIndexedValue(usernameTemplate, account, id, index);
                            var renderedTemplate = await templateRendering.RenderTextTemplateAsync(indexedTemplate, cancellationToken);
                            var username = BuildIndexedValue(renderedTemplate, account, id, index);
                            if (!TryPrepareUsername(username, generatedUsernames, out var preparedUsername, out var prepareError))
                                throw new InvalidOperationException(prepareError ?? "生成用户名失败");

                            var (ok, err, normalized) = await accountTools.UpdateUsernameAsync(id, preparedUsername, cancellationToken);
                            if (ok && account != null)
                            {
                                account.Username = normalized;
                                await accountManagement.UpdateAccountAsync(account);
                            }
                            results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, ok, ok ? $"用户名已修改：{preparedUsername}" : "修改失败", err));
                            break;
                        }
                    default:
                        throw new InvalidOperationException("批量资料模式无效");
                }
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, false, "修改失败", ex.Message));
            }
            finally
            {
                index++;
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> BatchUpdateAvatarAsync(
        HttpRequest httpRequest,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        ImageAssetStorageService assetStorage,
        TemplateRenderingService templateRendering,
        CancellationToken cancellationToken)
    {
        if (!httpRequest.HasFormContentType)
            return Results.BadRequest(new OperationResultDto(false, "请使用 multipart/form-data 上传头像配置"));

        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var ids = ParseIntList(form["accountIds"]);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var source = (form["source"].ToString() ?? string.Empty).Trim().ToLowerInvariant();
        if (source is not ("fixed" or "dictionary"))
            return Results.BadRequest(new OperationResultDto(false, "头像来源无效"));

        byte[]? fixedBytes = null;
        var fixedFileName = "avatar.jpg";
        var dictionaryName = string.Empty;
        if (source == "fixed")
        {
            var file = form.Files.GetFile("avatar");
            if (file == null)
                return Results.BadRequest(new OperationResultDto(false, "请先选择头像图片"));

            fixedFileName = string.IsNullOrWhiteSpace(file.FileName) ? "avatar.jpg" : file.FileName.Trim();
            await using var fileStream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await fileStream.CopyToAsync(memory, cancellationToken);
            fixedBytes = memory.ToArray();
            if (fixedBytes.Length == 0)
                return Results.BadRequest(new OperationResultDto(false, "头像图片为空"));
        }
        else
        {
            dictionaryName = (form["dictionaryName"].ToString() ?? string.Empty).Trim().Trim('{', '}');
            if (string.IsNullOrWhiteSpace(dictionaryName))
                return Results.BadRequest(new OperationResultDto(false, "请选择图片字典"));
        }

        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            var account = await accountManagement.GetAccountAsync(id);
            try
            {
                bool ok;
                string? err;
                string summary;
                if (source == "fixed")
                {
                    await using var stream = new MemoryStream(fixedBytes!, writable: false);
                    (ok, err) = await accountTools.UpdateProfilePhotoAsync(id, stream, fixedFileName, cancellationToken);
                    summary = ok ? "头像已修改" : "修改失败";
                }
                else
                {
                    var token = $"{{{dictionaryName}}}";
                    var asset = await templateRendering.ResolveImageTemplateAsync(token, cancellationToken);
                    await using var stream = await assetStorage.OpenReadAsync(asset.AssetPath, cancellationToken);
                    (ok, err) = await accountTools.UpdateProfilePhotoAsync(id, stream, asset.FileName, cancellationToken);
                    summary = ok ? $"头像已修改：{asset.FileName}" : "修改失败";
                }

                results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, ok, summary, err));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, false, "修改失败", ex.Message));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> ChangeTwoFactorPasswordAsync(
        ChangeTwoFactorPasswordRequestDto request,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));
        if (string.IsNullOrWhiteSpace(request.NewPassword))
            return Results.BadRequest(new OperationResultDto(false, "新二级密码不能为空"));

        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            var account = await accountManagement.GetAccountAsync(id);
            try
            {
                var useStoredPassword = request.UseStoredPasswords != false;
                var currentPassword = useStoredPassword && !string.IsNullOrWhiteSpace(account?.TwoFactorPassword)
                    ? account.TwoFactorPassword
                    : request.CurrentPassword;
                var (ok, err) = await accountTools.ChangeTwoFactorPasswordAsync(id, currentPassword, request.NewPassword!, request.Hint, cancellationToken);
                if (ok && account != null && request.SaveNewPasswordToDb != false)
                {
                    account.TwoFactorPassword = request.NewPassword!.Trim();
                    await accountManagement.UpdateAccountAsync(account);
                }
                results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, ok, ok ? "二级密码已修改" : "修改失败", err));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, false, "修改失败", ex.Message));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> RequestTwoFactorPasswordResetAsync(
        ResetTwoFactorPasswordRequestDto request,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            var account = await accountManagement.GetAccountAsync(id);
            try
            {
                var (ok, message, waitUntilUtc) = await accountTools.RequestTwoFactorPasswordResetAsync(id, cancellationToken);
                var detail = waitUntilUtc.HasValue
                    ? $"{message}；预计可重置时间：{waitUntilUtc.Value:yyyy-MM-dd HH:mm:ss} UTC"
                    : message;
                results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, ok, ok ? "已申请重置" : "申请失败", detail));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(id, account?.DisplayPhone, false, "申请失败", ex.Message));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> GetTwoFactorRecoveryEmailAsync(
        int id,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var (success, error, hasPassword, hasRecoveryEmail, pattern) = await accountTools.GetTwoFactorRecoveryEmailStatusAsync(id, cancellationToken);
        return Results.Ok(new TwoFactorRecoveryEmailStatusDto(success, error, hasPassword, hasRecoveryEmail, pattern));
    }

    private static async Task<IResult> SetTwoFactorRecoveryEmailAsync(
        int id,
        SetTwoFactorRecoveryEmailRequestDto request,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var account = await accountManagement.GetAccountAsync(id);
        var currentPassword = string.IsNullOrWhiteSpace(request.CurrentPassword) ? account?.TwoFactorPassword : request.CurrentPassword;
        var (success, error, pattern) = await accountTools.SetTwoFactorRecoveryEmailAsync(id, currentPassword, request.Email ?? "", cancellationToken);
        return success
            ? Results.Ok(new EmailOperationResultDto(true, null, pattern))
            : Results.BadRequest(new EmailOperationResultDto(false, error, pattern));
    }

    private static async Task<IResult> ConfirmTwoFactorRecoveryEmailAsync(
        int id,
        ConfirmEmailCodeRequestDto request,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var (success, error) = await accountTools.ConfirmTwoFactorRecoveryEmailAsync(id, request.Code ?? "", cancellationToken);
        return success
            ? Results.Ok(new OperationResultDto(true, "找回邮箱已确认"))
            : Results.BadRequest(new OperationResultDto(false, error));
    }

    private static async Task<IResult> ResendTwoFactorRecoveryEmailAsync(
        int id,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var (success, error, pattern, _) = await accountTools.ResendTwoFactorRecoveryEmailAsync(id, cancellationToken);
        return success
            ? Results.Ok(new EmailOperationResultDto(true, null, pattern))
            : Results.BadRequest(new EmailOperationResultDto(false, error, pattern));
    }

    private static async Task<IResult> CancelTwoFactorRecoveryEmailAsync(
        int id,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var (success, error) = await accountTools.CancelTwoFactorRecoveryEmailAsync(id, cancellationToken);
        return success
            ? Results.Ok(new OperationResultDto(true, "已取消待确认找回邮箱"))
            : Results.BadRequest(new OperationResultDto(false, error));
    }

    private static async Task<IResult> GetLoginEmailAsync(
        int id,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var (success, error, hasLoginEmail, pattern) = await accountTools.GetLoginEmailStatusAsync(id, cancellationToken);
        return Results.Ok(new LoginEmailStatusDto(success, error, hasLoginEmail, pattern));
    }

    private static async Task<IResult> SetLoginEmailAsync(
        int id,
        SetLoginEmailRequestDto request,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var (success, error, pattern) = await accountTools.SetLoginEmailAsync(id, request.Email ?? "", cancellationToken);
        return success
            ? Results.Ok(new EmailOperationResultDto(true, null, pattern))
            : Results.BadRequest(new EmailOperationResultDto(false, error, pattern));
    }

    private static async Task<IResult> ConfirmLoginEmailAsync(
        int id,
        ConfirmEmailCodeRequestDto request,
        AccountTelegramToolsService accountTools,
        CancellationToken cancellationToken)
    {
        var (success, error) = await accountTools.ConfirmLoginEmailAsync(id, request.Code ?? "", cancellationToken);
        return success
            ? Results.Ok(new OperationResultDto(true, "登录邮箱已确认"))
            : Results.BadRequest(new OperationResultDto(false, error));
    }

    private static async Task<IResult> BatchChangeTwoFactorRecoveryEmailAsync(
        BatchChangeRecoveryEmailRequestDto request,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        IConfiguration configuration,
        CloudMailClient cloudMail,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.AccountIds);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择账号"));

        var cloudMailBaseUrl = FirstNonEmpty(request.CloudMailBaseUrl, configuration["CloudMail:BaseUrl"]);
        var cloudMailToken = FirstNonEmpty(request.CloudMailToken, configuration["CloudMail:Token"]);
        var domain = FirstNonEmpty(request.Domain, configuration["CloudMail:Domain"]).Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(cloudMailBaseUrl) || string.IsNullOrWhiteSpace(cloudMailToken))
            return Results.BadRequest(new OperationResultDto(false, "请先配置 Cloud Mail URL/Token"));
        if (string.IsNullOrWhiteSpace(domain))
            return Results.BadRequest(new OperationResultDto(false, "请填写邮箱域名"));

        var pollIntervalSeconds = Math.Clamp(request.PollIntervalSeconds ?? 3, 2, 30);
        var pollTimeoutSeconds = Math.Clamp(request.PollTimeoutSeconds ?? 60, 10, 300);
        var autoConfirm = request.AutoConfirm != false;
        var changeLoginEmail = request.ChangeLoginEmail != false;
        var trySetLoginEmailWhenMissing = request.TrySetLoginEmailWhenMissing == true;
        var useStoredPasswords = request.UseStoredPasswords != false;
        var currentPassword = (request.CurrentPassword ?? string.Empty).Trim();
        var sendEmailFilter = (request.SendEmailFilter ?? string.Empty).Trim();
        var subjectFilter = (request.SubjectFilter ?? string.Empty).Trim();

        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var account = await accountManagement.GetAccountAsync(id);
            if (account == null)
            {
                results.Add(new AccountOperationItemDto(id, null, false, "账号不存在", "账号不存在或已被删除"));
                continue;
            }

            var phoneDigits = PhoneNumberFormatter.NormalizeToDigits(account.Phone);
            var email = string.IsNullOrWhiteSpace(phoneDigits) ? string.Empty : $"{phoneDigits}@{domain}";
            if (string.IsNullOrWhiteSpace(email))
            {
                results.Add(new AccountOperationItemDto(id, account.DisplayPhone, false, "邮箱生成失败", "手机号无效，无法生成邮箱"));
                continue;
            }

            try
            {
                var (kind, message) = await ProcessRecoveryEmailAccountAsync(
                    account,
                    email,
                    accountTools,
                    cloudMail,
                    cloudMailBaseUrl,
                    cloudMailToken,
                    useStoredPasswords,
                    currentPassword,
                    autoConfirm,
                    changeLoginEmail,
                    trySetLoginEmailWhenMissing,
                    pollIntervalSeconds,
                    pollTimeoutSeconds,
                    sendEmailFilter,
                    subjectFilter,
                    cancellationToken);

                var success = kind != BatchEmailResultKind.Failed;
                results.Add(new AccountOperationItemDto(
                    account.Id,
                    account.DisplayPhone,
                    success,
                    kind == BatchEmailResultKind.Skipped ? "已跳过" : "已处理",
                    $"{email}{(string.IsNullOrWhiteSpace(message) ? string.Empty : "；" + message)}"));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(account.Id, account.DisplayPhone, false, "处理失败", $"{email}；{ex.Message}"));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<(BatchEmailResultKind Kind, string? Message)> ProcessRecoveryEmailAccountAsync(
        Account account,
        string email,
        AccountTelegramToolsService accountTools,
        CloudMailClient cloudMail,
        string cloudMailBaseUrl,
        string cloudMailToken,
        bool useStoredPasswords,
        string currentPassword,
        bool autoConfirm,
        bool changeLoginEmail,
        bool trySetLoginEmailWhenMissing,
        int pollIntervalSeconds,
        int pollTimeoutSeconds,
        string sendEmailFilter,
        string subjectFilter,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var okRecovery = true;
        var okLogin = true;
        var loginKind = BatchEmailResultKind.Success;

        var effectivePassword = useStoredPasswords
            ? (!string.IsNullOrWhiteSpace(account.TwoFactorPassword) ? account.TwoFactorPassword : currentPassword)
            : currentPassword;
        effectivePassword = (effectivePassword ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(effectivePassword))
        {
            okRecovery = false;
            messages.Add("找回邮箱失败：未提供原二级密码");
        }
        else
        {
            var startUtc = DateTimeOffset.UtcNow;
            var (sent, err, pattern) = await accountTools.SetTwoFactorRecoveryEmailAsync(
                account.Id,
                effectivePassword,
                email,
                cancellationToken);

            if (!sent)
            {
                okRecovery = false;
                if (autoConfirm && (err ?? string.Empty).Contains("EMAIL_UNCONFIRMED", StringComparison.OrdinalIgnoreCase))
                {
                    var pendingCode = await WaitTelegramMailCodeAsync(
                        cloudMail,
                        cloudMailBaseUrl,
                        cloudMailToken,
                        email,
                        startUtc,
                        pollIntervalSeconds,
                        pollTimeoutSeconds,
                        sendEmailFilter,
                        subjectFilter,
                        TelegramMailCodePurpose.RecoveryEmail,
                        allowOlder: true,
                        cancellationToken);

                    if (string.IsNullOrWhiteSpace(pendingCode))
                    {
                        var diag = await BuildCloudMailReceiveDiagAsync(cloudMail, cloudMailBaseUrl, cloudMailToken, email, cancellationToken);
                        messages.Add($"找回邮箱失败：邮箱待确认但收码超时（{pollTimeoutSeconds}s）：{err}{(string.IsNullOrWhiteSpace(diag) ? string.Empty : "；" + diag)}");
                    }
                    else
                    {
                        var (pendingConfirmed, pendingConfirmErr) = await accountTools.ConfirmTwoFactorRecoveryEmailAsync(
                            account.Id,
                            pendingCode,
                            cancellationToken);
                        if (pendingConfirmed)
                        {
                            okRecovery = true;
                            messages.Add("找回邮箱已确认绑定（之前存在待确认邮箱）");
                        }
                        else
                        {
                            if ((pendingConfirmErr ?? string.Empty).Contains("CODE_INVALID", StringComparison.OrdinalIgnoreCase))
                            {
                                _ = await accountTools.ResendTwoFactorRecoveryEmailAsync(account.Id, cancellationToken);
                                var retryCode = await WaitTelegramMailCodeAsync(
                                    cloudMail,
                                    cloudMailBaseUrl,
                                    cloudMailToken,
                                    email,
                                    DateTimeOffset.UtcNow,
                                    pollIntervalSeconds,
                                    pollTimeoutSeconds,
                                    sendEmailFilter,
                                    subjectFilter,
                                    TelegramMailCodePurpose.RecoveryEmail,
                                    allowOlder: true,
                                    cancellationToken);

                                if (!string.IsNullOrWhiteSpace(retryCode))
                                {
                                    var (retryOk, retryErr) = await accountTools.ConfirmTwoFactorRecoveryEmailAsync(
                                        account.Id,
                                        retryCode,
                                        cancellationToken);
                                    if (retryOk)
                                    {
                                        okRecovery = true;
                                        messages.Add("找回邮箱已确认绑定（之前存在待确认邮箱，重发后重试成功）");
                                    }
                                    else
                                    {
                                        messages.Add($"找回邮箱确认失败：{retryErr}");
                                    }
                                }
                            }

                            if (!okRecovery)
                                messages.Add($"找回邮箱确认失败：{pendingConfirmErr}");
                        }
                    }
                }
                else
                {
                    messages.Add($"找回邮箱失败：{err}");
                }
            }
            else if (string.IsNullOrWhiteSpace(pattern))
            {
                messages.Add("找回邮箱无需确认（可能已是目标邮箱）");
            }
            else if (!autoConfirm)
            {
                messages.Add("找回邮箱已发送验证码（未自动确认）");
            }
            else
            {
                var code = await WaitTelegramMailCodeAsync(
                    cloudMail,
                    cloudMailBaseUrl,
                    cloudMailToken,
                    email,
                    startUtc,
                    pollIntervalSeconds,
                    pollTimeoutSeconds,
                    sendEmailFilter,
                    subjectFilter,
                    TelegramMailCodePurpose.RecoveryEmail,
                    allowOlder: false,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(code))
                {
                    okRecovery = false;
                    var diag = await BuildCloudMailReceiveDiagAsync(cloudMail, cloudMailBaseUrl, cloudMailToken, email, cancellationToken);
                    messages.Add($"找回邮箱收码超时（{pollTimeoutSeconds}s）{(string.IsNullOrWhiteSpace(diag) ? string.Empty : "：" + diag)}");
                }
                else
                {
                    var (confirmed, confirmErr) = await accountTools.ConfirmTwoFactorRecoveryEmailAsync(account.Id, code, cancellationToken);
                    if (confirmed)
                    {
                        messages.Add("找回邮箱已确认绑定");
                    }
                    else
                    {
                        okRecovery = false;
                        if ((confirmErr ?? string.Empty).Contains("CODE_INVALID", StringComparison.OrdinalIgnoreCase))
                        {
                            _ = await accountTools.ResendTwoFactorRecoveryEmailAsync(account.Id, cancellationToken);
                            var retryCode = await WaitTelegramMailCodeAsync(
                                cloudMail,
                                cloudMailBaseUrl,
                                cloudMailToken,
                                email,
                                DateTimeOffset.UtcNow,
                                pollIntervalSeconds,
                                pollTimeoutSeconds,
                                sendEmailFilter,
                                subjectFilter,
                                TelegramMailCodePurpose.RecoveryEmail,
                                allowOlder: true,
                                cancellationToken);

                            if (!string.IsNullOrWhiteSpace(retryCode))
                            {
                                var (retryOk, retryErr) = await accountTools.ConfirmTwoFactorRecoveryEmailAsync(account.Id, retryCode, cancellationToken);
                                if (retryOk)
                                {
                                    okRecovery = true;
                                    messages.Add("找回邮箱已确认绑定（重发后重试成功）");
                                }
                                else
                                {
                                    messages.Add($"找回邮箱确认失败：{retryErr}");
                                }
                            }
                        }

                        if (!okRecovery)
                            messages.Add($"找回邮箱确认失败：{confirmErr}");
                    }
                }
            }
        }

        if (changeLoginEmail)
        {
            var (statusOk, statusErr, hasLoginEmail, _) = await accountTools.GetLoginEmailStatusAsync(account.Id, cancellationToken);
            if (!statusOk)
            {
                okLogin = false;
                loginKind = BatchEmailResultKind.Failed;
                messages.Add($"登录邮箱失败：获取状态失败：{statusErr}");
            }
            else if (!hasLoginEmail && !trySetLoginEmailWhenMissing)
            {
                loginKind = BatchEmailResultKind.Skipped;
                messages.Add("登录邮箱已跳过：该账号未设置登录邮箱");
            }
            else
            {
                var startUtc = DateTimeOffset.UtcNow;
                var (sent, err, pattern) = await accountTools.SetLoginEmailAsync(account.Id, email, cancellationToken);
                if (!sent)
                {
                    var msg = err ?? "未知错误";
                    if (msg.Contains("EMAIL_NOT_SETUP", StringComparison.OrdinalIgnoreCase))
                    {
                        loginKind = BatchEmailResultKind.Skipped;
                        messages.Add($"登录邮箱不支持：{msg}");
                    }
                    else
                    {
                        okLogin = false;
                        loginKind = BatchEmailResultKind.Failed;
                        messages.Add($"登录邮箱失败：{msg}");
                    }
                }
                else if (!autoConfirm)
                {
                    messages.Add($"登录邮箱已发送验证码（未自动确认）{(string.IsNullOrWhiteSpace(pattern) ? string.Empty : $"，掩码：{pattern}")}");
                }
                else
                {
                    var code = await WaitTelegramMailCodeAsync(
                        cloudMail,
                        cloudMailBaseUrl,
                        cloudMailToken,
                        email,
                        startUtc,
                        pollIntervalSeconds,
                        pollTimeoutSeconds,
                        sendEmailFilter,
                        subjectFilter,
                        TelegramMailCodePurpose.LoginEmail,
                        allowOlder: false,
                        cancellationToken);

                    if (string.IsNullOrWhiteSpace(code))
                    {
                        okLogin = false;
                        loginKind = BatchEmailResultKind.Failed;
                        var diag = await BuildCloudMailReceiveDiagAsync(cloudMail, cloudMailBaseUrl, cloudMailToken, email, cancellationToken);
                        messages.Add($"登录邮箱收码超时（{pollTimeoutSeconds}s）{(string.IsNullOrWhiteSpace(diag) ? string.Empty : "：" + diag)}");
                    }
                    else
                    {
                        var (confirmed, confirmErr) = await accountTools.ConfirmLoginEmailAsync(account.Id, code, cancellationToken);
                        if (confirmed)
                        {
                            messages.Add("登录邮箱已确认");
                        }
                        else
                        {
                            okLogin = false;
                            loginKind = BatchEmailResultKind.Failed;
                            if ((confirmErr ?? string.Empty).Contains("CODE_INVALID", StringComparison.OrdinalIgnoreCase)
                                || (confirmErr ?? string.Empty).Contains("EMAIL_TOKEN_INVALID", StringComparison.OrdinalIgnoreCase))
                            {
                                _ = await accountTools.SetLoginEmailAsync(account.Id, email, cancellationToken);
                                var retryCode = await WaitTelegramMailCodeAsync(
                                    cloudMail,
                                    cloudMailBaseUrl,
                                    cloudMailToken,
                                    email,
                                    DateTimeOffset.UtcNow,
                                    pollIntervalSeconds,
                                    pollTimeoutSeconds,
                                    sendEmailFilter,
                                    subjectFilter,
                                    TelegramMailCodePurpose.LoginEmail,
                                    allowOlder: true,
                                    cancellationToken);

                                if (!string.IsNullOrWhiteSpace(retryCode))
                                {
                                    var (retryOk, retryErr) = await accountTools.ConfirmLoginEmailAsync(account.Id, retryCode, cancellationToken);
                                    if (retryOk)
                                    {
                                        okLogin = true;
                                        loginKind = BatchEmailResultKind.Success;
                                        messages.Add("登录邮箱已确认（重发后重试成功）");
                                    }
                                    else
                                    {
                                        messages.Add($"登录邮箱确认失败：{retryErr}");
                                    }
                                }
                            }

                            if (!okLogin)
                                messages.Add($"登录邮箱确认失败：{confirmErr}");
                        }
                    }
                }
            }
        }

        var kind = changeLoginEmail ? loginKind : (okRecovery ? BatchEmailResultKind.Success : BatchEmailResultKind.Failed);
        if (!okRecovery || (changeLoginEmail && !okLogin && loginKind != BatchEmailResultKind.Skipped))
            kind = BatchEmailResultKind.Failed;

        return (kind, messages.Count == 0 ? null : string.Join("；", messages));
    }

    private static async Task<IResult> ImportAccountsZipAsync(
        HttpRequest httpRequest,
        AccountImportService importService,
        AccountManagementService accountManagement,
        CancellationToken cancellationToken)
    {
        if (!httpRequest.HasFormContentType)
            return Results.BadRequest(new OperationResultDto(false, "请使用 multipart/form-data 上传 zip 文件"));

        var formOptions = PrepareAccountImportZipRequest(httpRequest);
        var form = await httpRequest.ReadFormAsync(
            formOptions,
            cancellationToken);
        var file = form.Files.GetFile("file");
        if (file == null)
            return Results.BadRequest(new OperationResultDto(false, "请先选择 Zip 压缩包"));
        if (file.Length > AccountImportZipMaxFileSize)
            return Results.BadRequest(new OperationResultDto(false, "Zip 压缩包不能超过 200MB"));

        var categoryId = ParseNullableInt(form["categoryId"]);
        var twoFactorPassword = NormalizeNullable(form["twoFactorPassword"]);
        var deviceProfileKey = NormalizeNullable(form["deviceProfileKey"]);
        var proxyStrategy = form["proxyStrategy"].ToString().Trim().ToLowerInvariant();
        var usesPerAccountProxyBatch = proxyStrategy == "proxy_per_account";
        var perAccountProxyText = usesPerAccountProxyBatch
            ? form["proxyText"].ToString()
            : null;
        AccountProxyBindingInput? proxyBinding = null;
        if (usesPerAccountProxyBatch)
        {
            if (string.IsNullOrWhiteSpace(perAccountProxyText))
            {
                return Results.BadRequest(new OperationResultDto(
                    false,
                    "请填写逐账号批量代理，每行一个代理地址"));
            }
            if (perAccountProxyText.Length > ProxyManagementService.MaxPerAccountProxyTextLength)
            {
                return Results.BadRequest(new OperationResultDto(
                    false,
                    "批量代理文本不能超过 100000 个字符"));
            }
        }
        else
        {
            proxyBinding = ParseImportProxyBinding(proxyStrategy, form["proxyId"]);
            if (proxyBinding == null)
            {
                return Results.BadRequest(new OperationResultDto(
                    false,
                    "请先明确选择账号首次连接出口：已有代理、逐账号批量代理、自动分配已有 WARP、创建一对一 WARP、已配置的全局代理或明确直连"));
            }
            await importService.ValidateImportProxyBindingAsync(proxyBinding, cancellationToken);
        }

        await using var stream = file.OpenReadStream();
        try
        {
            var results = await importService.ImportFromZipStreamAsync(
                file.FileName,
                stream,
                categoryId,
                twoFactorPassword,
                proxyBinding,
                cancellationToken,
                perAccountProxyText,
                deviceProfileKey);
            return Results.Ok(await BuildImportResponseAsync(results, accountManagement));
        }
        catch (AccountImportProxyBatchException ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }
    }

    private static async Task<IResult> ImportAccountsSessionFilesAsync(
        HttpRequest httpRequest,
        AccountImportService importService,
        AccountManagementService accountManagement,
        CancellationToken cancellationToken)
    {
        if (!httpRequest.HasFormContentType)
            return Results.BadRequest(new OperationResultDto(false, "请使用 multipart/form-data 上传 session 文件"));



        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var files = form.Files.GetFiles("files");
        if (files.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择 Session 文件"));
        if (files.Any(x => x.Length > 10L * 1024 * 1024))
            return Results.BadRequest(new OperationResultDto(false, "单个 Session 文件不能超过 10MB"));

        var categoryId = ParseNullableInt(form["categoryId"]);
        var deviceProfileKey = NormalizeNullable(form["deviceProfileKey"]);
        var proxyBinding = ParseImportProxyBinding(form["proxyStrategy"], form["proxyId"]);
        if (proxyBinding == null)
        {
            return Results.BadRequest(new OperationResultDto(
                false,
                "请先明确选择账号首次连接出口：已有代理、自动分配已有 WARP、创建一对一 WARP、已配置的全局代理或明确直连；逐账号批量代理仅支持 Zip 导入"));
        }
        await importService.ValidateImportProxyBindingAsync(proxyBinding, cancellationToken);
        if (string.Equals(proxyBinding.Strategy, "warp_per_account", StringComparison.OrdinalIgnoreCase)
            && files.Count > AccountImportService.MaxWarpPerAccountImportCount)
        {
            return Results.BadRequest(new OperationResultDto(
                false,
                $"创建一对一 WARP 单次最多导入 {AccountImportService.MaxWarpPerAccountImportCount} 个账号，当前选择了 {files.Count} 个"));
        }
        var importFiles = new List<AccountImportFile>();
        foreach (var file in files)
            importFiles.Add(new AccountImportFile(file.FileName, file.OpenReadStream()));

        try
        {
            var results = await importService.ImportFromSessionFileStreamsBalancedAsync(
                importFiles,
                categoryId,
                proxyBinding,
                cancellationToken,
                deviceProfileKey);
            return Results.Ok(await BuildImportResponseAsync(results, accountManagement));
        }
        finally
        {
            foreach (var importFile in importFiles)
                await importFile.Content.DisposeAsync();
        }
    }

    private static async Task<IResult> ImportAccountsStringSessionAsync(
        ImportStringSessionRequestDto request,
        AccountImportService importService,
        AccountManagementService accountManagement,
        CancellationToken cancellationToken)
    {


        var sessionString = (request.SessionString ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sessionString))
            return Results.BadRequest(new OperationResultDto(false, "请填写 StringSession"));

        var proxyBinding = ParseImportProxyBinding(request.ProxyStrategy, request.ProxyId?.ToString());
        if (proxyBinding == null)
        {
            return Results.BadRequest(new OperationResultDto(
                false,
                "请先明确选择账号首次连接出口：已有代理、自动分配已有 WARP、创建一对一 WARP、已配置的全局代理或明确直连；逐账号批量代理仅支持 Zip 导入"));
        }
        await importService.ValidateImportProxyBindingAsync(proxyBinding, cancellationToken);

        var result = await importService.ImportFromStringSessionBalancedAsync(
            sessionString,
            request.CategoryId,
            proxyBinding,
            cancellationToken,
            request.DeviceProfileKey);
        return Results.Ok(await BuildImportResponseAsync(new[] { result }, accountManagement));
    }

    private static async Task<IResult> StartAccountLoginAsync(
        StartAccountLoginRequestDto request,
        IAccountService accountService,
        AccountManagementService accountManagement,
        TelegramApiProfilePool apiProfilePool,
        AccountLoginProxyCoordinator loginProxy,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {


        var phone = (request.Phone ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(phone))
            return Results.BadRequest(new OperationResultDto(false, "请输入手机号（包含国家代码）"));

        var reuseLoginId = request.LoginId > 0 && loginProxy.HasState(request.LoginId);
        var loginId = reuseLoginId
            ? request.LoginId
            : await ResolveLoginIdAsync(
                request.LoginId,
                loginProxy,
                accountManagement);
        TelegramApiCredentials apiCredentials;
        try
        {
            var existingAccount = await accountManagement.GetAccountByPhoneAsync(phone);
            apiCredentials = await apiProfilePool.SelectForAccountAsync(existingAccount, accountManagement);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }
        AccountLoginProxyStateLease? reuseLease = null;
        AccountLoginProxyState proxyState;
        try
        {
            if (reuseLoginId)
            {
                reuseLease = loginProxy.ClaimFrozenState(
                    loginId,
                    request.ProxyStrategy,
                    request.ProxyId);
                try
                {
                    await accountService.ReleaseClientStrictAsync(loginId);
                }
                catch (Exception ex)
                {
                    reuseLease.Dispose();
                    reuseLease = null;
                    return Results.BadRequest(new AccountLoginResponseDto(
                        false,
                        loginId,
                        null,
                        $"旧登录客户端无法安全停止，已保留冻结路由并阻止重新发送验证码：{ex.Message}",
                        null));
                }

                proxyState = reuseLease.State;
            }
            else
            {
                proxyState = await loginProxy.PrepareAsync(
                    loginId,
                    request.ProxyStrategy,
                    request.ProxyId,
                    cancellationToken,
                    request.DeviceProfileKey);
            }
        }
        catch (Exception ex) when (IsLoginProxyInputError(ex))
        {
            reuseLease?.Dispose();
            return Results.BadRequest(new AccountLoginResponseDto(
                false,
                loginId,
                null,
                ex.Message,
                null));
        }

        try
        {
            await loginProxy.QuiesceExistingAccountAsync(loginId, phone, cancellationToken);
        }
        catch (Exception ex)
        {
            reuseLease?.Dispose();
            if (!reuseLoginId)
                await loginProxy.AbandonAsync(loginId, CancellationToken.None);
            return Results.BadRequest(new AccountLoginResponseDto(
                false,
                loginId,
                null,
                ex.Message,
                null));
        }

        LoginResult result;
        try
        {
            result = await accountService.StartLoginAsync(
                loginId,
                phone,
                proxyState.Resolution,
                apiCredentials,
                proxyState.DeviceProfileKey);
        }
        catch (Exception ex) when (IsLoginProxyInputError(ex))
        {
            reuseLease?.Dispose();
            if (!reuseLoginId)
                await loginProxy.AbandonAsync(loginId, CancellationToken.None);
            return Results.BadRequest(new AccountLoginResponseDto(
                false,
                loginId,
                null,
                ex.Message,
                null));
        }
        catch
        {
            reuseLease?.Dispose();
            if (!reuseLoginId)
                await loginProxy.AbandonAsync(loginId, CancellationToken.None);
            throw;
        }

        reuseLease?.Dispose();
        return await BuildLoginResponseAsync(
            loginId,
            result,
            accountService,
            accountManagement,
            loginProxy,
            configuration,
            cancellationToken: cancellationToken);
    }

    private static async Task<IResult> StartAccountQrLoginAsync(
        StartAccountQrLoginRequestDto request,
        IAccountService accountService,
        AccountManagementService accountManagement,
        TelegramApiProfilePool apiProfilePool,
        AccountLoginProxyCoordinator loginProxy,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {


        var reuseLoginId = request.LoginId > 0 && loginProxy.HasState(request.LoginId);
        var loginId = reuseLoginId
            ? request.LoginId
            : await ResolveLoginIdAsync(
                request.LoginId,
                loginProxy,
                accountManagement);
        TelegramApiCredentials apiCredentials;
        try
        {
            apiCredentials = await apiProfilePool.SelectForNewAccountAsync(accountManagement);
        }
        catch (Exception ex)
        {
            return Results.Ok(new AccountQrLoginResponseDto(false, request.LoginId, "failed", ex.Message, null, null, null));
        }
        AccountLoginProxyStateLease? reuseLease = null;
        AccountLoginProxyState proxyState;
        try
        {
            if (reuseLoginId)
            {
                reuseLease = loginProxy.ClaimFrozenState(
                    loginId,
                    request.ProxyStrategy,
                    request.ProxyId);
                try
                {
                    await accountService.CancelQrLoginStrictAsync(loginId);
                }
                catch (Exception ex)
                {
                    reuseLease.Dispose();
                    reuseLease = null;
                    return Results.Ok(new AccountQrLoginResponseDto(
                        false,
                        loginId,
                        "failed",
                        $"旧二维码登录客户端无法安全停止，已保留冻结路由并阻止重新生成：{ex.Message}",
                        null,
                        null,
                        null));
                }

                proxyState = reuseLease.State;
            }
            else
            {
                proxyState = await loginProxy.PrepareAsync(
                    loginId,
                    request.ProxyStrategy,
                    request.ProxyId,
                    cancellationToken,
                    request.DeviceProfileKey);
            }
        }
        catch (Exception ex) when (IsLoginProxyInputError(ex))
        {
            reuseLease?.Dispose();
            return Results.Ok(new AccountQrLoginResponseDto(
                false,
                loginId,
                "failed",
                ex.Message,
                null,
                null,
                null));
        }

        QrLoginResult result;
        try
        {
            result = await accountService.StartQrLoginAsync(
                loginId,
                proxyState.Resolution,
                apiCredentials,
                proxyState.DeviceProfileKey);
        }
        catch
        {
            reuseLease?.Dispose();
            if (!reuseLoginId)
                await loginProxy.AbandonAsync(loginId, CancellationToken.None);
            throw;
        }

        reuseLease?.Dispose();
        return await BuildQrLoginResponseAsync(
            result,
            accountService,
            accountManagement,
            loginProxy,
            configuration,
            cancellationToken: cancellationToken);
    }

    private static async Task<IResult> PollAccountQrLoginAsync(
        AccountLoginSessionRequestDto request,
        IAccountService accountService,
        AccountManagementService accountManagement,
        AccountLoginProxyCoordinator loginProxy,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (request.LoginId <= 0)
            return Results.Ok(new AccountQrLoginResponseDto(false, request.LoginId, "expired", "扫码登录会话已失效，请重新生成二维码", null, null, null));
        if (!loginProxy.HasState(request.LoginId))
            return Results.Ok(new AccountQrLoginResponseDto(false, request.LoginId, "expired", "登录代理会话已失效，请重新生成二维码", null, null, null));

        var result = await accountService.PollQrLoginAsync(request.LoginId);
        return await BuildQrLoginResponseAsync(
            result,
            accountService,
            accountManagement,
            loginProxy,
            configuration,
            cancellationToken: cancellationToken);
    }

    private static async Task<IResult> SubmitAccountQrLoginPasswordAsync(
        AccountLoginPasswordRequestDto request,
        IAccountService accountService,
        AccountManagementService accountManagement,
        AccountLoginProxyCoordinator loginProxy,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (request.LoginId <= 0)
            return Results.Ok(new AccountQrLoginResponseDto(false, request.LoginId, "expired", "扫码登录会话已失效，请重新生成二维码", null, null, null));
        if (!loginProxy.HasState(request.LoginId))
            return Results.Ok(new AccountQrLoginResponseDto(false, request.LoginId, "expired", "登录代理会话已失效，请重新生成二维码", null, null, null));

        var password = request.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
            return Results.Ok(new AccountQrLoginResponseDto(false, request.LoginId, "password", "请输入两步验证密码", null, null, null));

        var result = await accountService.SubmitQrPasswordAsync(request.LoginId, password);
        var passwordToSave = request.SaveTwoFactorPassword == true ? password : null;
        return await BuildQrLoginResponseAsync(
            result,
            accountService,
            accountManagement,
            loginProxy,
            configuration,
            passwordToSave,
            cancellationToken);
    }

    private static async Task<IResult> CancelAccountQrLoginAsync(
        AccountLoginSessionRequestDto request,
        IAccountService accountService,
        AccountLoginProxyCoordinator loginProxy)
    {
        if (request.LoginId > 0)
        {
            try
            {
                await accountService.CancelQrLoginStrictAsync(request.LoginId);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new OperationResultDto(
                    false,
                    $"旧二维码登录客户端无法安全停止，已保留冻结路由：{ex.Message}"));
            }

            await loginProxy.AbandonAsync(request.LoginId, CancellationToken.None);
        }

        return Results.Ok(new OperationResultDto(true, "扫码登录会话已取消"));
    }

    private static async Task<IResult> SubmitAccountLoginCodeAsync(
        AccountLoginCodeRequestDto request,
        IAccountService accountService,
        AccountManagementService accountManagement,
        AccountLoginProxyCoordinator loginProxy,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (request.LoginId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "登录会话已失效，请重新发送验证码"));
        if (!loginProxy.HasState(request.LoginId))
            return Results.BadRequest(new OperationResultDto(false, "登录代理会话已失效，请重新发送验证码"));

        var code = (request.Code ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
            return Results.BadRequest(new OperationResultDto(false, "请输入验证码"));

        var result = await accountService.SubmitCodeAsync(request.LoginId, code);
        return await BuildLoginResponseAsync(
            request.LoginId,
            result,
            accountService,
            accountManagement,
            loginProxy,
            configuration,
            cancellationToken: cancellationToken);
    }

    private static async Task<IResult> ResendAccountLoginCodeAsync(
        AccountLoginSessionRequestDto request,
        IAccountService accountService,
        AccountManagementService accountManagement,
        AccountLoginProxyCoordinator loginProxy,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (request.LoginId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "登录会话已失效，请重新发送验证码"));
        if (!loginProxy.HasState(request.LoginId))
            return Results.BadRequest(new OperationResultDto(false, "登录代理会话已失效，请重新发送验证码"));

        var result = await accountService.ResendCodeAsync(request.LoginId);
        return await BuildLoginResponseAsync(
            request.LoginId,
            result,
            accountService,
            accountManagement,
            loginProxy,
            configuration,
            cancellationToken: cancellationToken);
    }

    private static async Task<IResult> SubmitAccountLoginPasswordAsync(
        AccountLoginPasswordRequestDto request,
        IAccountService accountService,
        AccountManagementService accountManagement,
        AccountLoginProxyCoordinator loginProxy,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (request.LoginId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "登录会话已失效，请重新发送验证码"));
        if (!loginProxy.HasState(request.LoginId))
            return Results.BadRequest(new OperationResultDto(false, "登录代理会话已失效，请重新发送验证码"));

        var password = request.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
            return Results.BadRequest(new OperationResultDto(false, "请输入两步验证密码"));

        var result = await accountService.SubmitPasswordAsync(request.LoginId, password);
        var passwordToSave = request.SaveTwoFactorPassword == true ? password : null;
        return await BuildLoginResponseAsync(
            request.LoginId,
            result,
            accountService,
            accountManagement,
            loginProxy,
            configuration,
            passwordToSave,
            cancellationToken);
    }

    private static async Task<IResult> ResetAccountLoginAsync(
        AccountLoginSessionRequestDto request,
        IAccountService accountService,
        AccountLoginProxyCoordinator loginProxy)
    {
        if (request.LoginId > 0)
        {
            try
            {
                await accountService.ReleaseClientStrictAsync(request.LoginId);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new OperationResultDto(
                    false,
                    $"旧登录客户端无法安全停止，已保留冻结路由：{ex.Message}"));
            }

            await loginProxy.AbandonAsync(request.LoginId, CancellationToken.None);
        }

        return Results.Ok(new OperationResultDto(true, "登录会话已释放"));
    }

    private static async Task<IResult> GetOperationAccountsAsync(AccountManagementService accounts)
    {
        var items = (await accounts.GetAllAccountsAsync())
            .Where(x => x.Category?.ExcludeFromOperations != true)
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.DisplayNumber)
            .ThenBy(x => x.DisplayPhone, StringComparer.OrdinalIgnoreCase)
            .Select(ToOperationAccountDto)
            .ToList();

        return Results.Ok(items);
    }

    private static Task<IResult> GetSettingsAsync(IConfiguration configuration, IWebHostEnvironment environment, PanelTimeZoneService timeZone)
    {
        var localPath = LocalConfigFile.ResolvePath(configuration, environment);
        var tz = timeZone.Current;
        var offset = tz.BaseUtcOffset >= TimeSpan.Zero ? "+" + tz.BaseUtcOffset.ToString(@"hh\:mm") : "-" + tz.BaseUtcOffset.Duration().ToString(@"hh\:mm");
        var telegramApiStatus = ReadTelegramApiRuntimeStatus(configuration);
        var dto = new SettingsDto(
            LocalConfigPath: localPath,
            LocalConfigExists: File.Exists(localPath),
            Telegram: new TelegramApiSettingsDto(
                ReadConfiguredTelegramApiId(configuration),
                ReadConfiguredTelegramApiHash(configuration),
                ReadTelegramApiProfiles(configuration),
                TelegramApiProfilePool.IsOfficialApiEnabled(configuration),
                TelegramDeviceProfileCatalog.ReadProfiles(configuration).Select(ToDto).ToList(),
                TelegramDeviceProfileCatalog.ResolveDefaultKey(configuration),
                telegramApiStatus.EffectiveApiId,
                telegramApiStatus.EffectiveApiSource,
                telegramApiStatus.EffectiveApiName,
                TelegramApiProfilePool.OfficialAndroidApiId.ToString(CultureInfo.InvariantCulture),
                TelegramApiProfilePool.OfficialAndroidApiName,
                telegramApiStatus.HasUsableApi),
            GlobalProxy: ReadGlobalProxySettings(configuration),
            CloudMail: new CloudMailSettingsDto(configuration["CloudMail:BaseUrl"] ?? "", configuration["CloudMail:Domain"] ?? "", configuration["CloudMail:Token"] ?? ""),
            Ai: new AiSettingsDto(
                configuration["AI:OpenAI:Endpoint"] ?? "",
                configuration["AI:OpenAI:ApiKey"] ?? "",
                configuration["AI:OpenAI:DefaultModel"] ?? "",
                configuration.GetSection("AI:OpenAI:PresetModels").GetChildren().Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList(),
                AiOpenAiSettingsSnapshot.NormalizeRetryCount(configuration.GetValue("AI:OpenAI:RetryCount", 2))),
            Batch: new BatchSettingsDto(
                configuration.GetValue("Telegram:DefaultDelayMs", 2000),
                configuration.GetValue("BatchTasks:MaxConcurrent", 1),
                configuration.GetValue("BatchTasks:HistoryRetentionLimit", 0),
                configuration.GetValue("Telegram:MaxRetries", 0) > 0,
                configuration.GetValue("Telegram:MaxRetries", 3)),
            Sync: new SyncSettingsDto(configuration.GetValue("Sync:AutoSyncEnabled", false), configuration.GetValue("Sync:IntervalHours", 6)),
            BotAutoSync: new BotAutoSyncSettingsDto(configuration.GetValue("Telegram:BotAutoSyncEnabled", false), configuration.GetValue("Telegram:BotAutoSyncIntervalSeconds", 2)),
            TelegramStatus: new TelegramStatusAutoRefreshSettingsDto(
                configuration.GetValue("TelegramStatus:AutoRefreshTransientFailures", true),
                configuration.GetValue("TelegramStatus:AutoRefreshIntervalMinutes", 30),
                configuration.GetValue("TelegramStatus:AutoRefreshBatchSize", 3),
                configuration.GetValue("TelegramStatus:AutoRefreshMinAgeMinutes", 10),
                configuration.GetValue("TelegramStatus:AutoRefreshDelayMs", 5000)),
            Logging: new LoggingSettingsDto(configuration.GetValue("Serilog:Enabled", false), configuration["Serilog:MinimumLevel:Default"] ?? "Information", configuration.GetValue("Serilog:RetainedFileCountLimit", 30)),
            TimeZone: new TimeZoneSettingsDto(configuration["System:TimeZoneId"] ?? "", $"{tz.Id}（UTC{offset}）"),
            System: new SystemInfoSettingsDto(VersionService.Version, ".NET 8.0", "SQLite", telegramApiStatus.EffectiveApiId));

        return Task.FromResult<IResult>(Results.Ok(dto));
    }
    private static IResult GetDeviceProfilesAsync(IConfiguration configuration) =>
        Results.Ok(new
        {
            items = TelegramDeviceProfileCatalog.ReadProfiles(configuration).Select(ToDto).ToList(),
            defaultKey = TelegramDeviceProfileCatalog.ResolveDefaultKey(configuration)
        });

    private static async Task<IResult> GetVersionInfoAsync(AppSelfUpdateService selfUpdate, CancellationToken cancellationToken)
    {
        var info = await selfUpdate.CheckLatestAsync(forceRefresh: false, cancellationToken);
        return Results.Ok(ToDto(info));
    }

    private static async Task<IResult> CheckVersionInfoAsync(AppSelfUpdateService selfUpdate, CancellationToken cancellationToken)
    {
        var info = await selfUpdate.CheckLatestAsync(forceRefresh: true, cancellationToken);
        return Results.Ok(ToDto(info));
    }

    private static async Task<IResult> ApplyVersionUpdateAsync(AppSelfUpdateService selfUpdate, CancellationToken cancellationToken)
    {
        var result = await selfUpdate.ApplyLatestAsync(cancellationToken);
        return result.Success
            ? Results.Ok(ToDto(result))
            : Results.BadRequest(ToDto(result));
    }

    private static async Task<IResult> SaveUpdateModeAsync(
        UpdateModeRequestDto request,
        UpdateModeStore updateModeStore,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var mode = SelfUpdateOptions.NormalizeMode(request.Mode);
        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var selfUpdate = EnsureObject(root, "SelfUpdate");
        selfUpdate["Mode"] = mode;
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        await updateModeStore.SetModeAsync(mode, cancellationToken);
        return Results.Ok(new UpdateModeResultDto(true, mode, "更新方式已保存，重启面板后生效"));
    }

    private static async Task<IResult> SaveTelegramApiSettingsAsync(
        TelegramApiSettingsDto request,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ITelegramClientPool telegramClientPool,
        CancellationToken cancellationToken)
    {
        var requestedProfiles = request.Profiles ?? ReadTelegramApiProfiles(configuration);
        var profiles = NormalizeTelegramApiProfiles(requestedProfiles, out var profileError);
        if (profileError != null)
            return Results.BadRequest(new OperationResultDto(false, profileError));

        var (hasDefaultApi, defaultApiIdText, defaultApiHashText) = NormalizeTelegramApiDefaultInput(request.ApiId, request.ApiHash);
        if (hasDefaultApi)
        {
            if (!int.TryParse(defaultApiIdText, out var apiId) || apiId <= 0)
                return Results.BadRequest(new OperationResultDto(false, "旧版单 API ID 格式错误"));
            if (!TelegramApiConfigValidator.TryNormalizeApiHash(defaultApiHashText, out var apiHash, out var reason))
                return Results.BadRequest(new OperationResultDto(false, $"旧版单 API Hash 无效：{reason}"));
            request = request with { ApiId = apiId.ToString(CultureInfo.InvariantCulture), ApiHash = apiHash };
        }
        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var telegram = EnsureObject(root, "Telegram");
        if (hasDefaultApi)
        {
            telegram["ApiId"] = int.Parse(request.ApiId, CultureInfo.InvariantCulture);
            telegram["ApiHash"] = request.ApiHash;
        }
        else
        {
            telegram["ApiId"] = 0;
            telegram["ApiHash"] = string.Empty;
        }
        var selectedDeviceProfile = TelegramDeviceProfileCatalog.Find(configuration, request.DefaultDeviceProfileKey);
        if (!string.IsNullOrWhiteSpace(request.DefaultDeviceProfileKey) && selectedDeviceProfile == null)
            return Results.BadRequest(new OperationResultDto(false, "默认设备指纹不存在或已停用"));
        telegram["DefaultDeviceProfileKey"] = selectedDeviceProfile?.Key ?? TelegramDeviceProfileCatalog.DefaultProfileKey;
        telegram["OfficialApiEnabled"] = request.OfficialApiEnabled;
        var normalizedProfiles = new JsonArray();
        foreach (var profile in profiles)
        {
            normalizedProfiles.Add(new JsonObject
            {
                ["Name"] = profile.Name,
                ["ApiId"] = profile.ApiId,
                ["ApiHash"] = profile.ApiHash,
                ["Enabled"] = profile.Enabled,
                ["Weight"] = profile.Weight,
                ["Notes"] = profile.Notes
            });
        }
        telegram["ApiProfiles"] = normalizedProfiles;
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        await telegramClientPool.RemoveAllClientsAsync();
        return Results.Ok(new OperationResultDto(true, "API 配置已保存，Telegram 客户端缓存已清理"));
    }

    internal static (bool HasDefaultApi, string ApiId, string ApiHash) NormalizeTelegramApiDefaultInput(string? apiId, string? apiHash)
    {
        var apiIdText = (apiId ?? string.Empty).Trim();
        var apiHashText = (apiHash ?? string.Empty).Trim();
        if (apiIdText == "0" && string.IsNullOrWhiteSpace(apiHashText))
            apiIdText = string.Empty;

        return (!string.IsNullOrWhiteSpace(apiIdText) || !string.IsNullOrWhiteSpace(apiHashText), apiIdText, apiHashText);
    }

    private static IReadOnlyList<TelegramApiProfileDto> ReadTelegramApiProfiles(IConfiguration configuration) =>
        TelegramApiProfilePool.ReadConfiguredProfiles(configuration)
            .Select(profile => new TelegramApiProfileDto(
                profile.Name,
                profile.ApiId.ToString(CultureInfo.InvariantCulture),
                profile.ApiHash,
                profile.Enabled,
                profile.Weight,
                profile.Notes))
            .ToList();

    internal sealed record TelegramApiRuntimeStatus(
        string EffectiveApiId,
        string EffectiveApiSource,
        string EffectiveApiName,
        bool HasUsableApi);

    internal static TelegramApiRuntimeStatus ReadTelegramApiRuntimeStatus(IConfiguration configuration)
    {
        var profiles = TelegramApiProfilePool.GetEnabledPoolProfiles(configuration);
        if (profiles.Count > 0)
        {
            var profile = profiles[0];
            var isOfficialAndroid = profile.ApiId == TelegramApiProfilePool.OfficialAndroidApiId
                && string.Equals(profile.ApiHash, TelegramApiProfilePool.OfficialAndroidApiHash, StringComparison.OrdinalIgnoreCase);
            return new TelegramApiRuntimeStatus(
                profile.ApiId.ToString(CultureInfo.InvariantCulture),
                isOfficialAndroid ? "built_in_official" : "api_profile",
                profile.Name,
                true);
        }

        if (TelegramApiProfilePool.TryGetGlobalFallback(configuration, out var credentials))
        {
            var isOfficialAndroid = credentials.ApiId == TelegramApiProfilePool.OfficialAndroidApiId
                && string.Equals(credentials.ApiHash, TelegramApiProfilePool.OfficialAndroidApiHash, StringComparison.OrdinalIgnoreCase);
            return new TelegramApiRuntimeStatus(
                credentials.ApiId.ToString(CultureInfo.InvariantCulture),
                isOfficialAndroid ? "built_in_official" : "custom_default",
                credentials.ProfileName ?? (isOfficialAndroid ? TelegramApiProfilePool.OfficialAndroidApiName : "旧版单 API"),
                true);
        }

        var configuredApiId = ReadConfiguredTelegramApiId(configuration);
        return new TelegramApiRuntimeStatus(configuredApiId, "invalid", "不可用", false);
    }

    private static string ReadConfiguredTelegramApiId(IConfiguration configuration)
    {
        var configuredApiId = (configuration["Telegram:ApiId"] ?? string.Empty).Trim();
        return configuredApiId == "0" ? string.Empty : configuredApiId;
    }

    private static string ReadConfiguredTelegramApiHash(IConfiguration configuration) =>
        configuration["Telegram:ApiHash"] ?? string.Empty;

    private static IReadOnlyList<TelegramApiProfile> NormalizeTelegramApiProfiles(
        IReadOnlyList<TelegramApiProfileDto>? profiles,
        out string? error)
    {
        error = null;
        var normalized = new List<TelegramApiProfile>();
        if (profiles == null || profiles.Count == 0)
            return normalized;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var name = (profile.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = $"API {i + 1}";
            if (!names.Add(name))
            {
                error = $"API 配置名称重复：{name}";
                return Array.Empty<TelegramApiProfile>();
            }

            if (!int.TryParse(profile.ApiId, out var apiId) || apiId <= 0)
            {
                error = $"API 配置 {name} 的 ApiId 格式错误";
                return Array.Empty<TelegramApiProfile>();
            }

            if (!TelegramApiConfigValidator.TryNormalizeApiHash(profile.ApiHash, out var apiHash, out var reason))
            {
                error = $"API 配置 {name} 的 ApiHash 无效：{reason}";
                return Array.Empty<TelegramApiProfile>();
            }

            normalized.Add(new TelegramApiProfile(
                name,
                apiId,
                apiHash,
                profile.Enabled,
                Math.Clamp(profile.Weight <= 0 ? 1 : profile.Weight, 1, 1000),
                string.IsNullOrWhiteSpace(profile.Notes) ? null : profile.Notes.Trim()));
        }

        return normalized;
    }

    private static GlobalProxySettingsDto ReadGlobalProxySettings(IConfiguration configuration)
    {
        var server = (configuration["Telegram:Proxy:Server"] ?? string.Empty).Trim();
        var portText = (configuration["Telegram:Proxy:Port"] ?? string.Empty).Trim();
        var secret = (configuration["Telegram:Proxy:Secret"] ?? string.Empty).Trim();
        var configuredProtocol = (configuration["Telegram:Proxy:Protocol"] ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        var protocol = OutboundProxyProtocols.IsSupported(configuredProtocol)
            ? configuredProtocol
            : string.IsNullOrWhiteSpace(secret)
                ? OutboundProxyProtocols.Socks5
                : OutboundProxyProtocols.MtProto;

        return new GlobalProxySettingsDto(
            // 统一使用配置解析器判断启用状态。已有代理模式没有 Server/Port，
            // 不能再按旧手动地址推断，否则后台显示会与运行时路由不一致。
            Enabled: GlobalTelegramProxyConfiguration.IsEnabled(configuration),
            Protocol: protocol,
            Server: server,
            Port: int.TryParse(portText, out var port) ? port : 0,
            Username: configuration["Telegram:Proxy:Username"] ?? string.Empty,
            HasPassword: protocol != OutboundProxyProtocols.MtProto
                         && !string.IsNullOrWhiteSpace(configuration["Telegram:Proxy:Password"]),
            HasSecret: protocol == OutboundProxyProtocols.MtProto
                       && !string.IsNullOrWhiteSpace(secret),
            SourceMode: GlobalTelegramProxyConfiguration.GetSourceMode(configuration),
            ProxyId: GlobalTelegramProxyConfiguration.GetSelectedProxyId(
                configuration,
                requireEnabled: false));
    }

    private static async Task<IResult> GetGlobalProxySettingsAsync(
        IConfiguration configuration,
        ProxyManagementService proxyManagement,
        CancellationToken cancellationToken)
    {
        var settings = ReadGlobalProxySettings(configuration);
        if (settings.SourceMode != GlobalTelegramProxyConfiguration.ExistingSourceMode
            || settings.ProxyId is not > 0)
            return Results.Ok(settings);

        var proxy = await proxyManagement.GetAsync(
            settings.ProxyId.Value,
            cancellationToken: cancellationToken);
        return Results.Ok(settings with
        {
            ProxyName = proxy?.Name,
            ProxyKind = proxy?.Kind
        });
    }

    private static Task<IResult> SaveGlobalProxySettingsEndpointAsync(
        SaveGlobalProxySettingsRequestDto request,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ITelegramClientPool telegramClientPool,
        ProxyManagementService proxyManagement,
        CancellationToken cancellationToken) =>
        SaveGlobalProxySettingsAsync(
            request,
            configuration,
            environment,
            telegramClientPool,
            cancellationToken,
            proxyManagement);

    internal static async Task<IResult> SaveGlobalProxySettingsAsync(
        SaveGlobalProxySettingsRequestDto request,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ITelegramClientPool telegramClientPool,
        CancellationToken cancellationToken,
        ProxyManagementService? proxyManagement = null)
    {
        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var telegram = EnsureObject(root, "Telegram");

        if (!request.Enabled)
        {
            var disabledProxy = EnsureObject(telegram, "Proxy");
            disabledProxy["Enabled"] = false;
            await PersistGlobalProxyChangeAsync(
                proxyManagement,
                nextEnabled: false,
                nextSourceMode: GlobalTelegramProxyConfiguration.GetSourceMode(configuration),
                nextProxyId: GlobalTelegramProxyConfiguration.GetSelectedProxyId(
                    configuration,
                    requireEnabled: false),
                configuration,
                environment,
                root,
                telegramClientPool,
                cancellationToken);
            return Results.Ok(new OperationResultDto(true, "全局代理已关闭，Telegram 客户端缓存已清理"));
        }

        var sourceMode = (request.SourceMode ?? GlobalTelegramProxyConfiguration.ManualSourceMode)
            .Trim()
            .ToLowerInvariant();
        if (sourceMode == GlobalTelegramProxyConfiguration.ExistingSourceMode)
        {
            if (request.ProxyId is not > 0)
                return Results.BadRequest(new OperationResultDto(false, "请选择已有代理"));
            if (proxyManagement == null)
                return Results.BadRequest(new OperationResultDto(false, "无法解析已有代理，请刷新后重试"));

            var selected = await proxyManagement.GetAsync(
                request.ProxyId.Value,
                cancellationToken: cancellationToken);
            if (selected is not { IsEnabled: true })
                return Results.BadRequest(new OperationResultDto(false, "所选代理不存在或已停用"));

            var selectedProxy = EnsureObject(telegram, "Proxy");
            selectedProxy["Enabled"] = true;
            selectedProxy["SourceMode"] = GlobalTelegramProxyConfiguration.ExistingSourceMode;
            selectedProxy["ProxyId"] = selected.Id;
            // 引用已有代理时不写入 Server/Password/Secret，运行时始终从数据库读取。
            selectedProxy.Remove("Server");
            selectedProxy.Remove("Port");
            selectedProxy.Remove("Protocol");
            selectedProxy.Remove("Username");
            selectedProxy.Remove("Password");
            selectedProxy.Remove("Secret");
            await PersistGlobalProxyChangeAsync(
                proxyManagement,
                nextEnabled: true,
                nextSourceMode: GlobalTelegramProxyConfiguration.ExistingSourceMode,
                nextProxyId: selected.Id,
                configuration,
                environment,
                root,
                telegramClientPool,
                cancellationToken);
            return Results.Ok(new OperationResultDto(true, "全局代理已切换为已有代理，Telegram 客户端缓存已清理"));
        }
        if (sourceMode != GlobalTelegramProxyConfiguration.ManualSourceMode)
            return Results.BadRequest(new OperationResultDto(false, "全局代理来源仅支持 manual 或 existing"));

        var protocol = (request.Protocol ?? string.Empty).Trim().ToLowerInvariant();
        if (!OutboundProxyProtocols.IsSupported(protocol))
            return Results.BadRequest(new OperationResultDto(false, "全局代理协议仅支持 http、socks5 或 mtproto"));

        var server = (request.Server ?? string.Empty).Trim().Trim('[', ']');
        if (server.Length is 0 or > 253
            || server.Any(char.IsControl)
            || server.Any(char.IsWhiteSpace)
            || server.Contains('/')
            || server.Contains('@'))
        {
            return Results.BadRequest(new OperationResultDto(false, "全局代理主机格式无效"));
        }
        server = server.ToLowerInvariant();
        if (request.Port is < 1 or > 65535)
            return Results.BadRequest(new OperationResultDto(false, "全局代理端口必须在 1-65535 之间"));

        var username = (request.Username ?? string.Empty).Trim();
        var current = ReadGlobalProxySettings(configuration);
        var submittedPassword = (request.Password ?? string.Empty).Trim();
        if (Encoding.UTF8.GetByteCount(username) > 255
            || Encoding.UTF8.GetByteCount(submittedPassword) > 255)
        {
            return Results.BadRequest(new OperationResultDto(
                false,
                "全局代理用户名或密码不能超过 255 个 UTF-8 字节"));
        }

        var submittedSecret = (request.Secret ?? string.Empty).Trim();
        if (submittedSecret.Length > 500)
            return Results.BadRequest(new OperationResultDto(false, "MTProxy Secret 不能超过 500 个字符"));
        if (protocol == OutboundProxyProtocols.MtProto
            && string.IsNullOrWhiteSpace(submittedSecret)
            && !current.HasSecret)
        {
            return Results.BadRequest(new OperationResultDto(false, "MTProxy 必须填写 Secret"));
        }

        var proxy = EnsureObject(telegram, "Proxy");
        proxy["Enabled"] = true;
        proxy["SourceMode"] = GlobalTelegramProxyConfiguration.ManualSourceMode;
        proxy.Remove("ProxyId");
        proxy["Protocol"] = protocol;
        proxy["Server"] = server;
        proxy["Port"] = request.Port;
        if (protocol == OutboundProxyProtocols.MtProto)
        {
            proxy.Remove("Username");
            proxy.Remove("Password");
            if (!string.IsNullOrWhiteSpace(submittedSecret))
                proxy["Secret"] = submittedSecret;
        }
        else
        {
            proxy["Username"] = username;
            if (request.ClearPassword)
            {
                // 写入空值才能覆盖仍存在的环境变量凭据。
                proxy["Password"] = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(submittedPassword))
            {
                proxy["Password"] = submittedPassword;
            }
            else if (current.Protocol == OutboundProxyProtocols.MtProto)
            {
                // 从 MTProxy 切换时不要激活此前被忽略的上游密码。
                proxy["Password"] = string.Empty;
            }

            // 留空保持时不读取 IConfiguration 中的敏感值，避免把环境变量
            // 密码物化到 appsettings.local.json；本地已有字段会原样保留。
            proxy.Remove("Secret");
        }

        await PersistGlobalProxyChangeAsync(
            proxyManagement,
            nextEnabled: true,
            nextSourceMode: GlobalTelegramProxyConfiguration.ManualSourceMode,
            nextProxyId: null,
            configuration,
            environment,
            root,
            telegramClientPool,
            cancellationToken);
        return Results.Ok(new OperationResultDto(true, "全局代理配置已保存，Telegram 客户端缓存已清理"));
    }

    private static async Task PersistGlobalProxyChangeAsync(
        ProxyManagementService? proxyManagement,
        bool nextEnabled,
        string nextSourceMode,
        int? nextProxyId,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        JsonObject root,
        ITelegramClientPool telegramClientPool,
        CancellationToken cancellationToken)
    {
        async Task ApplyAsync(CancellationToken applyCancellationToken)
        {
            await SaveLocalRootAsync(
                configuration,
                environment,
                root,
                applyCancellationToken);
            ReloadConfiguration(configuration);
            // 配置生效后再严格清空客户端池。清理会提升连接代际，既能淘汰
            // 保存前的旧客户端，也能拒绝保存窗口中按旧配置创建的客户端写回。
            await telegramClientPool.RemoveAllClientsAsync();
        }

        if (proxyManagement != null)
        {
            await proxyManagement.ExecuteGlobalProxyChangeAsync(
                nextEnabled,
                nextSourceMode,
                nextProxyId,
                ApplyAsync,
                cancellationToken);
            return;
        }
        else
        {
            // 仅保留给没有注册代理管理服务的轻量测试/兼容调用方。
            await ApplyAsync(cancellationToken);
        }
    }

    private static void ReloadConfiguration(IConfiguration configuration)
    {
        if (configuration is IConfigurationRoot root)
            root.Reload();
    }

    private static async Task<IResult> SaveCloudMailSettingsAsync(CloudMailSettingsDto request, IConfiguration configuration, IWebHostEnvironment environment, CancellationToken cancellationToken)
    {
        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var cloud = EnsureObject(root, "CloudMail");
        cloud["BaseUrl"] = (request.BaseUrl ?? string.Empty).Trim();
        cloud["Domain"] = (request.Domain ?? string.Empty).Trim().TrimStart('@');
        cloud["Token"] = (request.Token ?? string.Empty).Trim();
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "Cloud Mail 配置已保存"));
    }

    private static async Task<IResult> GenerateCloudMailTokenAsync(GenerateCloudMailTokenRequestDto request, CloudMailClient cloudMail, CancellationToken cancellationToken)
    {
        var token = await cloudMail.GenerateTokenAsync(request.BaseUrl ?? "", request.AdminEmail ?? "", request.AdminPassword ?? "", cancellationToken);
        return Results.Ok(new CloudMailTokenResultDto(token));
    }

    private static async Task<IResult> SaveAiSettingsAsync(AiSettingsDto request, IConfiguration configuration, IWebHostEnvironment environment, CancellationToken cancellationToken)
    {
        var endpoint = AiOpenAiSettingsSnapshot.NormalizeEndpoint(request.Endpoint) ?? string.Empty;
        var apiKey = AiOpenAiSettingsSnapshot.NormalizeApiKey(request.ApiKey) ?? string.Empty;
        var defaultModel = AiOpenAiSettingsSnapshot.NormalizeModel(request.DefaultModel) ?? string.Empty;
        var presetModels = AiOpenAiSettingsSnapshot.NormalizeModelEntries(request.PresetModels, defaultModel);
        var retryCount = AiOpenAiSettingsSnapshot.NormalizeRetryCount(request.RetryCount);

        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var ai = EnsureObject(root, "AI");
        var openAi = EnsureObject(ai, "OpenAI");
        openAi["Endpoint"] = endpoint;
        openAi["ApiKey"] = apiKey;
        openAi["DefaultModel"] = defaultModel;
        openAi["RetryCount"] = retryCount;
        var models = new JsonArray();
        foreach (var model in presetModels)
            models.Add(model);
        openAi["PresetModels"] = models;
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "AI 配置已保存"));
    }

    private static async Task<IResult> TestAiSettingsAsync(AiSettingsDto request, TelegramPanelAiService aiService, CancellationToken cancellationToken)
    {
        var snapshot = new AiOpenAiSettingsSnapshot(
            AiOpenAiSettingsSnapshot.NormalizeEndpoint(request.Endpoint),
            AiOpenAiSettingsSnapshot.NormalizeApiKey(request.ApiKey),
            AiOpenAiSettingsSnapshot.NormalizeModel(request.DefaultModel),
            AiOpenAiSettingsSnapshot.NormalizeModelEntries(request.PresetModels, request.DefaultModel),
            AiOpenAiSettingsSnapshot.NormalizeRetryCount(request.RetryCount));
        var model = snapshot.ResolveModel(request.DefaultModel);
        if (!snapshot.TryValidateForTask(model, out var error))
            return Results.BadRequest(new OperationResultDto(false, error));

        var result = await aiService.TestConnectionAsync(snapshot, model, cancellationToken);
        return result.Success
            ? Results.Ok(new AiTestResultDto(true, result.Model, result.ResponseText, null))
            : Results.BadRequest(new AiTestResultDto(false, result.Model, result.ResponseText, result.Error));
    }

    private static async Task<IResult> SaveBatchSettingsAsync(BatchSettingsDto request, IConfiguration configuration, IWebHostEnvironment environment, BatchTaskManagementService tasks, CancellationToken cancellationToken)
    {
        if (request.DefaultDelayMs < 1000 || request.DefaultDelayMs > 60000)
            return Results.BadRequest(new OperationResultDto(false, "默认操作间隔范围应为 1000-60000ms"));
        if (request.MaxConcurrent < 1 || request.MaxConcurrent > 10)
            return Results.BadRequest(new OperationResultDto(false, "最大并发任务数范围应为 1-10"));
        if (request.HistoryRetentionLimit < 0 || request.HistoryRetentionLimit > 5000)
            return Results.BadRequest(new OperationResultDto(false, "历史任务保留上限范围应为 0-5000"));

        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var telegram = EnsureObject(root, "Telegram");
        telegram["DefaultDelayMs"] = request.DefaultDelayMs;
        telegram["MaxRetries"] = request.AutoRetry ? Math.Clamp(request.MaxRetries, 1, 5) : 0;
        var batch = EnsureObject(root, "BatchTasks");
        batch["MaxConcurrent"] = request.MaxConcurrent;
        batch["HistoryRetentionLimit"] = request.HistoryRetentionLimit;
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        var deleted = request.HistoryRetentionLimit > 0 ? await tasks.TrimHistoryTasksAsync(request.HistoryRetentionLimit) : 0;
        return Results.Ok(new OperationResultDto(true, deleted > 0 ? $"批量操作配置已保存，并清理 {deleted} 条历史任务" : "批量操作配置已保存"));
    }

    private static async Task<IResult> SaveTimeZoneSettingsAsync(
        TimeZoneSettingsDto request,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        PanelTimeZoneService timeZone,
        ScheduledTaskService scheduledTasks,
        CancellationToken cancellationToken)
    {
        var timeZoneId = (request.TimeZoneId ?? string.Empty).Trim();
        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var system = EnsureObject(root, "System");
        system["TimeZoneId"] = timeZoneId;
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);

        timeZone.ApplyTimeZoneId(timeZoneId);
        var updated = await scheduledTasks.RecalculateNextRunsAsync(DateTime.UtcNow, cancellationToken);
        return Results.Ok(new OperationResultDto(true, updated > 0
            ? $"时区设置已保存，已按新时区重算 {updated} 条计划任务"
            : "时区设置已保存"));
    }

    private static async Task<IResult> SaveSyncSettingsAsync(SyncSettingsDto request, IConfiguration configuration, IWebHostEnvironment environment, CancellationToken cancellationToken)
    {
        if (request.AutoSyncEnabled && (request.IntervalHours < 1 || request.IntervalHours > 24))
            return Results.BadRequest(new OperationResultDto(false, "同步间隔范围应为 1-24 小时"));
        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var sync = EnsureObject(root, "Sync");
        sync["AutoSyncEnabled"] = request.AutoSyncEnabled;
        sync["IntervalHours"] = request.IntervalHours;
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "同步设置已保存"));
    }

    private static async Task<IResult> StartSyncNowAsync(DataSyncService dataSync, CancellationToken cancellationToken)
    {
        var taskId = await dataSync.StartAllActiveAccountsTrackedInBackgroundAsync("vue_settings_manual", cancellationToken);
        return Results.Ok(new OperationResultDto(true, $"已启动后台同步任务 #{taskId}"));
    }

    private static async Task<IResult> SaveBotAutoSyncSettingsAsync(BotAutoSyncSettingsDto request, IConfiguration configuration, IWebHostEnvironment environment, CancellationToken cancellationToken)
    {
        if (request.Enabled && (request.IntervalSeconds < 2 || request.IntervalSeconds > 60))
            return Results.BadRequest(new OperationResultDto(false, "Bot 自动同步轮询间隔范围应为 2-60 秒"));
        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var telegram = EnsureObject(root, "Telegram");
        telegram["BotAutoSyncEnabled"] = request.Enabled;
        telegram["BotAutoSyncIntervalSeconds"] = request.IntervalSeconds;
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "Bot 自动同步设置已保存"));
    }

    private static async Task<IResult> SaveTelegramStatusSettingsAsync(TelegramStatusAutoRefreshSettingsDto request, IConfiguration configuration, IWebHostEnvironment environment, CancellationToken cancellationToken)
    {
        if (request.Enabled && (request.IntervalMinutes < 5 || request.IntervalMinutes > 1440))
            return Results.BadRequest(new OperationResultDto(false, "账号状态自动刷新间隔范围应为 5-1440 分钟"));
        if (request.BatchSize < 1 || request.BatchSize > 20)
            return Results.BadRequest(new OperationResultDto(false, "每轮刷新账号数范围应为 1-20"));
        if (request.MinAgeMinutes < 1 || request.MinAgeMinutes > 1440)
            return Results.BadRequest(new OperationResultDto(false, "最小状态年龄范围应为 1-1440 分钟"));
        if (request.DelayMs < 0 || request.DelayMs > 60000)
            return Results.BadRequest(new OperationResultDto(false, "账号间延迟范围应为 0-60000ms"));

        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var status = EnsureObject(root, "TelegramStatus");
        status["AutoRefreshTransientFailures"] = request.Enabled;
        status["AutoRefreshIntervalMinutes"] = request.IntervalMinutes;
        status["AutoRefreshBatchSize"] = request.BatchSize;
        status["AutoRefreshMinAgeMinutes"] = request.MinAgeMinutes;
        status["AutoRefreshDelayMs"] = request.DelayMs;
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "账号状态自动刷新设置已保存"));
    }

    private static async Task<IResult> SaveLoggingSettingsAsync(LoggingSettingsDto request, IConfiguration configuration, IWebHostEnvironment environment, CancellationToken cancellationToken)
    {
        var level = (request.Level ?? "Information").Trim();
        if (level is not ("Debug" or "Information" or "Warning" or "Error"))
            return Results.BadRequest(new OperationResultDto(false, "日志级别无效"));
        if (request.RetentionDays < 1 || request.RetentionDays > 90)
            return Results.BadRequest(new OperationResultDto(false, "日志保留天数范围应为 1-90"));
        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var serilog = EnsureObject(root, "Serilog");
        serilog["Enabled"] = request.Enabled;
        var minimum = EnsureObject(serilog, "MinimumLevel");
        minimum["Default"] = level;
        serilog["RetainedFileCountLimit"] = request.RetentionDays;
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "日志配置已保存"));
    }

    private static IResult GetBucketBackupSettings(BucketBackupService bucketBackup) =>
        Results.Ok(bucketBackup.GetSettings());

    private static async Task<IResult> SaveBucketBackupSettingsAsync(
        SaveBucketBackupSettingsDto request,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var method = string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) ? "POST" : "PUT";
        var uploadUrl = (request.UploadUrl ?? string.Empty).Trim();
        if (request.Enabled)
        {
            if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Results.BadRequest(new OperationResultDto(false, "启用存储桶备份时必须填写有效 HTTP/HTTPS 上传 URL"));
        }

        var root = await LoadLocalConfigRootAsync(LocalConfigFile.ResolvePath(configuration, environment));
        var backup = EnsureObject(root, "BucketBackup");
        backup["Enabled"] = request.Enabled;
        backup["UploadUrl"] = uploadUrl;
        backup["Method"] = method;
        backup["TimeoutSeconds"] = Math.Clamp(request.TimeoutSeconds <= 0 ? 300 : request.TimeoutSeconds, 30, 1800);
        if (request.ClearAuthorizationHeader)
            backup["AuthorizationHeader"] = string.Empty;
        else if (!string.IsNullOrWhiteSpace(request.AuthorizationHeader))
            backup["AuthorizationHeader"] = request.AuthorizationHeader.Trim();
        await SaveLocalRootAsync(configuration, environment, root, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "存储桶备份配置已保存"));
    }

    private static async Task<IResult> RunBucketBackupAsync(BucketBackupService bucketBackup, CancellationToken cancellationToken)
    {
        var result = await bucketBackup.RunAsync(cancellationToken);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }

    private static async Task<IResult> ClearCacheAsync(ITelegramClientPool telegramClientPool)
    {
        await telegramClientPool.RemoveAllClientsAsync();
        return Results.Ok(new OperationResultDto(true, "缓存已清除"));
    }

    private static async Task<IResult> ChangeAdminPasswordAsync(ChangeAdminPasswordRequestDto request, AdminCredentialStore credentialStore, CancellationToken cancellationToken)
    {
        await credentialStore.ChangePasswordAsync(request.CurrentPassword ?? "", request.NewPassword ?? "", cancellationToken);
        return Results.Ok(new OperationResultDto(true, "密码已修改"));
    }

    internal static async Task<IResult> ChangeAdminUsernameAsync(
        ChangeAdminUsernameRequestDto request,
        HttpContext http,
        AdminCredentialStore credentialStore,
        CancellationToken cancellationToken)
    {
        if (!AdminCredentialStore.TryNormalizeUsername(
                request.NewUsername,
                out var normalizedUsername,
                out var validationError))
        {
            return Results.BadRequest(new OperationResultDto(false, validationError));
        }

        await credentialStore.ChangeUsernameAsync(
            request.CurrentPassword ?? "",
            normalizedUsername,
            cancellationToken);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, credentialStore.Username),
            new(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

        return Results.Ok(new OperationResultDto(true, "后台用户名已修改"));
    }

    private static async Task<IResult> VerifyAdminPasswordAsync(VerifyAdminPasswordRequestDto request, AdminCredentialStore credentialStore, CancellationToken cancellationToken)
    {
        var password = (request.Password ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(password))
            return Results.BadRequest(new OperationResultDto(false, "请输入后台密码"));

        var ok = await credentialStore.ValidateAsync(credentialStore.Username, password, cancellationToken);
        return Results.Ok(new OperationResultDto(ok, ok ? "校验通过" : "后台密码错误"));
    }

    private static async Task<IResult> GetChannelsPageAsync(
        int? page,
        int? pageSize,
        int? accountId,
        int? groupId,
        string? filterType,
        string? membershipRole,
        string? search,
        ChannelManagementService channelManagement,
        CancellationToken cancellationToken)
    {
        var safePage = Math.Max(1, page ?? 1);
        var safePageSize = Math.Clamp(pageSize ?? 20, 1, 500);
        var normalizedGroupId = groupId == -1 ? null : groupId;
        var (items, total) = await channelManagement.QueryChannelsForViewPagedAsync(
            accountId ?? 0,
            normalizedGroupId,
            filterType,
            membershipRole,
            search,
            safePage - 1,
            safePageSize,
            cancellationToken);

        return Results.Ok(new PagedResultDto<ChannelListItemDto>(items.Select(ToDto).ToList(), total, safePage, safePageSize));
    }

    private static async Task<IResult> GetGroupsPageAsync(
        int? page,
        int? pageSize,
        int? accountId,
        int? categoryId,
        string? filterType,
        string? membershipRole,
        string? search,
        GroupManagementService groupManagement,
        CancellationToken cancellationToken)
    {
        var safePage = Math.Max(1, page ?? 1);
        var safePageSize = Math.Clamp(pageSize ?? 20, 1, 500);
        var normalizedCategoryId = categoryId == -1 ? null : categoryId;
        var (items, total) = await groupManagement.QueryGroupsForViewPagedAsync(
            accountId ?? 0,
            normalizedCategoryId,
            filterType,
            membershipRole,
            search,
            safePage - 1,
            safePageSize,
            cancellationToken);

        return Results.Ok(new PagedResultDto<GroupListItemDto>(items.Select(ToDto).ToList(), total, safePage, safePageSize));
    }

    private static async Task<IResult> GetChannelDetailAsync(
        int id,
        ChannelManagementService channelManagement,
        CancellationToken cancellationToken)
    {
        var channel = await channelManagement.GetChannelAsync(id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "频道不存在"));

        var memberships = await channelManagement.GetChannelAccountMembershipsAsync(id, cancellationToken);
        return Results.Ok(ToDetailDto(channel, memberships));
    }

    private static async Task<IResult> GetChannelAdminsAsync(
        int id,
        ChannelManagementService channelManagement,
        IChannelService channelService)
    {
        var channel = await channelManagement.GetChannelAsync(id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "频道不存在"));

        var accountId = await channelManagement.ResolveExecuteAccountIdAsync(channel);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该频道暂无可用执行账号（请先同步频道关联账号，或确保至少有一个账号是管理员）"));

        var admins = await channelService.GetAdminsAsync(accountId.Value, channel.TelegramId);
        return Results.Ok(admins.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetGroupDetailAsync(
        int id,
        GroupManagementService groupManagement,
        CancellationToken cancellationToken)
    {
        var group = await groupManagement.GetGroupAsync(id);
        if (group == null)
            return Results.NotFound(new OperationResultDto(false, "群组不存在"));

        var memberships = await groupManagement.GetGroupAccountMembershipsAsync(id, cancellationToken);
        return Results.Ok(ToDetailDto(group, memberships));
    }

    private static async Task<IResult> GetGroupAdminsAsync(
        int id,
        GroupManagementService groupManagement,
        IGroupService groupService)
    {
        var group = await groupManagement.GetGroupAsync(id);
        if (group == null)
            return Results.NotFound(new OperationResultDto(false, "群组不存在"));

        var accountId = await groupManagement.ResolveExecuteAccountIdAsync(group);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该群组暂无可用执行账号（请先同步群组关联账号，或确保至少有一个账号是管理员）"));

        var admins = await groupService.GetAdminsAsync(accountId.Value, group.TelegramId);
        return Results.Ok(admins.Select(ToDto).ToList());
    }

    private static async Task<IResult> CreateChannelAsync(
        CreateChannelRequestDto request,
        IChannelService channelService,
        AccountManagementService accountManagement,
        AccountRiskService riskService,
        ChannelManagementService channelManagement,
        ChannelGroupManagementService channelGroupManagement,
        ILoggerFactory loggerFactory)
    {
        var title = (request.Title ?? string.Empty).Trim();
        if (request.AccountId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请选择账号"));
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new OperationResultDto(false, "请输入频道名称"));
        if (request.IsPublic && string.IsNullOrWhiteSpace(request.Username))
            return Results.BadRequest(new OperationResultDto(false, "公开频道需要设置用户名"));

        var account = await accountManagement.GetAccountAsync(request.AccountId);
        if (account == null)
            return Results.NotFound(new OperationResultDto(false, "账号不存在"));

        if (request.GroupId is > 0 && await channelGroupManagement.GetGroupAsync(request.GroupId.Value) == null)
            return Results.BadRequest(new OperationResultDto(false, "所选频道分组不存在，请刷新页面后重试"));

        if (!request.IgnoreRiskWarning)
        {
            var risk = riskService.CheckLoginDuration(account);
            if (risk.IsRisky)
                return Results.BadRequest(new OperationResultDto(
                    false,
                    $"{risk.Message}。{risk.DetailedMessage}",
                    AccountRiskConfirmationRequiredCode));
        }

        var about = (request.About ?? string.Empty).Trim();
        var username = NormalizeUsername(request.Username);
        try
        {
            var channelInfo = await channelService.CreateChannelAsync(request.AccountId, title, about, request.IsPublic);
            var now = DateTime.UtcNow;
            var saved = await channelManagement.CreateOrUpdateChannelAsync(new Channel
            {
                TelegramId = channelInfo.TelegramId,
                AccessHash = channelInfo.AccessHash,
                Title = title,
                Username = channelInfo.Username,
                IsBroadcast = channelInfo.IsBroadcast,
                MemberCount = channelInfo.MemberCount,
                About = about,
                CreatorAccountId = request.AccountId,
                GroupId = request.GroupId is > 0 ? request.GroupId : null,
                CreatedAt = now,
                SystemCreatedAtUtc = now,
                SyncedAt = now
            });

            await channelManagement.UpsertAccountChannelAsync(request.AccountId, saved.Id, isCreator: true, isAdmin: true, syncedAtUtc: now);
            var warnings = new List<string>();

            if (request.IsPublic && !string.IsNullOrWhiteSpace(username))
            {
                try
                {
                    var configured = await channelService.SetChannelVisibilityAsync(request.AccountId, channelInfo.TelegramId, true, username);
                    if (!configured)
                    {
                        warnings.Add("公开用户名未设置成功");
                    }
                    else
                    {
                        saved.Username = username;
                        await channelManagement.UpdateChannelAsync(saved);
                    }
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("TelegramPanel.Web.Api.ChatCreation")
                        .LogWarning(ex, "频道 {TelegramId} 已创建，但设置公开用户名失败", channelInfo.TelegramId);
                    warnings.Add(BuildChatOperationError("设置公开用户名", ex));
                }
            }

            if (!request.AllowForwarding)
            {
                try
                {
                    if (!await channelService.SetForwardingAllowedAsync(request.AccountId, channelInfo.TelegramId, allowed: false))
                        warnings.Add("禁止转发未设置成功");
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("TelegramPanel.Web.Api.ChatCreation")
                        .LogWarning(ex, "频道 {TelegramId} 已创建，但设置禁止转发失败", channelInfo.TelegramId);
                    warnings.Add(BuildChatOperationError("设置禁止转发", ex));
                }
            }

            var warning = warnings.Count == 0
                ? null
                : $"频道已创建并保存，但部分附加设置未完成：{string.Join("；", warnings)}。请在频道列表中检查后重试设置。";
            return Results.Ok(ToDto(saved) with { Warning = warning });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TelegramPanel.Web.Api.ChatCreation")
                .LogWarning(ex, "创建频道失败，账号 {AccountId}", request.AccountId);
            return Results.BadRequest(new OperationResultDto(false, BuildChatOperationError("创建频道", ex)));
        }
    }

    private static async Task<IResult> CreateGroupAsync(
        CreateGroupRequestDto request,
        IGroupService groupService,
        AccountManagementService accountManagement,
        AccountRiskService riskService,
        GroupManagementService groupManagement,
        GroupCategoryManagementService categoryManagement,
        ILoggerFactory loggerFactory)
    {
        var title = (request.Title ?? string.Empty).Trim();
        if (request.AccountId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请选择账号"));
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new OperationResultDto(false, "请输入群组名称"));
        if (request.IsPublic && string.IsNullOrWhiteSpace(request.Username))
            return Results.BadRequest(new OperationResultDto(false, "公开群组需要设置用户名"));

        var account = await accountManagement.GetAccountAsync(request.AccountId);
        if (account == null)
            return Results.NotFound(new OperationResultDto(false, "账号不存在"));

        if (request.CategoryId is > 0 && await categoryManagement.GetCategoryAsync(request.CategoryId.Value) == null)
            return Results.BadRequest(new OperationResultDto(false, "所选群组分类不存在，请刷新页面后重试"));

        if (!request.IgnoreRiskWarning)
        {
            var risk = riskService.CheckLoginDuration(account);
            if (risk.IsRisky)
                return Results.BadRequest(new OperationResultDto(
                    false,
                    $"{risk.Message}。{risk.DetailedMessage}",
                    AccountRiskConfirmationRequiredCode));
        }

        try
        {
            var username = NormalizeUsername(request.Username);
            var info = await groupService.CreatePrivateGroupAsync(
                request.AccountId,
                title,
                (request.About ?? string.Empty).Trim());
            var now = DateTime.UtcNow;
            var saved = await groupManagement.CreateOrUpdateGroupAsync(new Group
            {
                TelegramId = info.TelegramId,
                AccessHash = info.AccessHash,
                Title = info.Title,
                Username = info.Username,
                MemberCount = Math.Max(info.MemberCount, 1),
                About = info.About,
                CreatorAccountId = request.AccountId,
                CategoryId = request.CategoryId is > 0 ? request.CategoryId : null,
                CreatedAt = info.CreatedAt ?? now,
                SystemCreatedAtUtc = now,
                SyncedAt = now
            });

            await groupManagement.UpsertAccountGroupAsync(request.AccountId, saved.Id, isCreator: true, isAdmin: true, syncedAtUtc: now);
            string? warning = null;
            if (request.IsPublic && !string.IsNullOrWhiteSpace(username))
            {
                try
                {
                    var configured = await groupService.SetGroupVisibilityAsync(request.AccountId, info.TelegramId, true, username);
                    if (!configured)
                    {
                        warning = "群组已创建并保存，但公开用户名未设置成功。请在群组列表中检查后重试设置。";
                    }
                    else
                    {
                        saved.Username = username;
                        await groupManagement.UpdateGroupAsync(saved);
                    }
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("TelegramPanel.Web.Api.ChatCreation")
                        .LogWarning(ex, "群组 {TelegramId} 已创建，但设置公开用户名失败", info.TelegramId);
                    warning = $"群组已创建并保存，但公开用户名未设置成功：{BuildChatOperationError("设置公开用户名", ex)}。请在群组列表中检查后重试设置。";
                }
            }

            return Results.Ok(ToDto(saved) with { Warning = warning });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TelegramPanel.Web.Api.ChatCreation")
                .LogWarning(ex, "创建群组失败，账号 {AccountId}", request.AccountId);
            return Results.BadRequest(new OperationResultDto(false, BuildChatOperationError("创建群组", ex)));
        }
    }

    private static async Task<IResult> UpdateChannelAsync(
        int id,
        HttpRequest httpRequest,
        IChannelService channelService,
        ChannelManagementService channelManagement,
        ChannelGroupManagementService channelGroupManagement)
    {
        var channel = await channelManagement.GetChannelAsync(id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "频道不存在"));

        var request = await ReadSaveChatRequestAsync(httpRequest);
        var title = (request.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new OperationResultDto(false, "频道名称不能为空"));
        if (request.CategoryId is > 0 && await channelGroupManagement.GetGroupAsync(request.CategoryId.Value) == null)
            return Results.BadRequest(new OperationResultDto(false, "所选频道分组不存在，请刷新页面后重试"));

        var accountId = await channelManagement.ResolveExecuteAccountIdAsync(channel);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该频道暂无可用执行账号"));

        var about = NormalizeNullable(request.About);
        await channelService.UpdateChannelInfoAsync(accountId.Value, channel.TelegramId, title, about);
        await channelService.SetChannelVisibilityAsync(accountId.Value, channel.TelegramId, request.IsPublic, request.IsPublic ? NormalizeUsername(request.Username) : null);
        if (request.ForwardingAllowed.HasValue)
            await channelService.SetForwardingAllowedAsync(accountId.Value, channel.TelegramId, request.ForwardingAllowed.Value);
        if (request.Photo != null && request.Photo.Length > 0)
        {
            await using var stream = request.Photo.OpenReadStream();
            await channelService.SetChannelPhotoAsync(accountId.Value, channel.TelegramId, stream, request.Photo.FileName, httpRequest.HttpContext.RequestAborted);
        }

        channel.Title = title;
        channel.About = about;
        channel.Username = request.IsPublic ? NormalizeUsername(request.Username) : null;
        channel.GroupId = request.CategoryId is > 0 ? request.CategoryId : null;
        await channelManagement.UpdateChannelAsync(channel);
        return Results.Ok(ToDto(channel));
    }

    private static async Task<IResult> UpdateGroupAsync(
        int id,
        HttpRequest httpRequest,
        IGroupService groupService,
        GroupManagementService groupManagement,
        GroupCategoryManagementService categoryManagement)
    {
        var group = await groupManagement.GetGroupAsync(id);
        if (group == null)
            return Results.NotFound(new OperationResultDto(false, "群组不存在"));

        var request = await ReadSaveChatRequestAsync(httpRequest);
        var title = (request.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new OperationResultDto(false, "群组名称不能为空"));
        if (request.CategoryId is > 0 && await categoryManagement.GetCategoryAsync(request.CategoryId.Value) == null)
            return Results.BadRequest(new OperationResultDto(false, "所选群组分类不存在，请刷新页面后重试"));

        var accountId = await groupManagement.ResolveExecuteAccountIdAsync(group);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该群组暂无可用执行账号"));

        var about = NormalizeNullable(request.About);
        await groupService.UpdateGroupInfoAsync(accountId.Value, group.TelegramId, title, about);
        await groupService.SetGroupVisibilityAsync(accountId.Value, group.TelegramId, request.IsPublic, request.IsPublic ? NormalizeUsername(request.Username) : null);
        if (request.Photo != null && request.Photo.Length > 0)
        {
            await using var stream = request.Photo.OpenReadStream();
            await groupService.SetGroupPhotoAsync(accountId.Value, group.TelegramId, stream, request.Photo.FileName, httpRequest.HttpContext.RequestAborted);
        }

        group.Title = title;
        group.About = about;
        group.Username = request.IsPublic ? NormalizeUsername(request.Username) : null;
        group.CategoryId = request.CategoryId is > 0 ? request.CategoryId : null;
        await groupManagement.UpdateGroupAsync(group);
        return Results.Ok(ToDto(group));
    }

    private static async Task<IResult> SyncChannelsAsync(SyncChatsRequestDto request, DataSyncService dataSync, CancellationToken cancellationToken)
    {
        if (request.AccountId is > 0)
            return Results.Ok(ToDto(await dataSync.SyncAccountAsync(request.AccountId.Value, cancellationToken), null));

        var taskId = await dataSync.StartAllActiveAccountsTrackedInBackgroundAsync("vue_channels_sync", cancellationToken);
        return Results.Ok(new SyncResultDto(taskId, 0, 0, 0, 0, Array.Empty<SyncFailureDto>(), "同步任务已提交，请在任务中心查看进度"));
    }

    private static async Task<IResult> SyncGroupsAsync(SyncChatsRequestDto request, DataSyncService dataSync, CancellationToken cancellationToken)
    {
        if (request.AccountId is > 0)
            return Results.Ok(ToDto(await dataSync.SyncAccountAsync(request.AccountId.Value, cancellationToken), null));

        var taskId = await dataSync.StartAllActiveAccountsTrackedInBackgroundAsync("vue_groups_sync", cancellationToken);
        return Results.Ok(new SyncResultDto(taskId, 0, 0, 0, 0, Array.Empty<SyncFailureDto>(), "同步任务已提交，请在任务中心查看进度"));
    }

    private static async Task<IResult> SetChannelGroupAsync(
        int id,
        SetCategoryRequestDto request,
        ChannelManagementService channelManagement,
        ChannelGroupManagementService groupManagement,
        CancellationToken cancellationToken)
    {
        if (request.CategoryId is > 0 && await groupManagement.GetGroupAsync(request.CategoryId.Value) == null)
            return Results.NotFound(new OperationResultDto(false, "频道分组不存在，请刷新页面后重试"));

        await channelManagement.UpdateChannelGroupAssignmentsAsync(
            new[] { id },
            new[] { id },
            request.CategoryId is > 0 ? request.CategoryId : null,
            cancellationToken);
        return Results.Ok(new OperationResultDto(true, "频道分类已更新"));
    }

    private static async Task<IResult> SetGroupCategoryAsync(
        int id,
        SetCategoryRequestDto request,
        GroupManagementService groupManagement,
        GroupCategoryManagementService categoryManagement,
        CancellationToken cancellationToken)
    {
        if (request.CategoryId is > 0 && await categoryManagement.GetCategoryAsync(request.CategoryId.Value) == null)
            return Results.NotFound(new OperationResultDto(false, "群组分类不存在，请刷新页面后重试"));

        await groupManagement.UpdateGroupCategoryAssignmentsAsync(
            new[] { id },
            new[] { id },
            request.CategoryId is > 0 ? request.CategoryId : null,
            cancellationToken);
        return Results.Ok(new OperationResultDto(true, "群组分类已更新"));
    }

    private static async Task<IResult> DeleteChannelAsync(int id, ChannelManagementService channelManagement)
    {
        await channelManagement.DeleteChannelAsync(id);
        return Results.Ok(new OperationResultDto(true, "频道已删除"));
    }

    private static async Task<IResult> DeleteGroupAsync(int id, GroupManagementService groupManagement)
    {
        await groupManagement.DeleteGroupAsync(id);
        return Results.Ok(new OperationResultDto(true, "群组已删除"));
    }

    private static async Task<IResult> BatchSetChannelGroupAsync(
        BatchSetCategoryRequestDto request,
        ChannelManagementService channelManagement,
        ChannelGroupManagementService groupManagement,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择频道"));
        if (request.CategoryId is > 0 && await groupManagement.GetGroupAsync(request.CategoryId.Value) == null)
            return Results.NotFound(new OperationResultDto(false, "频道分组不存在，请刷新页面后重试"));

        await channelManagement.UpdateChannelGroupAssignmentsAsync(ids, ids, request.CategoryId is > 0 ? request.CategoryId : null, cancellationToken);
        return Results.Ok(new OperationResultDto(true, $"分类已更新：{ids.Count} 个频道"));
    }

    private static async Task<IResult> BatchSetGroupCategoryAsync(
        BatchSetCategoryRequestDto request,
        GroupManagementService groupManagement,
        GroupCategoryManagementService categoryManagement,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择群组"));
        if (request.CategoryId is > 0 && await categoryManagement.GetCategoryAsync(request.CategoryId.Value) == null)
            return Results.NotFound(new OperationResultDto(false, "群组分类不存在，请刷新页面后重试"));

        await groupManagement.UpdateGroupCategoryAssignmentsAsync(ids, ids, request.CategoryId is > 0 ? request.CategoryId : null, cancellationToken);
        return Results.Ok(new OperationResultDto(true, $"分类已更新：{ids.Count} 个群组"));
    }

    private static async Task<IResult> BatchDeleteChannelsAsync(BatchIdsRequestDto request, ChannelManagementService channelManagement)
    {
        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择频道"));
        foreach (var id in ids)
            await channelManagement.DeleteChannelAsync(id);
        return Results.Ok(new OperationResultDto(true, $"频道已删除：{ids.Count} 个"));
    }

    private static async Task<IResult> BatchDeleteGroupsAsync(BatchIdsRequestDto request, GroupManagementService groupManagement)
    {
        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择群组"));
        foreach (var id in ids)
            await groupManagement.DeleteGroupAsync(id);
        return Results.Ok(new OperationResultDto(true, $"群组已删除：{ids.Count} 个"));
    }

    private static async Task<IResult> BatchInviteChannelsAsync(
        ChannelUserBatchRequestDto request,
        ChannelManagementService channelManagement,
        AccountManagementService accountManagement,
        BatchTaskManagementService taskManagement)
    {
        var ids = NormalizeIds(request.Ids);
        var usernames = NormalizeUsernames(request.Usernames);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择频道"));
        if (usernames.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请填写用户名"));

        var targets = new List<ChatInviteTargetItem>();
        foreach (var id in ids)
        {
            var channel = await channelManagement.GetChannelAsync(id);
            if (channel != null)
            {
                targets.Add(new ChatInviteTargetItem
                {
                    Id = channel.Id,
                    TelegramId = channel.TelegramId,
                    Title = channel.Title ?? channel.TelegramId.ToString()
                });
            }
        }

        if (targets.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "未找到可邀请的频道"));

        var accountScope = await ResolveInviteExecuteAccountScopeAsync(
            request.AccountId,
            request.AccountCategoryId,
            accountManagement);

        var config = new BatchInviteTaskConfig
        {
            AccountId = accountScope.PrimaryAccountId,
            AccountCategoryId = accountScope.CategoryId,
            AccountScopeName = accountScope.ScopeName,
            ExecuteAccountIds = accountScope.AccountIds,
            DelayMs = Math.Clamp(request.DelayMs ?? 2000, 0, 30000),
            Usernames = usernames.ToList(),
            Targets = targets,
            RequestedAtUtc = DateTime.UtcNow
        };

        var task = await taskManagement.CreateTaskAsync(new BatchTask
        {
            TaskType = BatchTaskTypes.ChannelInviteUsers,
            Total = config.Targets.Count * config.Usernames.Count,
            Completed = 0,
            Failed = 0,
            Config = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })
        });

        return Results.Ok(ToDto(task));
    }

    private static async Task<IResult> BatchSetChannelAdminsAsync(
        ChannelAdminBatchRequestDto request,
        ChannelManagementService channelManagement,
        IChannelService channelService,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.Ids);
        var usernames = NormalizeUsernames(request.Usernames);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择频道"));
        if (usernames.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请填写用户名"));

        var rights = request.Rights > 0 ? (AdminRights)request.Rights : AdminRights.BasicAdmin;
        var title = string.IsNullOrWhiteSpace(request.AdminTitle) ? "Admin" : request.AdminTitle.Trim();
        var delayMs = Math.Clamp(request.DelayMs ?? 1500, 0, 30000);
        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            var channel = await channelManagement.GetChannelAsync(id);
            if (channel == null)
            {
                results.Add(new AccountOperationItemDto(id, null, false, "设置失败", "频道不存在"));
                continue;
            }

            var accountId = request.AccountId is > 0 ? request.AccountId : await channelManagement.ResolveExecuteAccountIdAsync(channel);
            if (accountId is not > 0)
            {
                results.Add(new AccountOperationItemDto(id, channel.Title, false, "设置失败", "该频道暂无可用执行账号"));
                continue;
            }

            var success = 0;
            var failures = new List<string>();
            foreach (var username in usernames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var ok = await channelService.SetAdminAsync(accountId.Value, channel.TelegramId, username, rights, title);
                    if (ok)
                        success++;
                    else
                        failures.Add($"{username}: 失败");
                }
                catch (Exception ex)
                {
                    failures.Add($"{username}: {ex.Message}");
                }

                if (delayMs > 0)
                    await Task.Delay(delayMs, cancellationToken);
            }

            var okAll = failures.Count == 0;
            results.Add(new AccountOperationItemDto(
                id,
                channel.Title,
                okAll,
                $"设置成功 {success}/{usernames.Count}",
                okAll ? null : string.Join("；", failures.Take(10))));
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> BatchKickChannelUsersAsync(
        ChannelKickBatchRequestDto request,
        ChannelManagementService channelManagement,
        IChannelService channelService,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.Ids);
        var (userId, username) = ParseUserTarget(request.Target);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择频道"));
        if (userId == null && string.IsNullOrWhiteSpace(username))
            return Results.BadRequest(new OperationResultDto(false, "请填写用户名或用户 ID"));

        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            var channel = await channelManagement.GetChannelAsync(id);
            if (channel == null)
            {
                results.Add(new AccountOperationItemDto(id, null, false, "踢出失败", "频道不存在"));
                continue;
            }

            var accountId = request.AccountId is > 0 ? request.AccountId : await channelManagement.ResolveExecuteAccountIdAsync(channel);
            if (accountId is not > 0)
            {
                results.Add(new AccountOperationItemDto(id, channel.Title, false, "踢出失败", "该频道暂无可用执行账号"));
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ok = userId.HasValue
                    ? await channelService.KickUserByUserIdAsync(accountId.Value, channel.TelegramId, userId.Value, request.PermanentBan)
                    : await channelService.KickUserAsync(accountId.Value, channel.TelegramId, username!, request.PermanentBan);
                results.Add(new AccountOperationItemDto(id, channel.Title, ok, ok ? "已踢出" : "踢出失败", ok ? null : "操作返回失败"));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(id, channel.Title, false, "踢出失败", ex.Message));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> BatchInviteGroupsAsync(
        ChannelUserBatchRequestDto request,
        GroupManagementService groupManagement,
        AccountManagementService accountManagement,
        BatchTaskManagementService taskManagement)
    {
        var ids = NormalizeIds(request.Ids);
        var usernames = NormalizeUsernames(request.Usernames);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择群组"));
        if (usernames.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请填写用户名"));

        var targets = new List<ChatInviteTargetItem>();
        foreach (var id in ids)
        {
            var group = await groupManagement.GetGroupAsync(id);
            if (group != null)
            {
                targets.Add(new ChatInviteTargetItem
                {
                    Id = group.Id,
                    TelegramId = group.TelegramId,
                    Title = group.Title ?? group.TelegramId.ToString()
                });
            }
        }

        if (targets.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "未找到可邀请的群组"));

        var accountScope = await ResolveInviteExecuteAccountScopeAsync(
            request.AccountId,
            request.AccountCategoryId,
            accountManagement);

        var config = new BatchInviteTaskConfig
        {
            AccountId = accountScope.PrimaryAccountId,
            AccountCategoryId = accountScope.CategoryId,
            AccountScopeName = accountScope.ScopeName,
            ExecuteAccountIds = accountScope.AccountIds,
            DelayMs = Math.Clamp(request.DelayMs ?? 2000, 0, 30000),
            Usernames = usernames.ToList(),
            Targets = targets,
            RequestedAtUtc = DateTime.UtcNow
        };

        var task = await taskManagement.CreateTaskAsync(new BatchTask
        {
            TaskType = BatchTaskTypes.GroupInviteUsers,
            Total = config.Targets.Count * config.Usernames.Count,
            Completed = 0,
            Failed = 0,
            Config = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })
        });

        return Results.Ok(ToDto(task));
    }

    private static async Task<IResult> BatchSetGroupAdminsAsync(
        ChannelAdminBatchRequestDto request,
        GroupManagementService groupManagement,
        IGroupService groupService,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.Ids);
        var usernames = NormalizeUsernames(request.Usernames);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择群组"));
        if (usernames.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请填写用户名"));

        var rights = request.Rights > 0 ? (AdminRights)request.Rights : AdminRights.BasicAdmin;
        var title = string.IsNullOrWhiteSpace(request.AdminTitle) ? "Admin" : request.AdminTitle.Trim();
        var delayMs = Math.Clamp(request.DelayMs ?? 1500, 0, 30000);
        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            var group = await groupManagement.GetGroupAsync(id);
            if (group == null)
            {
                results.Add(new AccountOperationItemDto(id, null, false, "设置失败", "群组不存在"));
                continue;
            }

            var accountId = request.AccountId is > 0 ? request.AccountId : await groupManagement.ResolveExecuteAccountIdAsync(group);
            if (accountId is not > 0)
            {
                results.Add(new AccountOperationItemDto(id, group.Title, false, "设置失败", "该群组暂无可用执行账号"));
                continue;
            }

            var success = 0;
            var failures = new List<string>();
            foreach (var username in usernames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var ok = await groupService.SetAdminAsync(accountId.Value, group.TelegramId, username, rights, title);
                    if (ok)
                        success++;
                    else
                        failures.Add($"{username}: 失败");
                }
                catch (Exception ex)
                {
                    failures.Add($"{username}: {ex.Message}");
                }

                if (delayMs > 0)
                    await Task.Delay(delayMs, cancellationToken);
            }

            var okAll = failures.Count == 0;
            results.Add(new AccountOperationItemDto(
                id,
                group.Title,
                okAll,
                $"设置成功 {success}/{usernames.Count}",
                okAll ? null : string.Join("；", failures.Take(10))));
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> BatchKickGroupUsersAsync(
        ChannelKickBatchRequestDto request,
        GroupManagementService groupManagement,
        IGroupService groupService,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeIds(request.Ids);
        var (userId, username) = ParseUserTarget(request.Target);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择群组"));
        if (userId == null && string.IsNullOrWhiteSpace(username))
            return Results.BadRequest(new OperationResultDto(false, "请填写用户名或用户 ID"));

        var results = new List<AccountOperationItemDto>();
        foreach (var id in ids)
        {
            var group = await groupManagement.GetGroupAsync(id);
            if (group == null)
            {
                results.Add(new AccountOperationItemDto(id, null, false, "踢出失败", "群组不存在"));
                continue;
            }

            var accountId = request.AccountId is > 0 ? request.AccountId : await groupManagement.ResolveExecuteAccountIdAsync(group);
            if (accountId is not > 0)
            {
                results.Add(new AccountOperationItemDto(id, group.Title, false, "踢出失败", "该群组暂无可用执行账号"));
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ok = userId.HasValue
                    ? await groupService.KickUserByUserIdAsync(accountId.Value, group.TelegramId, userId.Value, request.PermanentBan)
                    : await groupService.KickUserAsync(accountId.Value, group.TelegramId, username!, request.PermanentBan);
                results.Add(new AccountOperationItemDto(id, group.Title, ok, ok ? "已踢出" : "踢出失败", ok ? null : "操作返回失败"));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(id, group.Title, false, "踢出失败", ex.Message));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> ExportChannelLinkAsync(int id, ChannelManagementService channelManagement, IChannelService channelService)
    {
        var channel = await channelManagement.GetChannelAsync(id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "频道不存在"));
        var accountId = await channelManagement.ResolveExecuteAccountIdAsync(channel);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该频道暂无可用执行账号"));
        return Results.Ok(new LinkResultDto(await channelService.ExportJoinLinkAsync(accountId.Value, channel.TelegramId)));
    }

    private static async Task<IResult> ExportGroupLinkAsync(int id, GroupManagementService groupManagement, IGroupService groupService)
    {
        var group = await groupManagement.GetGroupAsync(id);
        if (group == null)
            return Results.NotFound(new OperationResultDto(false, "群组不存在"));
        var accountId = await groupManagement.ResolveExecuteAccountIdAsync(group);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该群组暂无可用执行账号"));
        return Results.Ok(new LinkResultDto(await groupService.ExportJoinLinkAsync(accountId.Value, group.TelegramId)));
    }

    private static async Task<IResult> LeaveChannelAsync(int id, ChannelManagementService channelManagement, IChannelService channelService)
    {
        var channel = await channelManagement.GetChannelAsync(id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "频道不存在"));
        var accountId = await channelManagement.ResolveExecuteAccountIdAsync(channel);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该频道暂无可用执行账号"));
        await channelService.LeaveChannelAsync(accountId.Value, channel.TelegramId);
        await channelManagement.RemoveAccountChannelAsync(channel.Id, accountId.Value);
        return Results.Ok(new OperationResultDto(true, "已退出频道"));
    }

    private static async Task<IResult> LeaveGroupAsync(int id, GroupManagementService groupManagement, IGroupService groupService)
    {
        var group = await groupManagement.GetGroupAsync(id);
        if (group == null)
            return Results.NotFound(new OperationResultDto(false, "群组不存在"));
        var accountId = await groupManagement.ResolveExecuteAccountIdAsync(group);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该群组暂无可用执行账号"));
        await groupService.LeaveGroupAsync(accountId.Value, group.TelegramId);
        await groupManagement.RemoveAccountGroupAsync(group.Id, accountId.Value);
        return Results.Ok(new OperationResultDto(true, "已退出群组"));
    }

    private static async Task<IResult> DisbandChannelAsync(int id, ChannelManagementService channelManagement, IChannelService channelService)
    {
        var channel = await channelManagement.GetChannelAsync(id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "频道不存在"));
        var accountId = channel.CreatorAccountId ?? await channelManagement.ResolveExecuteAccountIdAsync(channel);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该频道暂无可用执行账号"));
        await channelService.DisbandChannelAsync(accountId.Value, channel.TelegramId);
        await channelManagement.DeleteChannelAsync(channel.Id);
        return Results.Ok(new OperationResultDto(true, "已解散频道"));
    }

    private static async Task<IResult> DisbandGroupAsync(int id, GroupManagementService groupManagement, IGroupService groupService)
    {
        var group = await groupManagement.GetGroupAsync(id);
        if (group == null)
            return Results.NotFound(new OperationResultDto(false, "群组不存在"));
        var accountId = group.CreatorAccountId ?? await groupManagement.ResolveExecuteAccountIdAsync(group);
        if (accountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该群组暂无可用执行账号"));
        await groupService.DisbandGroupAsync(accountId.Value, group.TelegramId);
        await groupManagement.DeleteGroupAsync(group.Id);
        return Results.Ok(new OperationResultDto(true, "已解散群组"));
    }

    private static async Task<IResult> TransferChannelOwnerAsync(
        int id,
        TransferOwnerRequestDto request,
        ChannelManagementService channelManagement,
        AccountManagementService accountManagement,
        IChannelService channelService)
    {
        var channel = await channelManagement.GetChannelAsync(id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "频道不存在"));

        var target = NormalizeUsername(request.Target);
        if (string.IsNullOrWhiteSpace(target))
            return Results.BadRequest(new OperationResultDto(false, "请填写新所有者用户名"));

        var executorAccountId = channel.CreatorAccountId ?? (request.AccountId is > 0 ? request.AccountId : null);
        if (executorAccountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该频道没有本地创建者记录，请先选择当前创建者账号执行"));

        var executor = await accountManagement.GetAccountAsync(executorAccountId.Value);
        if (executor == null)
            return Results.NotFound(new OperationResultDto(false, "执行账号不存在"));

        var password = ResolveTransferPassword(request.Password, executor);
        if (string.IsNullOrWhiteSpace(password))
            return Results.BadRequest(new OperationResultDto(false, "请输入当前创建者账号的二级密码"));

        var (targetAccount, targetAccountError) = await ResolveTargetAccountAsync(request.TargetAccountId, target, accountManagement);
        if (!string.IsNullOrWhiteSpace(targetAccountError))
            return Results.BadRequest(new OperationResultDto(false, targetAccountError));
        try
        {
            await channelService.TransferOwnershipAsync(executorAccountId.Value, channel.TelegramId, target, password);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }

        var now = DateTime.UtcNow;
        channel.CreatorAccountId = targetAccount?.Id;
        await channelManagement.UpdateChannelAsync(channel);
        await channelManagement.UpsertAccountChannelAsync(executorAccountId.Value, channel.Id, isCreator: false, isAdmin: true, syncedAtUtc: now);
        if (targetAccount != null)
            await channelManagement.UpsertAccountChannelAsync(targetAccount.Id, channel.Id, isCreator: true, isAdmin: true, syncedAtUtc: now);

        var message = targetAccount == null
            ? "已提交所有权转让。目标不是本系统账号或未匹配到账号，请同步频道后刷新创建者。"
            : $"已转让给 {targetAccount.DisplayPhone}";
        return Results.Ok(new OperationResultDto(true, message));
    }

    private static async Task<IResult> TransferGroupOwnerAsync(
        int id,
        TransferOwnerRequestDto request,
        GroupManagementService groupManagement,
        AccountManagementService accountManagement,
        IGroupService groupService)
    {
        var group = await groupManagement.GetGroupAsync(id);
        if (group == null)
            return Results.NotFound(new OperationResultDto(false, "群组不存在"));

        var target = NormalizeUsername(request.Target);
        if (string.IsNullOrWhiteSpace(target))
            return Results.BadRequest(new OperationResultDto(false, "请填写新所有者用户名"));

        var executorAccountId = group.CreatorAccountId ?? (request.AccountId is > 0 ? request.AccountId : null);
        if (executorAccountId is not > 0)
            return Results.BadRequest(new OperationResultDto(false, "该群组没有本地创建者记录，请先选择当前创建者账号执行"));

        var executor = await accountManagement.GetAccountAsync(executorAccountId.Value);
        if (executor == null)
            return Results.NotFound(new OperationResultDto(false, "执行账号不存在"));

        var password = ResolveTransferPassword(request.Password, executor);
        if (string.IsNullOrWhiteSpace(password))
            return Results.BadRequest(new OperationResultDto(false, "请输入当前创建者账号的二级密码"));

        var (targetAccount, targetAccountError) = await ResolveTargetAccountAsync(request.TargetAccountId, target, accountManagement);
        if (!string.IsNullOrWhiteSpace(targetAccountError))
            return Results.BadRequest(new OperationResultDto(false, targetAccountError));
        try
        {
            await groupService.TransferOwnershipAsync(executorAccountId.Value, group.TelegramId, target, password);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }

        var now = DateTime.UtcNow;
        group.CreatorAccountId = targetAccount?.Id;
        await groupManagement.UpdateGroupAsync(group);
        await groupManagement.UpsertAccountGroupAsync(executorAccountId.Value, group.Id, isCreator: false, isAdmin: true, syncedAtUtc: now);
        if (targetAccount != null)
            await groupManagement.UpsertAccountGroupAsync(targetAccount.Id, group.Id, isCreator: true, isAdmin: true, syncedAtUtc: now);

        var message = targetAccount == null
            ? "已提交所有权转让。目标不是本系统账号或未匹配到账号，请同步群组后刷新创建者。"
            : $"已转让给 {targetAccount.DisplayPhone}";
        return Results.Ok(new OperationResultDto(true, message));
    }

    private static async Task<IResult> GetChannelGroupsAsync(ChannelGroupManagementService groupManagement)
    {
        var items = (await groupManagement.GetAllGroupsAsync())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
        return Results.Ok(items);
    }

    private static async Task<IResult> CreateChannelGroupAsync(SaveSimpleCategoryRequestDto request, ChannelGroupManagementService groupManagement)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能为空"));
        if (name.Length > 100)
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能超过 100 个字符"));

        if (await groupManagement.IsNameTakenAsync(name))
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在"));

        try
        {
            var group = await groupManagement.CreateGroupAsync(new ChannelGroup
            {
                Name = name,
                Description = NormalizeNullable(request.Description),
                CreatedAt = DateTime.UtcNow
            });
            return Results.Ok(ToDto(group));
        }
        catch (DbUpdateException)
        {
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在或数据库暂时不可写，请稍后重试"));
        }
    }

    private static async Task<IResult> UpdateChannelGroupAsync(int id, SaveSimpleCategoryRequestDto request, ChannelGroupManagementService groupManagement)
    {
        var group = await groupManagement.GetGroupAsync(id);
        if (group == null)
            return Results.NotFound(new OperationResultDto(false, "分类不存在"));
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能为空"));
        if (name.Length > 100)
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能超过 100 个字符"));

        if (await groupManagement.IsNameTakenAsync(name, id))
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在"));

        group.Name = name;
        group.Description = NormalizeNullable(request.Description);
        try
        {
            await groupManagement.UpdateGroupAsync(group);
            return Results.Ok(ToDto(group));
        }
        catch (DbUpdateException)
        {
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在或数据库暂时不可写，请稍后重试"));
        }
    }

    private static async Task<IResult> DeleteChannelGroupAsync(int id, ChannelGroupManagementService groupManagement)
    {
        await groupManagement.DeleteGroupAsync(id);
        return Results.Ok(new OperationResultDto(true, "分类已删除"));
    }

    private static async Task<IResult> SaveChannelGroupAssignmentsAsync(
        int id,
        SaveResourceAssignmentsRequestDto request,
        ChannelManagementService channelManagement,
        ChannelGroupManagementService groupManagement,
        CancellationToken cancellationToken)
    {
        var scope = NormalizeIds(request.ScopeIds);
        if (await groupManagement.GetGroupAsync(id) == null)
            return Results.NotFound(new OperationResultDto(false, "分类不存在，请刷新页面后重试"));
        if (scope.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "当前没有可保存的频道"));

        var selected = NormalizeIds(request.SelectedIds).ToHashSet();
        try
        {
            await channelManagement.UpdateChannelGroupAssignmentsAsync(scope, selected, id, cancellationToken);
            return Results.Ok(new OperationResultDto(true, "分类绑定已保存"));
        }
        catch (DbUpdateException)
        {
            return Results.BadRequest(new OperationResultDto(false, "分类绑定保存失败：数据库暂时忙，请稍后重试"));
        }
    }

    private static async Task<IResult> GetGroupCategoriesAsync(GroupCategoryManagementService categoryManagement)
    {
        var items = (await categoryManagement.GetAllCategoriesAsync())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
        return Results.Ok(items);
    }

    private static async Task<IResult> CreateGroupCategoryAsync(SaveSimpleCategoryRequestDto request, GroupCategoryManagementService categoryManagement)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能为空"));
        if (name.Length > 100)
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能超过 100 个字符"));

        if (await categoryManagement.IsNameTakenAsync(name))
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在"));

        try
        {
            var category = await categoryManagement.CreateCategoryAsync(new GroupCategory
            {
                Name = name,
                Description = NormalizeNullable(request.Description),
                CreatedAt = DateTime.UtcNow
            });
            return Results.Ok(ToDto(category));
        }
        catch (DbUpdateException)
        {
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在或数据库暂时不可写，请稍后重试"));
        }
    }

    private static async Task<IResult> UpdateGroupCategoryAsync(int id, SaveSimpleCategoryRequestDto request, GroupCategoryManagementService categoryManagement)
    {
        var category = await categoryManagement.GetCategoryAsync(id);
        if (category == null)
            return Results.NotFound(new OperationResultDto(false, "分类不存在"));
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能为空"));
        if (name.Length > 100)
            return Results.BadRequest(new OperationResultDto(false, "分类名称不能超过 100 个字符"));

        if (await categoryManagement.IsNameTakenAsync(name, id))
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在"));

        category.Name = name;
        category.Description = NormalizeNullable(request.Description);
        try
        {
            await categoryManagement.UpdateCategoryAsync(category);
            return Results.Ok(ToDto(category));
        }
        catch (DbUpdateException)
        {
            return Results.BadRequest(new OperationResultDto(false, "分类名称已存在或数据库暂时不可写，请稍后重试"));
        }
    }

    private static async Task<IResult> DeleteGroupCategoryAsync(int id, GroupCategoryManagementService categoryManagement)
    {
        await categoryManagement.DeleteCategoryAsync(id);
        return Results.Ok(new OperationResultDto(true, "分类已删除"));
    }

    private static async Task<IResult> SaveGroupCategoryAssignmentsAsync(
        int id,
        SaveResourceAssignmentsRequestDto request,
        GroupManagementService groupManagement,
        GroupCategoryManagementService categoryManagement,
        CancellationToken cancellationToken)
    {
        var scope = NormalizeIds(request.ScopeIds);
        if (await categoryManagement.GetCategoryAsync(id) == null)
            return Results.NotFound(new OperationResultDto(false, "分类不存在，请刷新页面后重试"));
        if (scope.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "当前没有可保存的群组"));

        var selected = NormalizeIds(request.SelectedIds).ToHashSet();
        try
        {
            await groupManagement.UpdateGroupCategoryAssignmentsAsync(scope, selected, id, cancellationToken);
            return Results.Ok(new OperationResultDto(true, "分类绑定已保存"));
        }
        catch (DbUpdateException)
        {
            return Results.BadRequest(new OperationResultDto(false, "分类绑定保存失败：数据库暂时忙，请稍后重试"));
        }
    }

    private static async Task<IResult> GetBotsAsync(BotManagementService botManagement)
    {
        var items = (await botManagement.GetAllBotsAsync())
            .OrderBy(x => x.Id)
            .Select(ToBotManagementDto)
            .ToList();
        return Results.Ok(items);
    }

    private static async Task<IResult> CreateBotAsync(SaveBotRequestDto request, BotManagementService botManagement, TelegramBotApiClient botApi, IConfiguration configuration)
    {
        var bot = await botManagement.CreateBotAsync(request.Name ?? string.Empty, request.Token ?? string.Empty, request.Username);
        await TryRegisterWebhookForBotAsync(bot.Token, botApi, configuration);
        return Results.Ok(ToBotManagementDto(bot));
    }

    private static async Task<IResult> UpdateBotAsync(int id, SaveBotRequestDto request, BotManagementService botManagement, TelegramBotApiClient botApi, IConfiguration configuration)
    {
        await botManagement.UpdateBotProfileAsync(id, request.Name ?? string.Empty, request.Username, request.Token);
        if (!string.IsNullOrWhiteSpace(request.Token))
            await TryRegisterWebhookForBotAsync(request.Token, botApi, configuration);

        var bot = await botManagement.GetBotAsync(id);
        return bot == null ? Results.NotFound(new OperationResultDto(false, "机器人不存在")) : Results.Ok(ToBotManagementDto(bot));
    }

    private static async Task<IResult> SetBotActiveAsync(int id, SetActiveRequestDto request, BotManagementService botManagement, TelegramBotApiClient botApi, IConfiguration configuration)
    {
        await botManagement.SetBotActiveStatusAsync(id, request.IsActive);
        var bot = await botManagement.GetBotAsync(id);
        if (bot != null)
        {
            if (request.IsActive)
                await TryRegisterWebhookForBotAsync(bot.Token, botApi, configuration);
            else
                await TryDeleteWebhookForBotAsync(bot.Token, botApi, configuration);
        }
        return Results.Ok(new OperationResultDto(true, request.IsActive ? "Bot 已启用" : "Bot 已停用"));
    }

    private static async Task<IResult> DeleteBotAsync(int id, BotManagementService botManagement)
    {
        await botManagement.DeleteBotAsync(id);
        return Results.Ok(new OperationResultDto(true, "机器人已删除"));
    }

    private static async Task<IResult> GetBotChannelCategoriesAsync(BotManagementService botManagement)
    {
        var items = (await botManagement.GetCategoriesAsync())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
        return Results.Ok(items);
    }

    private static async Task<IResult> CreateBotChannelCategoryAsync(SaveSimpleCategoryRequestDto request, BotManagementService botManagement)
    {
        var category = await botManagement.CreateCategoryAsync(request.Name ?? string.Empty, request.Description);
        return Results.Ok(ToDto(category));
    }

    private static async Task<IResult> UpdateBotChannelCategoryAsync(int id, SaveSimpleCategoryRequestDto request, BotManagementService botManagement)
    {
        var category = await botManagement.UpdateCategoryAsync(id, request.Name ?? string.Empty, request.Description);
        return Results.Ok(ToDto(category));
    }

    private static async Task<IResult> DeleteBotChannelCategoryAsync(int id, BotManagementService botManagement)
    {
        await botManagement.DeleteCategoryAsync(id);
        return Results.Ok(new OperationResultDto(true, "分类已删除"));
    }

    private static async Task<IResult> GetBotChannelsPageAsync(
        int? page,
        int? pageSize,
        int? botId,
        int? categoryId,
        int? status,
        string? search,
        BotManagementService botManagement,
        CancellationToken cancellationToken)
    {
        var safePage = Math.Max(1, page ?? 1);
        var safePageSize = Math.Clamp(pageSize ?? 20, 1, 500);
        var (items, total) = await botManagement.QueryChannelsPagedAsync(
            botId ?? 0,
            categoryId,
            search,
            status ?? 0,
            safePage - 1,
            safePageSize,
            cancellationToken);

        var dtos = new List<BotChannelListItemDto>();
        foreach (var item in items)
        {
            var boundBots = await botManagement.GetChannelBoundBotsAsync(item.TelegramId);
            dtos.Add(ToBotChannelListDto(item, boundBots));
        }

        return Results.Ok(new PagedResultDto<BotChannelListItemDto>(dtos, total, safePage, safePageSize));
    }

    private static async Task<IResult> GetBotChannelDetailAsync(
        int id,
        int botId,
        BotManagementService botManagement,
        BotTelegramService botTelegram,
        CancellationToken cancellationToken)
    {
        if (botId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));

        var channel = (await botManagement.GetChatsAsync(botId)).FirstOrDefault(x => x.Id == id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "Bot 频道不存在"));

        BotTelegramService.BotChatInfo? chatInfo = null;
        try
        {
            chatInfo = await botTelegram.GetChatInfoAsync(botId, channel.TelegramId, cancellationToken);
        }
        catch
        {
            // 保留本地记录，避免详情弹窗因临时 Bot API 拉取失败而完全打不开。
        }

        var boundBots = await botManagement.GetChannelBoundBotsAsync(channel.TelegramId);
        return Results.Ok(new BotChannelDetailDto(
            ToBotChannelListDto(channel, boundBots),
            chatInfo == null
                ? null
                : new BotChannelRemoteInfoDto(
                    chatInfo.TelegramId,
                    chatInfo.Type,
                    chatInfo.Title,
                    chatInfo.Username,
                    chatInfo.Description,
                    chatInfo.MemberCount)));
    }

    private static async Task<IResult> GetBotChannelAdminsAsync(
        int id,
        int botId,
        BotManagementService botManagement,
        BotTelegramService botTelegram,
        CancellationToken cancellationToken)
    {
        if (botId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));

        var channel = (await botManagement.GetChatsAsync(botId)).FirstOrDefault(x => x.Id == id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "Bot 频道不存在"));

        var admins = await botTelegram.GetChatAdminsAsync(botId, channel.TelegramId, cancellationToken);
        return Results.Ok(admins.Select(ToDto).ToList());
    }

    private static async Task<IResult> UpdateBotChannelAsync(
        int id,
        int botId,
        HttpRequest httpRequest,
        BotManagementService botManagement,
        BotTelegramService botTelegram,
        CancellationToken cancellationToken)
    {
        if (botId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));
        if (!httpRequest.HasFormContentType)
            return Results.BadRequest(new OperationResultDto(false, "请使用 multipart/form-data 保存频道信息"));

        var channel = (await botManagement.GetChatsAsync(botId)).FirstOrDefault(x => x.Id == id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "Bot 频道不存在"));

        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var title = form["title"].ToString().Trim();
        var about = NormalizeNullable(form["about"].ToString());
        var editAvatar = ParseBool(form["editAvatar"]);
        if (string.IsNullOrWhiteSpace(title))
            return Results.BadRequest(new OperationResultDto(false, "频道标题不能为空"));

        await botTelegram.UpdateChannelInfoAsync(botId, channel.TelegramId, title, about, cancellationToken);

        if (editAvatar)
        {
            var file = form.Files.GetFile("avatar");
            if (file == null)
                return Results.BadRequest(new OperationResultDto(false, "请先选择头像图片"));
            if (file.Length <= 0)
                return Results.BadRequest(new OperationResultDto(false, "头像图片为空"));

            await using var stream = file.OpenReadStream();
            await botTelegram.SetChannelPhotoAsync(botId, channel.TelegramId, stream, file.FileName, cancellationToken);
        }

        BotTelegramService.BotChatInfo? chatInfo = null;
        try
        {
            chatInfo = await botTelegram.GetChatInfoAsync(botId, channel.TelegramId, cancellationToken);
        }
        catch
        {
            // 频道信息已保存，拉取新信息失败时使用本地旧字段回写。
        }

        var updated = await botManagement.UpsertChannelAsync(botId, new BotChannel
        {
            TelegramId = channel.TelegramId,
            Title = title,
            Username = chatInfo?.Username ?? channel.Username,
            IsBroadcast = channel.IsBroadcast,
            MemberCount = chatInfo?.MemberCount ?? channel.MemberCount,
            About = about,
            AccessHash = channel.AccessHash,
            CreatedAt = channel.CreatedAt
        });

        var boundBots = await botManagement.GetChannelBoundBotsAsync(updated.TelegramId);
        return Results.Ok(ToBotChannelListDto(updated, boundBots));
    }

    private static async Task<IResult> SetBotChannelCategoryAsync(int id, SetCategoryRequestDto request, BotManagementService botManagement)
    {
        await botManagement.SetChannelCategoryAsync(id, request.CategoryId);
        return Results.Ok(new OperationResultDto(true, "Bot 频道分类已更新"));
    }

    private static async Task<IResult> BatchSetBotChannelCategoryAsync(BatchSetCategoryRequestDto request, BotManagementService botManagement)
    {
        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择 Bot 频道"));
        foreach (var id in ids)
            await botManagement.SetChannelCategoryAsync(id, request.CategoryId);
        return Results.Ok(new OperationResultDto(true, $"分类已更新：{ids.Count} 个 Bot 频道"));
    }

    private static async Task<IResult> BatchDeleteBotChannelsAsync(BatchBotChannelDeleteRequestDto request, BotManagementService botManagement)
    {
        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择 Bot 频道"));

        var deleted = 0;
        var removedTotal = 0;
        var selectedBotIds = NormalizeIds(request.BotIds);
        var chats = (await botManagement.GetChatsAsync(0)).ToList();
        foreach (var id in ids)
        {
            var channel = chats.FirstOrDefault(x => x.Id == id);
            if (channel == null)
                continue;

            if (selectedBotIds.Count > 0)
            {
                removedTotal += await botManagement.DeleteChannelBindingsByBotIdsAsync(channel.TelegramId, selectedBotIds);
            }
            else
            {
                var bots = await botManagement.GetChannelBoundBotsAsync(channel.TelegramId);
                foreach (var bot in bots)
                {
                    await botManagement.DeleteChannelByTelegramIdAsync(bot.Id, channel.TelegramId);
                    removedTotal++;
                }
            }
            deleted++;
        }
        return Results.Ok(new OperationResultDto(true, $"Bot 频道已删除：处理 {deleted} 个，删除绑定 {removedTotal} 条"));
    }

    private static async Task<IResult> SyncBotChannelsAsync(SyncBotChannelsRequestDto request, BotTelegramService botTelegram, CancellationToken cancellationToken)
    {
        if (request.BotId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));
        var result = await botTelegram.SyncBotChannelsAsync(request.BotId, cancellationToken);
        return Results.Ok(new OperationResultDto(true, $"同步完成：更新 {result.AppliedUpdates}，移除 {result.RemovedStale}"));
    }

    private static async Task<IResult> ExportBotChannelLinkAsync(int id, int botId, BotManagementService botManagement, BotTelegramService botTelegram, CancellationToken cancellationToken)
    {
        if (botId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));
        var channel = (await botManagement.GetChatsAsync(botId)).FirstOrDefault(x => x.Id == id);
        if (channel == null)
            return Results.NotFound(new OperationResultDto(false, "Bot 频道不存在"));
        return Results.Ok(new LinkResultDto(await botTelegram.ExportInviteLinkAsync(botId, channel.TelegramId, cancellationToken)));
    }

    private static async Task<IResult> CheckBotChannelStatusAsync(
        BotChannelIdsRequestDto request,
        BotManagementService botManagement,
        BotTelegramService botTelegram,
        CancellationToken cancellationToken)
    {
        if (request.BotId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));
        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择 Bot 频道"));

        var channels = (await botManagement.GetChatsAsync(request.BotId))
            .Where(x => ids.Contains(x.Id))
            .ToList();
        var result = await botTelegram.CheckChannelsStatusAsync(request.BotId, channels.Select(x => x.TelegramId).ToList(), cancellationToken);
        return Results.Ok(new BotChannelStatusResultDto(
            result.SuccessCount,
            result.FailedCount,
            result.TotalCount,
            result.Failures.Select(x => new BotChannelStatusFailureDto(x.Key, x.Value)).ToList()));
    }

    private static async Task<IResult> BanBotChannelMembersAsync(
        BotChannelBanRequestDto request,
        BotManagementService botManagement,
        BotTelegramService botTelegram,
        AccountManagementService accountManagement,
        IChannelService channelService,
        CancellationToken cancellationToken)
    {
        if (request.BotId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));
        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择 Bot 频道"));
        var (userId, username) = ParseUserTarget(request.Target);
        if (userId is not > 0 && string.IsNullOrWhiteSpace(username))
            return Results.BadRequest(new OperationResultDto(false, "请填写用户 ID 或用户名"));

        var channels = (await botManagement.GetChatsAsync(request.BotId))
            .Where(x => ids.Contains(x.Id))
            .GroupBy(x => x.TelegramId)
            .Select(x => x.First())
            .ToList();
        if (channels.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "未找到可操作的 Bot 频道"));

        if (!request.UseAccountExecution)
        {
            if (userId is not > 0)
                return Results.BadRequest(new OperationResultDto(false, "机器人执行需要填写数字用户 ID"));

            var telegramIds = channels.Select(x => x.TelegramId).Distinct().ToList();
            var result = await botTelegram.BanChatMemberAsync(request.BotId, telegramIds, userId.Value, request.PermanentBan, cancellationToken);
            var items = channels.Select(channel =>
            {
                var ok = !result.Failures.TryGetValue(channel.TelegramId, out var error);
                return new AccountOperationItemDto(channel.Id, channel.Title, ok, ok ? "已处理" : "处理失败", error);
            }).ToList();
            return Results.Ok(new AccountBatchOperationResultDto(result.SuccessCount, result.TotalCount - result.SuccessCount, items));
        }

        var accountsByUserId = (await accountManagement.GetActiveAccountsAsync())
            .Where(x => x.UserId > 0 && x.Category?.ExcludeFromOperations != true)
            .GroupBy(x => x.UserId)
            .ToDictionary(x => x.Key, x => x.First());
        var selectedAccount = request.SelectedAccountId > 0
            ? await accountManagement.GetAccountAsync(request.SelectedAccountId)
            : null;
        var results = new List<AccountOperationItemDto>();

        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (executorId, reason) = await ResolveBotChannelBanExecutorAsync(
                request.BotId,
                channel.TelegramId,
                request.SelectedAccountId,
                selectedAccount,
                accountsByUserId,
                botTelegram,
                cancellationToken);

            if (executorId is not > 0)
            {
                results.Add(new AccountOperationItemDto(channel.Id, channel.Title, false, "踢出失败", reason ?? "无可用执行账号"));
                continue;
            }

            try
            {
                var ok = userId.HasValue
                    ? await channelService.KickUserByUserIdAsync(executorId.Value, channel.TelegramId, userId.Value, request.PermanentBan)
                    : await channelService.KickUserAsync(executorId.Value, channel.TelegramId, username!, request.PermanentBan);
                results.Add(new AccountOperationItemDto(channel.Id, channel.Title, ok, ok ? "已处理" : "处理失败", ok ? null : "操作返回失败"));
            }
            catch (Exception ex)
            {
                results.Add(new AccountOperationItemDto(channel.Id, channel.Title, false, "踢出失败", ex.Message));
            }
        }

        return Results.Ok(new AccountBatchOperationResultDto(results.Count(x => x.Success), results.Count(x => !x.Success), results));
    }

    private static async Task<IResult> InviteBotChannelMembersAsync(
        BotChannelInviteRequestDto request,
        BotManagementService botManagement,
        AccountManagementService accountManagement,
        BatchTaskManagementService taskManagement)
    {
        if (request.BotId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));
        var ids = NormalizeIds(request.Ids);
        var usernames = NormalizeUsernames(request.Usernames);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择 Bot 频道"));
        if (usernames.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请填写用户名"));

        var channels = (await botManagement.GetChatsAsync(request.BotId))
            .Where(x => ids.Contains(x.Id))
            .GroupBy(x => x.TelegramId)
            .Select(x => x.First())
            .ToList();
        if (channels.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "未找到可操作的 Bot 频道"));

        var accountScope = await ResolveInviteExecuteAccountScopeAsync(
            request.SelectedAccountId,
            request.AccountCategoryId,
            accountManagement);

        var config = new BatchInviteTaskConfig
        {
            BotId = request.BotId,
            SelectedAccountId = accountScope.PrimaryAccountId,
            AccountId = accountScope.PrimaryAccountId,
            AccountCategoryId = accountScope.CategoryId,
            AccountScopeName = accountScope.ScopeName,
            ExecuteAccountIds = accountScope.AccountIds,
            DelayMs = Math.Clamp(request.DelayMs ?? 2000, 0, 30000),
            Usernames = usernames.ToList(),
            Targets = channels
                .Select(x => new ChatInviteTargetItem
                {
                    Id = x.Id,
                    TelegramId = x.TelegramId,
                    Title = x.Title ?? x.TelegramId.ToString()
                })
                .ToList(),
            RequestedAtUtc = DateTime.UtcNow
        };

        var task = await taskManagement.CreateTaskAsync(new BatchTask
        {
            TaskType = BatchTaskTypes.BotChannelInviteUsers,
            Total = config.Targets.Count * config.Usernames.Count,
            Completed = 0,
            Failed = 0,
            Config = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })
        });

        return Results.Ok(ToDto(task));
    }

    private static async Task<InviteExecuteAccountScope> ResolveInviteExecuteAccountScopeAsync(
        int? accountId,
        int? accountCategoryId,
        AccountManagementService accountManagement)
    {
        if (accountId is > 0)
        {
            var account = await accountManagement.GetAccountAsync(accountId.Value);
            return account is { IsActive: true } && account.Category?.ExcludeFromOperations != true
                ? new InviteExecuteAccountScope(account.Id, account.CategoryId, AccountScopeLabel(account), new List<int> { account.Id })
                : new InviteExecuteAccountScope(accountId.Value, null, $"账号 {accountId.Value}", new List<int>());
        }

        if (accountCategoryId is > 0)
        {
            var accounts = (await accountManagement.GetAccountsByCategoryAsync(accountCategoryId.Value))
                .Where(x => x.IsActive && x.Category?.ExcludeFromOperations != true)
                .OrderBy(x => x.Id)
                .ToList();
            var scopeName = accounts.FirstOrDefault()?.Category?.Name;
            if (string.IsNullOrWhiteSpace(scopeName))
                scopeName = $"账号分组 #{accountCategoryId.Value}";

            return new InviteExecuteAccountScope(
                0,
                accountCategoryId.Value,
                scopeName,
                accounts.Select(x => x.Id).ToList());
        }

        return new InviteExecuteAccountScope(0, null, "自动选择", new List<int>());
    }

    private static string AccountScopeLabel(Account account)
    {
        var phone = account.DisplayPhone;
        if (string.IsNullOrWhiteSpace(phone))
            phone = account.Phone;
        return string.IsNullOrWhiteSpace(phone) ? $"账号 {account.Id}" : phone;
    }

    private static async Task<IResult> GetChannelInvitePresetsAsync(
        ChannelInvitePresetsService presets,
        CancellationToken cancellationToken)
    {
        var items = await presets.GetPresetsAsync(cancellationToken);
        return Results.Ok(items
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new TextPresetDto(x.Name, x.Usernames))
            .ToList());
    }

    private static async Task<IResult> SaveChannelInvitePresetAsync(
        SaveTextPresetRequestDto request,
        ChannelInvitePresetsService presets,
        CancellationToken cancellationToken)
    {
        var name = NormalizePresetName(request.Name);
        var usernames = NormalizeUsernames(request.Values);
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new OperationResultDto(false, "请输入预设名称"));
        if (usernames.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请至少输入一个用户名"));

        await presets.SavePresetAsync(name, usernames.ToList(), cancellationToken);
        return Results.Ok(new OperationResultDto(true, "已保存预设"));
    }

    private static async Task<IResult> DeleteChannelInvitePresetAsync(
        string name,
        ChannelInvitePresetsService presets,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePresetName(Uri.UnescapeDataString(name));
        if (string.IsNullOrWhiteSpace(normalized))
            return Results.BadRequest(new OperationResultDto(false, "请选择预设"));

        await presets.DeletePresetAsync(normalized, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "已删除预设"));
    }

    private static async Task<IResult> GetChannelAdminPresetsAsync(
        ChannelAdminPresetsService presets,
        CancellationToken cancellationToken)
    {
        var items = await presets.GetPresetsAsync(cancellationToken);
        return Results.Ok(items
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new TextPresetDto(x.Name, x.Usernames))
            .ToList());
    }

    private static async Task<IResult> SaveChannelAdminPresetAsync(
        SaveTextPresetRequestDto request,
        ChannelAdminPresetsService presets,
        CancellationToken cancellationToken)
    {
        var name = NormalizePresetName(request.Name);
        var usernames = NormalizeUsernames(request.Values);
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new OperationResultDto(false, "请输入预设名称"));
        if (usernames.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请至少输入一个用户名"));

        await presets.SavePresetAsync(name, usernames.ToList(), cancellationToken);
        return Results.Ok(new OperationResultDto(true, "已保存预设"));
    }

    private static async Task<IResult> DeleteChannelAdminPresetAsync(
        string name,
        ChannelAdminPresetsService presets,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePresetName(Uri.UnescapeDataString(name));
        if (string.IsNullOrWhiteSpace(normalized))
            return Results.BadRequest(new OperationResultDto(false, "请选择预设"));

        await presets.DeletePresetAsync(normalized, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "已删除预设"));
    }

    private static async Task<IResult> GetBotAdminPresetsAsync(
        BotAdminPresetsService presets,
        CancellationToken cancellationToken)
    {
        var items = await presets.GetPresetsAsync(cancellationToken);
        return Results.Ok(items
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new NumberPresetDto(x.Name, x.UserIds))
            .ToList());
    }

    private static async Task<IResult> SaveBotAdminPresetAsync(
        SaveNumberPresetRequestDto request,
        BotAdminPresetsService presets,
        CancellationToken cancellationToken)
    {
        var name = NormalizePresetName(request.Name);
        var userIds = (request.Values ?? Array.Empty<long>()).Where(x => x > 0).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new OperationResultDto(false, "请输入预设名称"));
        if (userIds.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请输入用户 ID"));

        await presets.SavePresetAsync(name, userIds, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "已保存预设"));
    }

    private static async Task<IResult> DeleteBotAdminPresetAsync(
        string name,
        BotAdminPresetsService presets,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePresetName(Uri.UnescapeDataString(name));
        if (string.IsNullOrWhiteSpace(normalized))
            return Results.BadRequest(new OperationResultDto(false, "请选择预设"));

        await presets.DeletePresetAsync(normalized, cancellationToken);
        return Results.Ok(new OperationResultDto(true, "已删除预设"));
    }

    private static async Task<IResult> GetChannelAdminDefaultsAsync(
        ChannelAdminDefaultsService defaults,
        CancellationToken cancellationToken)
    {
        var item = await defaults.GetAsync(cancellationToken);
        return Results.Ok(new ChannelAdminDefaultsDto((int)(item?.Rights ?? AdminRights.BasicAdmin)));
    }

    private static async Task<IResult> SaveChannelAdminDefaultsAsync(
        ChannelAdminDefaultsDto request,
        ChannelAdminDefaultsService defaults,
        CancellationToken cancellationToken)
    {
        var rights = request.Rights > 0 ? (AdminRights)request.Rights : AdminRights.BasicAdmin;
        await defaults.SaveAsync(new ChannelAdminDefaults(rights), cancellationToken);
        return Results.Ok(new OperationResultDto(true, "已保存默认权限"));
    }

    private static async Task<IResult> GetBotChannelAdminDefaultsAsync(
        BotChannelAdminDefaultsService defaults,
        CancellationToken cancellationToken)
    {
        var item = await defaults.GetAsync(cancellationToken);
        var rights = item?.Rights ?? new BotTelegramService.BotAdminRights(
            ManageChat: true,
            ChangeInfo: true,
            PostMessages: false,
            EditMessages: false,
            DeleteMessages: false,
            InviteUsers: true,
            RestrictMembers: false,
            PinMessages: false,
            PromoteMembers: false);
        return Results.Ok(new BotSetAdminsRightsPayload
        {
            ManageChat = rights.ManageChat,
            ChangeInfo = rights.ChangeInfo,
            PostMessages = rights.PostMessages,
            EditMessages = rights.EditMessages,
            DeleteMessages = rights.DeleteMessages,
            InviteUsers = rights.InviteUsers,
            RestrictMembers = rights.RestrictMembers,
            PinMessages = rights.PinMessages,
            PromoteMembers = rights.PromoteMembers
        });
    }

    private static async Task<IResult> SaveBotChannelAdminDefaultsAsync(
        BotSetAdminsRightsPayload request,
        BotChannelAdminDefaultsService defaults,
        CancellationToken cancellationToken)
    {
        var rights = new BotTelegramService.BotAdminRights(
            ManageChat: request.ManageChat,
            ChangeInfo: request.ChangeInfo,
            PostMessages: request.PostMessages,
            EditMessages: request.EditMessages,
            DeleteMessages: request.DeleteMessages,
            InviteUsers: request.InviteUsers,
            RestrictMembers: request.RestrictMembers,
            PinMessages: request.PinMessages,
            PromoteMembers: request.PromoteMembers);
        await defaults.SaveAsync(new BotChannelAdminDefaults(rights), cancellationToken);
        return Results.Ok(new OperationResultDto(true, "已保存默认权限"));
    }

    private static async Task<IResult> CreateBotAdminsByAccountTaskAsync(
        BotAdminsByAccountTaskRequestDto request,
        BotManagementService botManagement,
        BatchTaskManagementService taskManagement)
    {
        if (request.BotId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));
        var ids = NormalizeIds(request.Ids);
        var usernames = NormalizeUsernames(request.Usernames);
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择 Bot 频道"));
        if (usernames.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请填写用户名"));

        var channels = (await botManagement.GetChatsAsync(request.BotId))
            .Where(x => ids.Contains(x.Id))
            .Select(x => new BotTaskChannelItem
            {
                TelegramId = x.TelegramId,
                Title = x.Title ?? x.TelegramId.ToString()
            })
            .ToList();

        var config = new BotChannelSetAdminsByAccountTaskConfig
        {
            BotId = request.BotId,
            SelectedAccountId = request.SelectedAccountId,
            AdminTitle = string.IsNullOrWhiteSpace(request.AdminTitle) ? "Admin" : request.AdminTitle.Trim(),
            DelayMs = Math.Clamp(request.DelayMs ?? 1500, 0, 30000),
            Rights = request.Rights > 0 ? request.Rights : (int)AdminRights.BasicAdmin,
            Usernames = usernames.ToList(),
            Channels = channels,
            RequestedAtUtc = DateTime.UtcNow
        };

        var task = await taskManagement.CreateTaskAsync(new BatchTask
        {
            TaskType = BatchTaskTypes.BotChannelSetAdminsByAccount,
            Total = config.Channels.Count * config.Usernames.Count,
            Completed = 0,
            Failed = 0,
            Config = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })
        });
        return Results.Ok(ToDto(task));
    }

    private static async Task<IResult> CreateBotAdminsByBotTaskAsync(
        BotAdminsByBotTaskRequestDto request,
        BotManagementService botManagement,
        BatchTaskManagementService taskManagement)
    {
        if (request.BotId <= 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择机器人"));
        var ids = NormalizeIds(request.Ids);
        var userIds = (request.UserIds ?? Array.Empty<long>()).Where(x => x > 0).Distinct().ToList();
        if (ids.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请先选择 Bot 频道"));
        if (userIds.Count == 0)
            return Results.BadRequest(new OperationResultDto(false, "请填写用户 ID"));

        var channels = (await botManagement.GetChatsAsync(request.BotId))
            .Where(x => ids.Contains(x.Id))
            .Select(x => new BotTaskChannelItem
            {
                TelegramId = x.TelegramId,
                Title = x.Title ?? x.TelegramId.ToString()
            })
            .ToList();

        var config = new BotSetAdminsTaskConfig
        {
            BotId = request.BotId,
            UserIds = userIds,
            Channels = channels,
            Rights = request.Rights ?? new BotSetAdminsRightsPayload
            {
                ManageChat = true,
                ChangeInfo = true,
                InviteUsers = true,
                PostMessages = true,
                EditMessages = true,
                DeleteMessages = true,
                PinMessages = true,
                RestrictMembers = true,
                PromoteMembers = true
            },
            RequestedAtUtc = DateTime.UtcNow
        };

        var task = await taskManagement.CreateTaskAsync(new BatchTask
        {
            TaskType = BatchTaskTypes.BotSetAdmins,
            Total = config.Channels.Count * config.UserIds.Count,
            Completed = 0,
            Failed = 0,
            Config = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })
        });
        return Results.Ok(ToDto(task));
    }

    private static async Task<IResult> GetTasksAsync(
        int? count,
        BatchTaskManagementService taskManagement,
        ScheduledTaskService scheduledTaskManagement,
        ModuleContributionRegistry contributions,
        PanelTimeZoneService timeZone,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(count ?? 50, 1, 500);
        var taskList = (await taskManagement.GetTaskCenterItemsAsync(take, cancellationToken))
            .OrderByDescending(x => x.CreatedAt)
            .Select(ToTaskListDto)
            .ToList();
        var scheduled = (await scheduledTaskManagement.GetAllAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt)
            .Select(ToScheduledTaskListDto)
            .ToList();
        var definitions = contributions.Tasks
            .OrderBy(x => x.Definition.Category ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Definition.Order)
            .ThenBy(x => x.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();

        return Results.Ok(new TaskCenterDto(taskList, scheduled, definitions, timeZone.Current.Id));
    }

    private static async Task<IResult> GetTaskAsync(
        int id,
        BatchTaskManagementService taskManagement)
    {
        var task = await taskManagement.GetTaskAsync(id);
        return task == null ? Results.NotFound() : Results.Ok(ToDto(task));
    }


    private static async Task<IResult> PauseBatchTaskAsync(
        int id,
        BatchTaskExecutionControlService executionControl,
        CancellationToken cancellationToken)
    {
        try
        {
            await executionControl.PauseTaskAsync(id, cancellationToken);
            return Results.Ok(new OperationResultDto(true, "任务已暂停，旧执行实例已退出"));
        }
        catch (BatchTaskExecutionBarrierTimeoutException ex)
        {
            return Results.Conflict(new OperationResultDto(false, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }
    }

    private static async Task<IResult> ResumeBatchTaskAsync(
        int id,
        BatchTaskExecutionControlService executionControl,
        CancellationToken cancellationToken)
    {
        try
        {
            await executionControl.ResumeTaskAsync(id, cancellationToken);
            return Results.Ok(new OperationResultDto(true, "任务已恢复"));
        }
        catch (BatchTaskExecutionBarrierTimeoutException ex)
        {
            return Results.Conflict(new OperationResultDto(false, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }
    }


    private static async Task<IResult> CancelBatchTaskAsync(
        int id,
        BatchTaskExecutionControlService executionControl,
        CancellationToken cancellationToken)
    {
        try
        {
            await executionControl.CancelTaskAsync(id, cancellationToken);
            return Results.Ok(new OperationResultDto(true, "任务已取消，旧执行实例已退出"));
        }
        catch (BatchTaskExecutionBarrierTimeoutException ex)
        {
            return Results.Conflict(new OperationResultDto(false, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }
    }

    private static async Task<IResult> DeleteBatchTaskAsync(
        int id,
        BatchTaskExecutionControlService executionControl,
        CancellationToken cancellationToken)
    {
        try
        {
            await executionControl.DeleteTaskAsync(id, cancellationToken);
            return Results.Ok(new OperationResultDto(true, "任务已删除"));
        }
        catch (BatchTaskExecutionBarrierTimeoutException ex)
        {
            return Results.Conflict(new OperationResultDto(false, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }
    }

    private static async Task<IResult> GetScheduledTaskAsync(
        int id,
        ScheduledTaskService scheduledTasks,
        CancellationToken cancellationToken)
    {
        var task = await scheduledTasks.GetAsync(id, cancellationToken);
        return task == null ? Results.NotFound() : Results.Ok(ToDto(task));
    }

    private static async Task<IResult> CreateTaskAsync(
        CreateTaskRequestDto request,
        BatchTaskManagementService tasks)
    {
        ValidateTaskSubmission(request.TaskType, request.Config);
        var nameResult = TryNormalizeBatchTaskName(request.Name, out var taskName);
        if (nameResult != null)
            return nameResult;

        var task = await tasks.CreateTaskAsync(new BatchTask
        {
            Name = taskName,
            TaskType = request.TaskType.Trim(),
            Total = Math.Max(0, request.Total),
            Completed = 0,
            Failed = 0,
            Config = NormalizeNullable(request.Config)
        });

        return Results.Ok(ToDto(task));
    }

    private static async Task<IResult> UpdateTaskAsync(
        int id,
        UpdateTaskRequestDto request,
        BatchTaskManagementService tasks,
        CancellationToken cancellationToken)
    {
        ValidateTaskSubmission(request.TaskType, request.Config);
        var existing = await tasks.GetTaskAsync(id);
        if (existing == null)
            return Results.NotFound(new OperationResultDto(false, "任务不存在或已被删除"));

        var status = GetDisplayStatus(existing);
        if (status is "pending" or "running")
            return Results.BadRequest(new OperationResultDto(false, "任务尚未安全暂停，请先暂停后再编辑"));

        if (!string.Equals(existing.TaskType, request.TaskType?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new OperationResultDto(false, "不允许修改任务类型"));

        string? taskName;
        if (request.Name == null)
        {
            taskName = existing.Name;
        }
        else
        {
            var nameResult = TryNormalizeBatchTaskName(request.Name, out taskName);
            if (nameResult != null)
                return nameResult;
        }

        var updatedDraft = await tasks.TryUpdateEditableTaskDraftAsync(
            id,
            Math.Max(0, request.Total),
            NormalizeNullable(request.Config),
            taskName,
            cancellationToken);
        if (!updatedDraft)
            return Results.Conflict(new OperationResultDto(false, "任务状态已变化，请暂停任务后重新编辑"));

        var updated = await tasks.GetTaskAsync(id);
        return updated == null
            ? Results.NotFound(new OperationResultDto(false, "任务不存在或已被删除"))
            : Results.Ok(ToDto(updated));
    }

    private static async Task<IResult> CreateScheduledTaskAsync(
        CreateScheduledTaskRequestDto request,
        ScheduledTaskService scheduledTasks)
    {
        ValidateTaskSubmission(request.TaskType, request.ConfigJson);
        var task = await scheduledTasks.CreateAsync(new ScheduledTask
        {
            Name = request.Name ?? string.Empty,
            TaskType = request.TaskType.Trim(),
            Total = Math.Max(0, request.Total),
            ConfigJson = NormalizeNullable(request.ConfigJson),
            CronExpression = request.CronExpression,
            Status = string.IsNullOrWhiteSpace(request.Status) ? ScheduledTaskStatuses.Enabled : request.Status.Trim(),
            OwnedAssetScopeId = TaskAssetScopeHelper.GetAssetScopeId(request.ConfigJson)
        });

        return Results.Ok(ToDto(task));
    }

    private static async Task<IResult> UpdateScheduledTaskAsync(
        int id,
        UpdateScheduledTaskRequestDto request,
        ScheduledTaskService scheduledTasks)
    {
        ValidateTaskSubmission(request.TaskType, request.ConfigJson);
        var task = await scheduledTasks.GetAsync(id);
        if (task == null)
            return Results.NotFound(new OperationResultDto(false, "计划任务不存在或已被删除"));

        if (!string.Equals(task.TaskType, request.TaskType?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new OperationResultDto(false, "不允许修改任务类型"));

        if (request.Name is not null)
            task.Name = request.Name;
        task.Total = Math.Max(0, request.Total);
        task.ConfigJson = NormalizeNullable(request.ConfigJson);
        task.CronExpression = request.CronExpression;
        task.Status = string.IsNullOrWhiteSpace(request.Status) ? ScheduledTaskStatuses.Enabled : request.Status.Trim();
        task.OwnedAssetScopeId = TaskAssetScopeHelper.GetAssetScopeId(task.ConfigJson);

        var updated = await scheduledTasks.UpdateAsync(task);
        return Results.Ok(ToDto(updated));
    }

    private static async Task<IResult> RunScheduledTaskNowAsync(
        int id,
        ScheduledTaskService scheduledTasks,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await scheduledTasks.RunNowAsync(id, cancellationToken);
            return created == null
                ? Results.NotFound(new OperationResultDto(false, "计划任务不存在或已被删除"))
                : Results.Ok(ToDto(created));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }
    }

    private static async Task<IResult> CleanupTasksAsync(
        CleanupTasksRequestDto request,
        BatchTaskManagementService tasks,
        BatchTaskExecutionControlService executionControl,
        ImageAssetStorageService assetStorage,
        CancellationToken cancellationToken)
    {
        var mode = (request.Mode ?? string.Empty).Trim();
        var historyOnly = string.Equals(mode, "history", StringComparison.OrdinalIgnoreCase);
        if (!historyOnly && !string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new OperationResultDto(false, "清理模式无效"));

        var allTasks = (await tasks.GetAllTasksAsync()).ToList();
        var targets = historyOnly
            ? allTasks.Where(x => IsHistoryStatus(GetDisplayStatus(x))).ToList()
            : allTasks;

        foreach (var task in targets)
        {
            if (historyOnly)
                await tasks.DeleteTaskAsync(task.Id);
            else
                await executionControl.DeleteTaskAsync(task.Id, cancellationToken);

            var scopeId = TaskAssetScopeHelper.GetAssetScopeId(task.Config);
            if (!string.IsNullOrWhiteSpace(scopeId))
                await assetStorage.DeleteScopeAsync(scopeId);
        }

        var message = historyOnly
            ? $"已清理 {targets.Count} 条历史任务记录"
            : $"已清理 {targets.Count} 条任务记录";
        return Results.Ok(new OperationResultDto(true, message));
    }

    private static async Task<IResult> UploadTaskAvatarAssetAsync(
        HttpRequest httpRequest,
        ImageAssetStorageService assetStorage,
        CancellationToken cancellationToken)
    {
        if (!httpRequest.HasFormContentType)
            return Results.BadRequest(new OperationResultDto(false, "请使用 multipart/form-data 上传任务头像"));

        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file == null)
            return Results.BadRequest(new OperationResultDto(false, "请先选择头像图片"));

        if (file.Length <= 0)
            return Results.BadRequest(new OperationResultDto(false, "头像图片为空"));

        var scopeId = NormalizeTaskAssetScopeId(form["scopeId"].ToString());
        if (string.IsNullOrWhiteSpace(scopeId))
            scopeId = Guid.NewGuid().ToString("N");

        var stored = await assetStorage.SaveAsync(
            file.OpenReadStream(),
            file.FileName,
            $"task-assets/{scopeId}",
            ImageAssetKind.Avatar,
            cancellationToken);

        return Results.Ok(new TaskAssetUploadResultDto(stored.AssetPath, stored.FileName, scopeId));
    }

    private static async Task<IResult> GetDataDictionariesAsync(
        DataDictionaryService dictionaries,
        CancellationToken cancellationToken)
    {
        var items = (await dictionaries.GetAllAsync(cancellationToken))
            .Select(ToDto)
            .ToList();
        return Results.Ok(items);
    }

    private static async Task<IResult> SetDictionaryEnabledAsync(
        int id,
        SetEnabledRequestDto request,
        DataDictionaryService dictionaries,
        CancellationToken cancellationToken)
    {
        await dictionaries.SetEnabledAsync(id, request.IsEnabled, cancellationToken);
        return Results.Ok(new OperationResultDto(true, request.IsEnabled ? "字典已启用" : "字典已停用"));
    }

    private static async Task<IResult> SaveTextDictionaryAsync(
        SaveTextDictionaryRequestDto request,
        DataDictionaryService dictionaries,
        CancellationToken cancellationToken)
    {
        try
        {
            var saved = await dictionaries.SaveTextDictionaryAsync(
                request.Id,
                request.Name,
                request.DisplayName,
                request.Description,
                request.ReadMode,
                request.IsEnabled,
                (request.Items ?? Array.Empty<string>()).Select(x => new DataDictionaryTextItemInput(x)).ToList(),
                cancellationToken);

            return Results.Ok(ToDto(saved));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }
    }

    private static async Task<IResult> SaveImageDictionaryAsync(
        HttpRequest httpRequest,
        DataDictionaryService dictionaries,
        ImageAssetStorageService assetStorage,
        CancellationToken cancellationToken)
    {
        if (!httpRequest.HasFormContentType)
            return Results.BadRequest(new OperationResultDto(false, "请使用 multipart/form-data 保存图片字典"));

        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var id = ParseNullableInt(form["id"]);
        var name = form["name"].ToString();
        var displayName = form["displayName"].ToString();
        var description = form["description"].ToString();
        var readMode = form["readMode"].ToString();
        var isEnabled = ParseBool(form["isEnabled"]);
        var keepItemIds = ParseIntList(form["keepItemIds"]);

        var uploadScope = id is > 0
            ? $"dictionaries/{id.Value}"
            : $"dictionaries/draft-{Guid.NewGuid():N}";
        var newImages = new List<DataDictionaryImageItemInput>();
        async Task DeleteUploadedImagesAsync()
        {
            foreach (var image in newImages)
                await assetStorage.DeleteAssetAsync(image.AssetPath, cancellationToken);
        }

        try
        {
            foreach (var file in form.Files.GetFiles("images"))
            {
                if (file.Length > 20L * 1024 * 1024)
                    throw new InvalidOperationException($"图片 {file.FileName} 超过 20MB");

                await using var stream = file.OpenReadStream();
                var stored = await assetStorage.SaveAsync(stream, file.FileName, uploadScope, ImageAssetKind.General, cancellationToken);
                newImages.Add(new DataDictionaryImageItemInput(stored.AssetPath, stored.FileName));
            }

            var saved = await dictionaries.SaveImageDictionaryAsync(
                id,
                name,
                displayName,
                description,
                readMode,
                isEnabled,
                keepItemIds,
                newImages,
                cancellationToken);

            return Results.Ok(ToDto(saved));
        }
        catch (InvalidOperationException ex)
        {
            await DeleteUploadedImagesAsync();
            return Results.BadRequest(new OperationResultDto(false, ex.Message));
        }
        catch (Exception)
        {
            await DeleteUploadedImagesAsync();
            throw;
        }
    }

    private static async Task<IResult> GetModulesAsync(
        ModuleInstallerService moduleInstaller,
        ModuleContributionRegistry contributions)
    {
        var modules = (await moduleInstaller.GetOverviewAsync())
            .Select(ToDto)
            .ToList();
        return Results.Ok(new ModuleCenterDto(modules, contributions.Diagnostics.ToList()));
    }

    private static async Task<IResult> InstallModuleAsync(
        HttpRequest httpRequest,
        ModuleInstallerService moduleInstaller,
        AppRestartService restart)
    {
        if (!httpRequest.HasFormContentType)
            return Results.BadRequest(new OperationResultDto(false, "请使用 multipart/form-data 上传模块包"));

        var form = await httpRequest.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file == null)
            return Results.BadRequest(new OperationResultDto(false, "请先选择模块包"));
        if (file.Length > 50L * 1024 * 1024)
            return Results.BadRequest(new OperationResultDto(false, "模块包不能超过 50MB"));

        var activateAndEnable = ParseBool(form["activateAndEnable"]);
        var autoRestart = ParseBool(form["autoRestart"]);
        await using var stream = file.OpenReadStream();
        var result = await moduleInstaller.InstallAsync(stream, file.FileName, enableAfterInstall: false);
        if (!result.Success)
            return Results.BadRequest(new OperationResultDto(false, result.Message));

        var message = $"上传成功：{result.ModuleId} {result.Version}";
        if (activateAndEnable)
        {
            var moduleId = (result.ModuleId ?? string.Empty).Trim();
            var version = (result.Version ?? string.Empty).Trim();
            if (moduleId.Length == 0 || version.Length == 0)
                return Results.Ok(new ModuleOperationResultDto(true, "上传成功，但返回的模块信息不完整（ModuleId/Version 为空）", result.ModuleId, result.Version));

            var setActive = await moduleInstaller.SetActiveVersionAsync(moduleId, version);
            if (!setActive.Success)
                return Results.Ok(new ModuleOperationResultDto(true, $"已上传：{moduleId} {version}，但切换版本失败：{setActive.Message}", moduleId, version));

            var enable = await moduleInstaller.EnableAsync(moduleId);
            if (!enable.Success)
                return Results.Ok(new ModuleOperationResultDto(true, $"已上传：{moduleId} {version}，但启用失败：{enable.Message}", moduleId, version));

            message = $"已上传并启用：{moduleId} {version}";
        }

        if (autoRestart)
        {
            var scheduled = restart.RequestRestart(TimeSpan.FromSeconds(1), $"module install {result.ModuleId} {result.Version}");
            message += scheduled ? "，已请求重启服务" : "，重启请求已在等待执行";
        }

        return Results.Ok(new ModuleOperationResultDto(true, message, result.ModuleId, result.Version));
    }

    private static Task<IResult> RestartSystemAsync(AppRestartService restart)
    {
        var scheduled = restart.RequestRestart(TimeSpan.FromSeconds(1), "manual restart from panel");
        var message = scheduled
            ? "已提交重启请求，服务将在约 1 秒后退出；Docker、系统服务或桌面版守护进程会负责重新拉起。"
            : "已有重启请求正在等待执行。";
        return Task.FromResult<IResult>(Results.Ok(new SystemRestartResultDto(true, message, scheduled)));
    }

    private static async Task<IResult> EnableModuleAsync(
        string id,
        ModuleActionRequestDto request,
        ModuleInstallerService moduleInstaller,
        AppRestartService restart)
    {
        var result = await moduleInstaller.EnableAsync(id);
        if (!result.Success)
            return Results.BadRequest(new OperationResultDto(false, result.Message));

        if (request.AutoRestart)
            restart.RequestRestart(TimeSpan.FromSeconds(1), $"module enable {id}");

        return Results.Ok(new OperationResultDto(true, "已启用"));
    }

    private static async Task<IResult> DisableModuleAsync(
        string id,
        ModuleActionRequestDto request,
        ModuleInstallerService moduleInstaller,
        AppRestartService restart)
    {
        var result = await moduleInstaller.DisableAsync(id);
        if (!result.Success)
            return Results.BadRequest(new OperationResultDto(false, result.Message));

        if (request.AutoRestart)
            restart.RequestRestart(TimeSpan.FromSeconds(1), $"module disable {id}");

        return Results.Ok(new OperationResultDto(true, "已停用"));
    }

    private static async Task<IResult> RemoveModuleAsync(
        string id,
        bool? autoRestart,
        ModuleInstallerService moduleInstaller,
        AppRestartService restart)
    {
        var result = await moduleInstaller.RemoveModuleAsync(id);
        if (!result.Success)
            return Results.BadRequest(new OperationResultDto(false, result.Message));

        if (autoRestart == true)
            restart.RequestRestart(TimeSpan.FromSeconds(1), $"module remove {id}");

        return Results.Ok(new OperationResultDto(true, "已删除"));
    }

    private static async Task<IResult> PruneModuleVersionsAsync(
        string id,
        ModuleActionRequestDto request,
        ModuleInstallerService moduleInstaller,
        AppRestartService restart)
    {
        var result = await moduleInstaller.PruneOldVersionsAsync(id);
        if (!result.Success)
            return Results.BadRequest(new OperationResultDto(false, result.Message));

        if (request.AutoRestart)
            restart.RequestRestart(TimeSpan.FromSeconds(1), $"module prune {id}");

        return Results.Ok(new OperationResultDto(true, "已清理旧版本"));
    }

    private static async Task<IResult> GetExternalApisAsync(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ModuleContributionRegistry contributions)
    {
        var apis = await LoadExternalApisAsync(configuration, environment);
        var types = contributions.ApiTypes
            .OrderBy(x => x.Definition.Order)
            .ThenBy(x => x.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(x => ToDto(x.Definition))
            .ToList();
        var availableTypeSet = contributions.ApiTypeToDefinition.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Results.Ok(new ExternalApiCenterDto(
            apis
                .OrderByDescending(x => x.Enabled)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => ToDto(x, contributions))
                .ToList(),
            types,
            availableTypeSet.ToList()));
    }

    private static async Task<IResult> SaveExternalApiAsync(
        SaveExternalApiRequestDto request,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ModuleContributionRegistry contributions)
    {
        var apis = await LoadExternalApisAsync(configuration, environment);
        var api = FromDto(request);
        if (string.IsNullOrWhiteSpace(api.Id))
            api.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(api.Name))
            return Results.BadRequest(new OperationResultDto(false, "API 名称不能为空"));
        if (string.IsNullOrWhiteSpace(api.Type) || !contributions.ApiTypeToDefinition.ContainsKey(api.Type))
            return Results.BadRequest(new OperationResultDto(false, "请选择有效的 API 类型"));
        if (api.Enabled && string.IsNullOrWhiteSpace(api.ApiKey))
            return Results.BadRequest(new OperationResultDto(false, "启用前请先设置 X-API-Key"));
        if (apis.Any(x => !string.Equals(x.Id, api.Id, StringComparison.Ordinal)
                          && string.Equals(x.ApiKey, api.ApiKey, StringComparison.Ordinal)))
            return Results.BadRequest(new OperationResultDto(false, "X-API-Key 已存在，请更换后保存"));

        var index = apis.FindIndex(x => string.Equals(x.Id, api.Id, StringComparison.Ordinal));
        if (index >= 0)
            apis[index] = api;
        else
            apis.Add(api);

        await SaveExternalApisAsync(configuration, environment, apis);
        return Results.Ok(ToDto(api, contributions));
    }

    private static async Task<IResult> DeleteExternalApiAsync(
        string id,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var apis = await LoadExternalApisAsync(configuration, environment);
        apis.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        await SaveExternalApisAsync(configuration, environment, apis);
        return Results.Ok(new OperationResultDto(true, "已删除"));
    }

    private static async Task<IResult> GetExternalApiBotsAsync(BotManagementService botManagement)
    {
        var bots = (await botManagement.GetAllBotsAsync())
            .OrderBy(x => x.Id)
            .Select(ToDto)
            .ToList();
        return Results.Ok(bots);
    }

    private static async Task<IResult> GetExternalApiBotChatsAsync(
        int botId,
        BotManagementService botManagement)
    {
        var chats = (await botManagement.GetChatsAsync(botId))
            .OrderByDescending(x => x.IsBroadcast)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
        return Results.Ok(chats);
    }

    private static Task<IResult> GetModuleNavAsync(ModuleContributionRegistry contributions)
    {
        var items = new List<ModuleNavItemDto>();

        items.AddRange(contributions.NavItems
            .OrderBy(x => x.Definition.Group ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Definition.Order)
            .ThenBy(x => x.Definition.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                Item = x,
                Href = ResolveVueModuleHref(x.Module.Id, null, x.Definition.Href),
                PageKey = ResolveModuleNavPageKey(x.Module.Id, x.Definition.Href)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Href))
            .Select(x => new ModuleNavItemDto(
                x.Item.Definition.Title,
                x.Href!,
                x.Item.Definition.Icon,
                x.Item.Definition.Group,
                x.Item.Definition.Order,
                x.Item.Module.Id,
                x.PageKey,
                x.PageKey == null ? "direct" : "embedded")));

        items.AddRange(contributions.Pages
            .OrderBy(x => x.Definition.Group ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Definition.Order)
            .ThenBy(x => x.Definition.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                Page = x,
                Href = ResolveVueModuleHref(x.Module.Id, x.Definition.Key, null)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Href))
            .Select(x => new ModuleNavItemDto(
                x.Page.Definition.Title,
                x.Href!,
                x.Page.Definition.Icon,
                x.Page.Definition.Group,
                x.Page.Definition.Order,
                x.Page.Module.Id,
                x.Page.Definition.Key,
                "legacy")));

        return Task.FromResult<IResult>(Results.Ok(items));
    }

    private static string? ResolveVueModuleHref(string moduleId, string? pageKey, string? href)
    {
        var raw = NormalizeModuleHref(href);
        if (!string.IsNullOrWhiteSpace(pageKey))
            raw = $"/ext/{moduleId}/{pageKey}";

        return raw.StartsWith("/ext/", StringComparison.OrdinalIgnoreCase) ? raw : null;
    }

    private static string? ResolveModuleNavPageKey(string moduleId, string? href)
        => ExtensionModuleRoute.TryParse(href, out var routeModuleId, out var pageKey)
           && string.Equals(routeModuleId, moduleId, StringComparison.OrdinalIgnoreCase)
            ? pageKey
            : null;

    private static string NormalizeModuleHref(string? href)
    {
        var value = (href ?? string.Empty).Trim();
        if (value.StartsWith("/ui/", StringComparison.OrdinalIgnoreCase))
            value = value[3..];
        if (value.Length > 0 && !value.StartsWith('/'))
            value = "/" + value;
        return value;
    }

    private static AccountCategoryDto ToDto(AccountCategory category) =>
        new(category.Id, category.Name, category.Color, category.Description, category.ExcludeFromOperations, category.Accounts?.Count ?? 0);
    private static TelegramDeviceProfileDto ToDto(TelegramDeviceProfileDefinition profile) =>
        new(
            profile.Key,
            profile.Name,
            profile.Family,
            profile.AppVersion,
            profile.DeviceModel,
            profile.SystemVersion,
            profile.SystemLangCode,
            profile.LangCode,
            profile.BuiltIn,
            profile.Notes);

    private static VersionInfoDto ToDto(AppSelfUpdateInfo info) =>
        new(
            info.Success,
            info.Error,
            info.Enabled,
            info.CurrentVersion,
            info.LatestVersion,
            info.LatestTag,
            info.UpdateAvailable,
            info.Url,
            info.PublishedAt,
            info.Notes,
            info.CheckedAtUtc,
            info.IsDocker,
            info.UpdateMode,
            info.CanApply,
            info.BlockedReason,
            info.AssetName,
            info.AssetSizeBytes);

    private static VersionApplyResultDto ToDto(AppSelfUpdateApplyResult result) =>
        new(result.Success, result.Message, result.RestartScheduled, result.LatestTag, result.LatestVersion);

    private static AccountDetailDto ToDetailDto(Account account) =>
        new(
            account.Id,
            account.DisplayNumber,
            account.DisplayPhone,
            account.Phone,
            account.Nickname,
            account.Username,
            account.Remark,
            account.UserId,
            account.SessionPath,
            account.DeviceProfileKey,
            account.TwoFactorPassword,
            account.CategoryId,
            account.IsActive,
            account.CreatedAt,
            account.EstimatedRegistrationAt,
            account.LastSyncAt,
            account.LastLoginAt);

    private static AccountListItemDto ToDto(Account account) =>
        new(
            account.Id,
            account.DisplayNumber,
            account.DisplayPhone,
            account.Nickname,
            account.Username,
            account.Remark,
            account.UserId,
            account.IsActive,
            account.Category == null ? null : ToDto(account.Category),
            account.ChannelCount,
            account.GroupCount,
            account.EstimatedRegistrationAt,
            account.LastSyncAt,
            account.TelegramStatusSummary,
            account.TelegramStatusDetails,
            account.TelegramStatusOk,
            account.TelegramStatusCheckedAtUtc,
            account.UseGlobalProxy,
            account.Proxy == null
                ? null
                : new AccountProxySummaryDto(
                    account.Proxy.Id,
                    account.Proxy.Name,
                    account.Proxy.Kind,
                    account.Proxy.Protocol,
                    account.Proxy.Host,
                    account.Proxy.Port,
                    account.Proxy.ResinPlatform,
                    account.Proxy.IsEnabled,
                    account.Proxy.TestStatus,
                    account.Proxy.EgressIp));

    private static RiskAccountDto ToRiskAccountDto(Account account)
    {
        var hours = account.GetRiskReferenceHours();
        return new RiskAccountDto(
            account.Id,
            account.DisplayNumber,
            account.DisplayPhone,
            hours,
            account.IsRiskReferenceEstimated());
    }

    private static TelegramStatusDto ToDto(TelegramAccountStatusResult status) =>
        new(status.Ok, status.Summary, status.Details, status.CheckedAtUtc);

    private static TelegramSystemMessageDto ToDto(TelegramSystemMessage message) =>
        new(message.Id, message.DateUtc, message.Text);

    private static TelegramAuthorizationDto ToDto(TelegramAuthorizationInfo auth) =>
        new(
            auth.Hash.ToString(CultureInfo.InvariantCulture),
            auth.Current,
            auth.ApiId,
            auth.AppName,
            auth.AppVersion,
            auth.DeviceModel,
            auth.Platform,
            auth.SystemVersion,
            auth.Ip,
            auth.Country,
            auth.Region,
            auth.CreatedAtUtc,
            auth.LastActiveAtUtc,
            auth.Title,
            auth.ApiFamily,
            auth.ApiDisplayName,
            auth.ApiDescription,
            auth.DeviceDisplayName);

    private static AccountChannelMembershipDto ToDto(AccountChannel membership)
    {
        var channel = membership.Channel;
        return new AccountChannelMembershipDto(
            channel.Id,
            channel.Title,
            channel.Username,
            membership.IsCreator,
            membership.IsAdmin,
            channel.Group?.Name,
            channel.MemberCount,
            membership.SyncedAt);
    }

    private static AccountGroupMembershipDto ToDto(AccountGroup membership)
    {
        var group = membership.Group;
        return new AccountGroupMembershipDto(
            group.Id,
            group.Title,
            group.Username,
            membership.IsCreator,
            membership.IsAdmin,
            group.Category?.Name,
            group.MemberCount,
            membership.SyncedAt);
    }

    private static BatchTaskDto ToDto(BatchTask task) =>
        new(
            task.Id,
            task.Name,
            task.TaskType,
            task.Status,
            task.Total,
            task.Completed,
            task.Failed,
            task.Config,
            task.CreatedAt,
            task.StartedAt,
            task.CompletedAt);

    private static BatchTaskDto ToTaskListDto(BatchTask task) =>
        new(
            task.Id,
            task.Name,
            task.TaskType,
            task.Status,
            task.Total,
            task.Completed,
            task.Failed,
            null,
            task.CreatedAt,
            task.StartedAt,
            task.CompletedAt);

    private static ScheduledTaskDto ToDto(ScheduledTask task) =>
        new(
            task.Id,
            task.Name,
            task.TaskType,
            task.Status,
            task.Total,
            task.ConfigJson,
            task.CronExpression,
            task.NextRunAtUtc,
            task.LastRunAtUtc,
            task.LastBatchTaskId,
            task.CreatedAt,
            task.UpdatedAt);

    private static ScheduledTaskDto ToScheduledTaskListDto(ScheduledTask task) =>
        new(
            task.Id,
            task.Name,
            task.TaskType,
            task.Status,
            task.Total,
            null,
            task.CronExpression,
            task.NextRunAtUtc,
            task.LastRunAtUtc,
            task.LastBatchTaskId,
            task.CreatedAt,
            task.UpdatedAt);

    private static TaskDefinitionDto ToDto(RegisteredTaskDefinition registered) =>
        new(
            registered.Definition.TaskType,
            registered.Definition.DisplayName,
            registered.Definition.Category,
            registered.Definition.Description,
            registered.Definition.Icon,
            registered.Definition.CreateRoute,
            registered.CanCreate,
            registered.Definition.TaskCenter.CanPause,
            registered.Definition.TaskCenter.CanResume,
            registered.Definition.TaskCenter.CanEdit,
            registered.Definition.TaskCenter.CanRerun,
            registered.Definition.TaskCenter.AutoPauseBeforeEdit);

    private static DataDictionaryDto ToDto(DataDictionary dictionary) =>
        new(
            dictionary.Id,
            dictionary.Name,
            dictionary.DisplayName,
            dictionary.Description,
            dictionary.Type,
            dictionary.ReadMode,
            dictionary.IsEnabled,
            dictionary.NextIndex,
            dictionary.Items.Count(x => x.IsEnabled),
            dictionary.CreatedAt,
            dictionary.UpdatedAt,
            dictionary.Items
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .Select(ToDto)
                .ToList());

    private static DataDictionaryItemDto ToDto(DataDictionaryItem item) =>
        new(item.Id, item.TextValue, item.AssetPath, item.FileName, item.SortOrder, item.IsEnabled, item.CreatedAt);

    private static ModuleOverviewDto ToDto(ModuleOverview module) =>
        new(
            module.Id,
            module.Enabled,
            module.ActiveVersion,
            module.LastGoodVersion,
            module.InstalledVersions,
            module.Manifest == null ? null : ToDto(module.Manifest),
            module.ManifestError,
            module.BuiltIn);

    private static ModuleManifestDto ToDto(ModuleManifest manifest) =>
        new(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Host?.Min,
            manifest.Host?.Max,
            manifest.Dependencies
                .Select(x => new ModuleDependencyDto(x.Id, x.Range))
                .ToList(),
            manifest.Entry?.Assembly,
            manifest.Entry?.Type);

    private static ExternalApiTypeDto ToDto(ModuleApiTypeDefinition definition) =>
        new(
            definition.Type,
            definition.DisplayName,
            definition.Route,
            definition.Description,
            definition.Order);

    private static ExternalApiDefinitionDto ToDto(ExternalApiDefinition api, ModuleContributionRegistry contributions)
    {
        contributions.ApiTypeToDefinition.TryGetValue(api.Type, out var registered);
        return new ExternalApiDefinitionDto(
            api.Id,
            api.Name,
            api.Type,
            registered?.Definition.DisplayName ?? api.Type,
            registered?.Definition.Route,
            registered != null,
            api.Enabled,
            api.ApiKey,
            api.Config ?? new JsonObject(),
            api.Kick == null ? null : ToDto(api.Kick));
    }

    private static KickApiDto ToDto(KickApiDefinition kick) =>
        new(kick.BotId, kick.UseAllChats, kick.ChatIds ?? new List<long>(), kick.PermanentBanDefault);

    private static BotOptionDto ToDto(Bot bot) =>
        new(bot.Id, bot.Name, bot.Username, bot.IsActive);

    private static BotChatOptionDto ToDto(BotChannel channel) =>
        new(
            channel.Id,
            channel.TelegramId,
            channel.Title,
            channel.Username,
            channel.IsBroadcast,
            channel.MemberCount);

    private static OperationAccountDto ToOperationAccountDto(Account account) =>
        new(account.Id, account.DisplayNumber, account.DisplayPhone, account.Nickname, account.Username, account.IsActive, account.CategoryId, account.Category?.Name);

    private static ChatAdminDto ToDto(ChannelAdminInfo admin) =>
        new(
            admin.UserId,
            admin.Username,
            admin.DisplayName,
            admin.IsCreator,
            admin.Rank,
            null,
            null,
            null,
            null);

    private static ChatAdminDto ToDto(BotTelegramService.BotChatAdminInfo admin) =>
        new(
            admin.UserId,
            admin.Username,
            admin.DisplayName,
            admin.IsCreator,
            admin.CustomTitle,
            admin.Status,
            admin.CanInviteUsers,
            admin.CanPromoteMembers,
            admin.CanRestrictMembers);

    private static SimpleCategoryDto ToDto(ChannelGroup group) =>
        new(group.Id, group.Name, group.Description, group.Channels?.Count ?? 0, group.CreatedAt);

    private static SimpleCategoryDto ToDto(GroupCategory category) =>
        new(category.Id, category.Name, category.Description, category.Groups?.Count ?? 0, category.CreatedAt);

    private static SimpleCategoryDto ToDto(BotChannelCategory category) =>
        new(category.Id, category.Name, category.Description, category.Channels?.Count ?? 0, category.CreatedAt);

    private static ChannelListItemDto ToDto(Channel channel) =>
        new(
            channel.Id,
            channel.TelegramId,
            channel.Title,
            channel.Username,
            channel.MemberCount,
            channel.About,
            channel.CreatorAccountId,
            channel.CreatorAccount?.DisplayPhone,
            channel.GroupId,
            channel.Group?.Name,
            channel.CreatedAt,
            channel.SystemCreatedAtUtc,
            channel.SyncedAt,
            channel.AccountChannels
                .Select(x => new ChatMembershipAccountDto(
                    x.AccountId,
                    x.Account?.DisplayPhone,
                    x.IsCreator,
                    x.IsAdmin,
                    x.SyncedAt))
                .ToList());

    private static GroupListItemDto ToDto(Group group) =>
        new(
            group.Id,
            group.TelegramId,
            group.Title,
            group.Username,
            group.MemberCount,
            group.About,
            group.CreatorAccountId,
            group.CreatorAccount?.DisplayPhone,
            group.CategoryId,
            group.Category?.Name,
            group.CreatedAt,
            group.SystemCreatedAtUtc,
            group.SyncedAt,
            group.AccountGroups
                .Select(x => new ChatMembershipAccountDto(
                    x.AccountId,
                    x.Account?.DisplayPhone,
                    x.IsCreator,
                    x.IsAdmin,
                    x.SyncedAt))
                .ToList());

    private static ChannelDetailDto ToDetailDto(Channel channel, IReadOnlyList<AccountChannel> memberships) =>
        new(
            ToDto(channel),
            memberships
                .Select(x => new ChatMembershipAccountDto(
                    x.AccountId,
                    x.Account?.DisplayPhone,
                    x.IsCreator,
                    x.IsAdmin,
                    x.SyncedAt))
                .ToList());

    private static GroupDetailDto ToDetailDto(Group group, IReadOnlyList<AccountGroup> memberships) =>
        new(
            ToDto(group),
            memberships
                .Select(x => new ChatMembershipAccountDto(
                    x.AccountId,
                    x.Account?.DisplayPhone,
                    x.IsCreator,
                    x.IsAdmin,
                    x.SyncedAt))
                .ToList());

    private static BotManagementDto ToBotManagementDto(Bot bot) =>
        new(
            bot.Id,
            bot.Name,
            bot.Username,
            bot.IsActive,
            bot.ChannelMembers?.Count ?? 0,
            bot.CreatedAt,
            bot.LastSyncAt);

    private static BotChannelListItemDto ToBotChannelListDto(BotChannel channel, IReadOnlyList<Bot>? boundBots = null) =>
        new(
            channel.Id,
            channel.TelegramId,
            channel.Title,
            channel.Username,
            channel.IsBroadcast,
            channel.MemberCount,
            channel.About,
            channel.CategoryId,
            channel.Category?.Name,
            channel.ChannelStatusOk,
            channel.ChannelStatusCheckedAtUtc,
            channel.ChannelStatusError,
            channel.CreatedAt,
            channel.SyncedAt,
            (boundBots ?? Array.Empty<Bot>())
                .Select(bot => new BotBindingDto(bot.Id, bot.Name, bot.Username))
                .ToList());

    private static SyncResultDto ToDto(DataSyncService.SyncSummary summary, int? taskId) =>
        new(
            taskId,
            summary.TotalAccounts,
            summary.ProcessedAccounts,
            summary.TotalChannelsSynced,
            summary.TotalGroupsSynced,
            summary.AccountFailures
                .Select(x => new SyncFailureDto(x.AccountId, x.Phone, x.Error))
                .ToList(),
            summary.AccountFailures.Count == 0 ? "同步完成" : $"同步完成，失败 {summary.AccountFailures.Count} 个账号");

    private static ExternalApiDefinition FromDto(SaveExternalApiRequestDto request)
    {
        var api = new ExternalApiDefinition
        {
            Id = NormalizeNullable(request.Id) ?? Guid.NewGuid().ToString("N"),
            Name = (request.Name ?? string.Empty).Trim(),
            Type = (request.Type ?? string.Empty).Trim(),
            Enabled = request.Enabled,
            ApiKey = (request.ApiKey ?? string.Empty).Trim(),
            Config = request.Config ?? new JsonObject()
        };

        if (request.Kick != null || string.Equals(api.Type, "kick", StringComparison.OrdinalIgnoreCase))
        {
            api.Kick = BuildKickApiDefinition(request.Kick, api.Config);
            api.Config = BuildKickConfig(api.Kick);
        }

        return api;
    }

    private static async Task<List<ExternalApiDefinition>> LoadExternalApisAsync(
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var list = configuration.GetSection("ExternalApi:Apis").Get<List<ExternalApiDefinition>>() ?? new List<ExternalApiDefinition>();

        var localPath = LocalConfigFile.ResolvePath(configuration, environment);
        if (File.Exists(localPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(localPath);
                if (!string.IsNullOrWhiteSpace(json)
                    && JsonNode.Parse(json)?["ExternalApi"]?["Apis"] is JsonArray apisNode)
                {
                    list = apisNode.Deserialize<List<ExternalApiDefinition>>(LocalConfigFile.CreateIndentedJsonSerializerOptions())
                           ?? new List<ExternalApiDefinition>();
                }
            }
            catch
            {
                // 本地配置损坏时保持当前 IConfiguration 结果，避免 API 中心白屏。
            }
        }

        foreach (var api in list)
            NormalizeExternalApi(api);

        return list;
    }

    private static async Task SaveExternalApisAsync(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        List<ExternalApiDefinition> apis)
    {
        var path = LocalConfigFile.ResolvePath(configuration, environment);
        var root = await LoadLocalConfigRootAsync(path);
        var external = EnsureObject(root, "ExternalApi");

        var options = LocalConfigFile.CreateIndentedJsonSerializerOptions();
        external["Apis"] = JsonSerializer.SerializeToNode(apis, options) ?? new JsonArray();

        await LocalConfigFile.EnsureExistsAsync(path);
        await LocalConfigFile.WriteJsonAtomicallyAsync(path, LocalConfigFile.ToIndentedJson(root));
    }

    private static async Task<JsonObject> LoadLocalConfigRootAsync(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        var json = await File.ReadAllTextAsync(path);
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }

    private static async Task SaveLocalRootAsync(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        JsonObject root,
        CancellationToken cancellationToken)
    {
        var path = LocalConfigFile.ResolvePath(configuration, environment);
        await LocalConfigFile.EnsureExistsAsync(path, cancellationToken);
        await LocalConfigFile.WriteJsonAtomicallyAsync(path, LocalConfigFile.ToIndentedJson(root), cancellationToken);
    }

    private static JsonObject EnsureObject(JsonObject root, string key)
    {
        if (root[key] is JsonObject obj)
            return obj;

        var created = new JsonObject();
        root[key] = created;
        return created;
    }

    private static void NormalizeExternalApi(ExternalApiDefinition api)
    {
        api.Id = NormalizeNullable(api.Id) ?? Guid.NewGuid().ToString("N");
        api.Name = (api.Name ?? string.Empty).Trim();
        api.Type = (api.Type ?? string.Empty).Trim();
        api.ApiKey = (api.ApiKey ?? string.Empty).Trim();
        api.Config ??= new JsonObject();
        if (string.Equals(api.Type, "kick", StringComparison.OrdinalIgnoreCase))
        {
            api.Kick = BuildKickApiDefinition(api.Kick == null ? null : ToDto(api.Kick), api.Config);
            api.Config = BuildKickConfig(api.Kick);
        }
    }

    private static KickApiDefinition BuildKickApiDefinition(KickApiDto? dto, JsonObject? config)
    {
        var kick = new KickApiDefinition
        {
            BotId = dto?.BotId ?? ReadInt(config, "botId", "bot_id"),
            UseAllChats = dto?.UseAllChats ?? ReadBool(config, true, "useAllChats", "use_all_chats"),
            ChatIds = dto?.ChatIds?
                .Where(x => x != 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList() ?? ReadLongList(config, "chatIds", "chat_ids"),
            PermanentBanDefault = dto?.PermanentBanDefault ?? ReadBool(config, false, "permanentBanDefault", "permanent_ban_default")
        };

        if (kick.BotId == 0)
        {
            kick.UseAllChats = true;
            kick.ChatIds.Clear();
        }

        return kick;
    }

    private static JsonObject BuildKickConfig(KickApiDefinition kick)
    {
        var chatIds = new JsonArray();
        foreach (var chatId in kick.ChatIds)
            chatIds.Add(chatId);

        return new JsonObject
        {
            ["botId"] = kick.BotId,
            ["useAllChats"] = kick.UseAllChats,
            ["chatIds"] = chatIds,
            ["permanentBanDefault"] = kick.PermanentBanDefault
        };
    }

    private static int ReadInt(JsonObject? obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj?[name] is JsonValue value && value.TryGetValue<int>(out var intValue))
                return intValue;
        }

        return 0;
    }

    private static bool ReadBool(JsonObject? obj, bool fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj?[name] is JsonValue value && value.TryGetValue<bool>(out var boolValue))
                return boolValue;
        }

        return fallback;
    }

    private static List<long> ReadLongList(JsonObject? obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj?[name] is not JsonArray arr)
                continue;

            return arr
                .Select(x => x is JsonValue value && value.TryGetValue<long>(out var longValue) ? longValue : 0)
                .Where(x => x != 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }

        return new List<long>();
    }

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static async Task<SaveChatFormRequest> ReadSaveChatRequestAsync(HttpRequest httpRequest)
    {
        if (httpRequest.HasFormContentType)
        {
            var form = await httpRequest.ReadFormAsync(httpRequest.HttpContext.RequestAborted);
            return new SaveChatFormRequest(
                form["title"],
                form["about"],
                ParseBool(form["isPublic"]),
                form["username"],
                ParseNullableInt(form["categoryId"]),
                form.ContainsKey("forwardingAllowed") ? ParseBool(form["forwardingAllowed"]) : null,
                form.Files.GetFile("photo"));
        }

        var request = await JsonSerializer.DeserializeAsync<SaveChatRequestDto>(
                          httpRequest.Body,
                          LocalConfigFile.CreateIndentedJsonSerializerOptions(),
                          httpRequest.HttpContext.RequestAborted)
                      ?? new SaveChatRequestDto(null, null, false, null, null, null);

        return new SaveChatFormRequest(request.Title, request.About, request.IsPublic, request.Username, request.CategoryId, request.ForwardingAllowed, null);
    }

    private static string? NormalizeUsername(string? value)
    {
        var normalized = value?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? ResolveTransferPassword(string? requestPassword, Account executorAccount)
    {
        return NormalizeNullable(requestPassword) ?? NormalizeNullable(executorAccount.TwoFactorPassword);
    }

    private static async Task<(Account? Account, string? Error)> ResolveTargetAccountAsync(int? targetAccountId, string targetUsername, AccountManagementService accountManagement)
    {
        var normalizedTarget = NormalizeUsername(targetUsername);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            return (null, null);

        if (targetAccountId is > 0)
        {
            var selected = await accountManagement.GetAccountAsync(targetAccountId.Value);
            if (selected == null)
                return (null, "选择的新所有者账号不存在");
            if (string.IsNullOrWhiteSpace(NormalizeUsername(selected.Username)))
                return (null, "选择的新所有者账号没有用户名，请先为该账号设置 Telegram 用户名");
            if (!UsernameEquals(selected.Username, normalizedTarget))
                return (null, "选择的新所有者账号与填写的用户名不一致，请重新选择或清空系统账号选择");

            return (selected, null);
        }

        var accounts = await accountManagement.GetAllAccountsAsync();
        return (accounts.FirstOrDefault(x => UsernameEquals(x.Username, normalizedTarget)), null);
    }

    private static bool UsernameEquals(string? accountUsername, string targetUsername)
    {
        var normalized = NormalizeUsername(accountUsername);
        return !string.IsNullOrWhiteSpace(normalized)
            && string.Equals(normalized, targetUsername, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task TryRegisterWebhookForBotAsync(
        string? token,
        TelegramBotApiClient botApi,
        IConfiguration configuration)
    {
        if (!IsWebhookEnabled(configuration))
            return;

        var normalizedToken = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
            return;

        var baseUrl = (configuration["Telegram:WebhookBaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        var secretToken = (configuration["Telegram:WebhookSecretToken"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(secretToken))
            return;

        var pathToken = WebhookTokenHelper.ToWebhookPathToken(normalizedToken);
        var webhookUrl = $"{baseUrl}/api/bot/webhook/{pathToken}";
        await botApi.SetWebhookAsync(
            token: normalizedToken,
            url: webhookUrl,
            secretToken: secretToken,
            allowedUpdates: BotUpdateHub.AllowedUpdatesJson,
            cancellationToken: CancellationToken.None);
    }

    private static async Task TryDeleteWebhookForBotAsync(
        string? token,
        TelegramBotApiClient botApi,
        IConfiguration configuration)
    {
        if (!IsWebhookEnabled(configuration))
            return;

        var normalizedToken = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
            return;

        await botApi.DeleteWebhookAsync(normalizedToken, dropPendingUpdates: false, cancellationToken: CancellationToken.None);
    }

    private static bool IsWebhookEnabled(IConfiguration configuration) =>
        string.Equals(configuration["Telegram:WebhookEnabled"]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    internal static AccountProxyBindingInput? ParseImportProxyBinding(
        string? strategy,
        string? proxyId)
    {
        if (string.IsNullOrWhiteSpace(strategy)
            || string.Equals(
                strategy.Trim(),
                "proxy_per_account",
                StringComparison.OrdinalIgnoreCase))
            return null;

        var parsedProxyId = ParseNullableInt(proxyId);
        return new AccountProxyBindingInput(strategy.Trim(), parsedProxyId);
    }

    private static async Task<int> ResolveLoginIdAsync(
        int requestedLoginId,
        AccountLoginProxyCoordinator loginProxy,
        AccountManagementService accountManagement)
    {
        // 仅允许继续服务端已经登记的登录 ID。客户端自行传入的陌生 ID
        // 可能撞上正式账号客户端池键，因此一律重新生成。
        if (requestedLoginId > 0 && loginProxy.HasState(requestedLoginId))
            return requestedLoginId;

        int loginId;
        do
        {
            loginId = Random.Shared.Next(1, int.MaxValue);
        }
        while (loginProxy.HasState(loginId)
               || await accountManagement.GetAccountAsync(loginId) != null);

        return loginId;
    }

    private static bool IsLoginProxyInputError(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or KeyNotFoundException;

    private static async Task<ImportAccountsResponseDto> BuildImportResponseAsync(
        IEnumerable<ImportResult> results,
        AccountManagementService accountManagement)
    {
        var resultList = results
            .Select(ToDto)
            .ToList();

        var accounts = new List<AccountListItemDto>();
        foreach (var phone in resultList
                     .Where(x => x.Success && !string.IsNullOrWhiteSpace(x.Phone))
                     .Select(x => PhoneNumberFormatter.NormalizeToDigits(x.Phone))
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var account = await accountManagement.GetAccountByPhoneAsync(phone);
            if (account != null)
                accounts.Add(ToDto(account));
        }

        return new ImportAccountsResponseDto(
            resultList,
            accounts
                .GroupBy(x => x.Id)
                .Select(x => x.First())
                .OrderByDescending(x => x.Id)
                .ToList());
    }

    private static ImportResultDto ToDto(ImportResult result) =>
        new(
            result.Success,
            result.Phone,
            result.UserId,
            result.Username,
            result.SessionPath,
            result.Error,
            result.SourceKey,
            result.ProxyLine,
            result.ProxyId,
            result.ProxyName,
            result.ProxyEgressIp);

    private static async Task<IResult> BuildLoginResponseAsync(
        int loginId,
        LoginResult result,
        IAccountService accountService,
        AccountManagementService accountManagement,
        AccountLoginProxyCoordinator loginProxy,
        IConfiguration configuration,
        string? twoFactorPasswordToSave = null,
        CancellationToken cancellationToken = default)
    {
        if (result.Success && result.Account != null)
        {
            if (!loginProxy.HasState(loginId))
            {
                await accountService.ReleaseClientAsync(loginId);
                return Results.BadRequest(new AccountLoginResponseDto(
                    false,
                    loginId,
                    null,
                    "登录代理会话已失效，已阻止账号在未绑定代理时启用，请重新登录",
                    null));
            }

            var deviceProfileKey = loginProxy.GetDeviceProfileKey(loginId);

            Account? account = null;
            try
            {
                account = await SaveLoggedInAccountAsync(
                    result.Account,
                    accountManagement,
                    configuration,
                    twoFactorPasswordToSave,
                    activate: false,
                    deviceProfileKey: deviceProfileKey);
                await loginProxy.CompleteAsync(
                    loginId,
                    account.Id,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                await loginProxy.AbandonAsync(loginId, CancellationToken.None);
                try
                {
                    await accountService.ReleaseClientAsync(loginId);
                    if (account != null && account.Id != loginId)
                        await accountService.ReleaseClientAsync(account.Id);
                }
                catch
                {
                    // 已保持停用，释放失败不覆盖代理绑定错误。
                }

                return Results.BadRequest(new AccountLoginResponseDto(
                    false,
                    loginId,
                    null,
                    $"Telegram 登录成功，但代理绑定失败，账号已保持停用：{ex.Message}",
                    account == null ? null : ToDto(account)));
            }

            try
            {
                await accountService.ReleaseClientAsync(loginId);
                if (account.Id != loginId)
                    await accountService.ReleaseClientAsync(account.Id);
                account = await accountManagement.GetAccountAsync(account.Id) ?? account;
            }
            catch
            {
                // 代理绑定和启用已经提交，清理临时客户端失败不能改写登录结果。
            }

            return Results.Ok(new AccountLoginResponseDto(
                true,
                loginId,
                null,
                result.Message ?? "登录成功",
                ToDto(account)));
        }

        if (result.NextStep is "code" or "password")
        {
            return Results.Ok(new AccountLoginResponseDto(
                false,
                loginId,
                result.NextStep,
                result.Message,
                null));
        }

        if (!string.IsNullOrWhiteSpace(result.NextStep))
        {
            try
            {
                await accountService.ReleaseClientAsync(loginId);
            }
            catch
            {
                // 仍需继续回收登录代理资源。
            }

            await loginProxy.AbandonAsync(loginId, CancellationToken.None);
            return Results.Ok(new AccountLoginResponseDto(
                false,
                0,
                result.NextStep,
                result.Message,
                null));
        }

        if (loginId > 0)
        {
            try
            {
                await accountService.ReleaseClientAsync(loginId);
            }
            catch
            {
                // 忽略释放失败
            }

            await loginProxy.AbandonAsync(loginId, CancellationToken.None);
        }

        return Results.BadRequest(new AccountLoginResponseDto(false, loginId, null, result.Message ?? "登录失败", null));
    }

    private static async Task<IResult> BuildQrLoginResponseAsync(
        QrLoginResult result,
        IAccountService accountService,
        AccountManagementService accountManagement,
        AccountLoginProxyCoordinator loginProxy,
        IConfiguration configuration,
        string? twoFactorPasswordToSave = null,
        CancellationToken cancellationToken = default)
    {
        if (result.Success && result.Account != null)
        {
            if (!loginProxy.HasState(result.LoginId))
            {
                await accountService.ReleaseCompletedQrLoginAsync(result.LoginId);
                return Results.Ok(new AccountQrLoginResponseDto(
                    false,
                    result.LoginId,
                    "failed",
                    "登录代理会话已失效，已阻止账号在未绑定代理时启用，请重新生成二维码",
                    null,
                    result.ExpiresAtUtc,
                    null));
            }

            var deviceProfileKey = loginProxy.GetDeviceProfileKey(result.LoginId);

            Account? account = null;
            try
            {
                account = await SaveLoggedInAccountAsync(
                    result.Account,
                    accountManagement,
                    configuration,
                    twoFactorPasswordToSave,
                    activate: false,
                    deviceProfileKey: deviceProfileKey);
                await loginProxy.CompleteAsync(
                    result.LoginId,
                    account.Id,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                await loginProxy.AbandonAsync(result.LoginId, CancellationToken.None);
                try
                {
                    await accountService.ReleaseCompletedQrLoginAsync(result.LoginId);
                    if (account != null && account.Id != result.LoginId)
                        await accountService.ReleaseClientAsync(account.Id);
                }
                catch
                {
                    // 已保持停用，释放失败不覆盖代理绑定错误。
                }

                return Results.Ok(new AccountQrLoginResponseDto(
                    false,
                    result.LoginId,
                    "failed",
                    $"Telegram 登录成功，但代理绑定失败，账号已保持停用：{ex.Message}",
                    null,
                    result.ExpiresAtUtc,
                    account == null ? null : ToDto(account)));
            }

            try
            {
                await accountService.ReleaseCompletedQrLoginAsync(result.LoginId);
                if (account.Id != result.LoginId)
                    await accountService.ReleaseClientAsync(account.Id);
                account = await accountManagement.GetAccountAsync(account.Id) ?? account;
            }
            catch
            {
                // 代理绑定和启用已经提交，清理临时客户端失败不能改写扫码结果。
            }

            return Results.Ok(new AccountQrLoginResponseDto(
                true,
                result.LoginId,
                "authorized",
                result.Message ?? "扫码登录成功",
                null,
                result.ExpiresAtUtc,
                ToDto(account)));
        }

        if (result.Status is "failed" or "expired")
            await loginProxy.AbandonAsync(result.LoginId, CancellationToken.None);

        return Results.Ok(new AccountQrLoginResponseDto(
            false,
            result.LoginId,
            result.Status,
            result.Message,
            result.QrLoginUrl,
            result.ExpiresAtUtc,
            null));
    }

    private static async Task<Account> SaveLoggedInAccountAsync(
        AccountInfo accountInfo,
        AccountManagementService accountManagement,
        IConfiguration configuration,
        string? twoFactorPasswordToSave = null,
        bool activate = true,
        string? deviceProfileKey = null)
    {
        int apiId;
        string apiHash;
        if (TelegramApiProfilePool.TryNormalizeCredentials(accountInfo.ApiId, accountInfo.ApiHash, out var assignedApiHash, out _))
        {
            apiId = accountInfo.ApiId;
            apiHash = assignedApiHash;
        }
        else if (!TryGetTelegramApi(configuration, out apiId, out apiHash, out var apiError))
        {
            throw new InvalidOperationException(apiError);
        }
        var selectedDeviceProfile = TelegramDeviceProfileCatalog.Find(configuration, deviceProfileKey);
        if (!string.IsNullOrWhiteSpace(deviceProfileKey) && selectedDeviceProfile == null)
            throw new InvalidOperationException("登录设备指纹不存在或已停用");
        var savedDeviceProfileKey = selectedDeviceProfile?.Key ?? TelegramDeviceProfileCatalog.ResolveDefaultKey(configuration);


        var sessionsPath = configuration["Telegram:SessionsPath"] ?? "sessions";
        var phoneDigits = PhoneNumberFormatter.NormalizeToDigits(accountInfo.Phone);
        if (string.IsNullOrWhiteSpace(phoneDigits))
            throw new InvalidOperationException("手机号无效，无法保存账号");

        var normalizedTwoFactorPassword = NormalizeNullable(twoFactorPasswordToSave);
        var existing = await accountManagement.GetAccountByPhoneAsync(phoneDigits);
        if (existing != null)
        {
            existing.SessionPath = Path.Combine(sessionsPath, $"{phoneDigits}.session");
            existing.UserId = accountInfo.TelegramUserId;
            existing.Username = accountInfo.Username;
            existing.Nickname = BuildNickname(accountInfo);
            existing.IsActive = activate;
            existing.ApiId = apiId;
            existing.ApiHash = apiHash;
            existing.DeviceProfileKey = savedDeviceProfileKey;
            existing.TelegramStatusSummary = "正常";
            existing.TelegramStatusDetails = null;
            existing.TelegramStatusOk = true;
            existing.TelegramStatusCheckedAtUtc = DateTime.UtcNow;
            existing.LastSyncAt = DateTime.UtcNow;
            existing.LastLoginAt = DateTime.UtcNow;
            if (normalizedTwoFactorPassword != null)
                existing.TwoFactorPassword = normalizedTwoFactorPassword;
            await accountManagement.UpdateAccountAsync(existing);
            return existing;
        }

        var account = new Account
        {
            Phone = phoneDigits,
            UserId = accountInfo.TelegramUserId,
            Nickname = BuildNickname(accountInfo),
            Username = accountInfo.Username,
            SessionPath = Path.Combine(sessionsPath, $"{phoneDigits}.session"),
            ApiId = apiId,
            ApiHash = apiHash,
            DeviceProfileKey = savedDeviceProfileKey,
            IsActive = activate,
            TwoFactorPassword = normalizedTwoFactorPassword,
            CreatedAt = DateTime.UtcNow,
            LastSyncAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow
        };

        return await accountManagement.CreateAccountAsync(account);
    }

    private static string? BuildNickname(AccountInfo accountInfo)
    {
        var first = accountInfo.FirstName?.Trim();
        var last = accountInfo.LastName?.Trim();
        var display = string.Join(" ", new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(display))
            return display;

        return string.IsNullOrWhiteSpace(accountInfo.Username) ? null : accountInfo.Username.Trim();
    }

    private static void ValidateTaskSubmission(string? taskType, string? config)
    {
        if (string.IsNullOrWhiteSpace(taskType))
            throw new InvalidOperationException("请先选择任务类型");

        if (!string.IsNullOrWhiteSpace(config))
            System.Text.Json.JsonDocument.Parse(config);
    }

    private static IResult? TryNormalizeBatchTaskName(string? value, out string? name)
    {
        name = NormalizeNullable(value);
        return name is { Length: > 100 }
            ? Results.BadRequest(new OperationResultDto(false, "任务名称不能超过 100 个字符"))
            : null;
    }

    private static string? NormalizeNullable(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string NormalizePresetName(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeTaskAssetScopeId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;

        return Regex.Replace(text, "[^A-Za-z0-9_-]", "");
    }

    private static int? ParseNullableInt(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return int.TryParse(text, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static IReadOnlyList<int> ParseIntList(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<int>();

        return text
            .Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var parsed) ? parsed : 0)
            .Where(x => x > 0)
            .Distinct()
            .ToList();
    }

    private static bool TryGetTelegramApi(
        IConfiguration configuration,
        out int apiId,
        out string apiHash,
        out string error)
    {
        if (!TelegramApiProfilePool.TrySelectDefault(configuration, out var credentials, out error))
        {
            apiId = 0;
            apiHash = string.Empty;
            return false;
        }

        apiId = credentials.ApiId;
        apiHash = credentials.ApiHash;
        error = string.Empty;
        return true;
    }

    private static string? NormalizeRemark(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static ChatMembershipTarget ParseChatMembershipTarget(string input, bool treatNoBotSuffixAsBot)
    {
        var raw = (input ?? string.Empty).Trim();
        var isBot = ShouldTreatAsBot(raw, treatNoBotSuffixAsBot);
        var hasBotSuffix = HasBotSuffix(raw);
        var assumeBotUsername = isBot && !hasBotSuffix && treatNoBotSuffixAsBot && !HasStartParameter(raw);

        return new ChatMembershipTarget(raw, raw, isBot, assumeBotUsername);
    }

    private static bool ShouldTreatAsBot(string raw, bool treatNoBotSuffixAsBot)
    {
        if (IsResolveBotLink(raw))
            return true;

        if (HasStartParameter(raw))
            return true;

        if (HasBotSuffix(raw))
            return true;

        return treatNoBotSuffixAsBot && LooksLikeUsernameTarget(raw);
    }

    private static bool IsResolveBotLink(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return value.StartsWith("tg://resolve", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStartParameter(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return value.IndexOf("?start=", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("&start=", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasBotSuffix(string raw)
    {
        var candidate = ExtractUsernameCandidate(raw);
        return candidate.EndsWith("bot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeUsernameTarget(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
            return false;

        if (value.StartsWith("tg://join", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.Contains("joinchat/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.Contains("/+", StringComparison.Ordinal))
            return false;

        var candidate = ExtractUsernameCandidate(value);
        if (candidate.StartsWith("+", StringComparison.Ordinal))
            return false;

        return Regex.IsMatch(candidate, "^[A-Za-z0-9_]{5,64}$");
    }

    private static string ExtractUsernameCandidate(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        if (value.StartsWith("tg://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(value, UriKind.Absolute, out var tgUri))
        {
            var query = ParseQueryString(tgUri.Query);
            if (query.TryGetValue("domain", out var domain) && !string.IsNullOrWhiteSpace(domain))
                value = domain.Trim();
        }
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                 || value.StartsWith("t.me/", StringComparison.OrdinalIgnoreCase)
                 || value.StartsWith("telegram.me/", StringComparison.OrdinalIgnoreCase))
        {
            var url = value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var path = (uri.AbsolutePath ?? string.Empty).Trim('/');
                value = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            }
        }
        else
        {
            value = value.TrimStart('@');
        }

        var q = value.IndexOf('?');
        if (q >= 0)
            value = value[..q];

        var slash = value.IndexOf('/');
        if (slash >= 0)
            value = value[..slash];

        return value.Trim();
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
                var keyOnly = Uri.UnescapeDataString(part).Trim();
                if (!string.IsNullOrWhiteSpace(keyOnly))
                    map[keyOnly] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(part[..idx]).Trim();
            var val = Uri.UnescapeDataString(part[(idx + 1)..]).Trim();
            if (!string.IsNullOrWhiteSpace(key))
                map[key] = val;
        }

        return map;
    }

    private static IReadOnlyList<int> NormalizeIds(IEnumerable<int>? accountIds) =>
        (accountIds ?? Array.Empty<int>())
            .Where(x => x > 0)
            .Distinct()
            .ToList();

    /// <summary>
    /// 将 Telegram/数据库异常转换为前端可直接展示的中文错误，避免只看到 Axios 的 400。
    /// </summary>
    private static string BuildChatOperationError(string operation, Exception ex)
    {
        if (ex is DbUpdateException)
        {
            return $"{operation}后本地数据库保存失败。Telegram 侧可能已经创建成功，请先同步频道/群组列表确认，避免重复创建；数据库恢复可写后再重试。";
        }

        var (summary, details) = AccountTelegramToolsService.MapTelegramException(ex);
        var message = summary;
        if (!string.IsNullOrWhiteSpace(details)
            && !string.Equals(summary, details, StringComparison.OrdinalIgnoreCase))
        {
            message = string.IsNullOrWhiteSpace(summary)
                ? details
                : $"{summary}：{details}";
        }
        if (string.IsNullOrWhiteSpace(message))
            message = ex.Message;

        return string.IsNullOrWhiteSpace(message)
            ? $"{operation}失败"
            : $"{operation}失败：{message}";
    }

    private static IReadOnlyList<string> NormalizeUsernames(IEnumerable<string>? usernames) =>
        (usernames ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x =>
            {
                if (Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Host.EndsWith("t.me", StringComparison.OrdinalIgnoreCase))
                    x = uri.AbsolutePath.Trim('/');
                return x.Trim().TrimStart('@');
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static (long? UserId, string? Username) ParseUserTarget(string? target)
    {
        var text = (target ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);

        if (long.TryParse(text, out var userId) && userId > 0)
            return (userId, null);

        var username = NormalizeUsernames(new[] { text }).FirstOrDefault();
        return string.IsNullOrWhiteSpace(username) ? (null, null) : (null, username);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(x => (x ?? string.Empty).Trim()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private static async Task<string?> WaitTelegramMailCodeAsync(
        CloudMailClient cloudMail,
        string cloudMailBaseUrl,
        string cloudMailToken,
        string toEmail,
        DateTimeOffset startUtc,
        int pollIntervalSeconds,
        int pollTimeoutSeconds,
        string sendEmailFilter,
        string subjectFilter,
        TelegramMailCodePurpose purpose,
        bool allowOlder,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(pollTimeoutSeconds, 10, 300));
        var interval = TimeSpan.FromSeconds(Math.Clamp(pollIntervalSeconds, 2, 30));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var allowOldBeforeUtc = allowOlder ? DateTimeOffset.UtcNow.AddHours(-24) : startUtc.AddMinutes(-10);
        var useFilters = !string.IsNullOrWhiteSpace(sendEmailFilter) || !string.IsNullOrWhiteSpace(subjectFilter);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<CloudMailEmail> emails;
            try
            {
                CloudMailEmailListRequest BuildRequest(string? recipient) => new()
                {
                    ToEmail = string.IsNullOrWhiteSpace(recipient) ? null : recipient,
                    SendEmail = useFilters && !string.IsNullOrWhiteSpace(sendEmailFilter) ? sendEmailFilter.Trim() : null,
                    Subject = useFilters && !string.IsNullOrWhiteSpace(subjectFilter) ? subjectFilter.Trim() : null,
                    TimeSort = "desc",
                    Type = null,
                    IsDel = null,
                    Num = 1,
                    Size = 50
                };

                emails = await cloudMail.GetEmailListAsync(cloudMailBaseUrl, cloudMailToken, BuildRequest(toEmail), cancellationToken);
                if (emails.Count == 0)
                    emails = await cloudMail.GetEmailListAsync(cloudMailBaseUrl, cloudMailToken, BuildRequest("%" + toEmail + "%"), cancellationToken);
                if (useFilters && emails.Count == 0)
                    emails = await cloudMail.GetEmailListAsync(cloudMailBaseUrl, cloudMailToken, BuildRequest(toEmail) with { SendEmail = null, Subject = null }, cancellationToken);
                if (emails.Count == 0)
                    emails = await cloudMail.GetEmailListAsync(cloudMailBaseUrl, cloudMailToken, BuildRequest(null) with { SendEmail = null, Subject = null }, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch
            {
                await Task.Delay(interval, cancellationToken);
                continue;
            }

            var candidates = new List<(DateTimeOffset CreatedUtc, CloudMailEmail Mail)>();
            foreach (var mail in FilterCloudMailRecipients(emails, toEmail))
            {
                var createdUtc = TryParseUtc(mail.CreateTime);
                if (createdUtc.HasValue && createdUtc.Value < allowOldBeforeUtc)
                    continue;

                if (!IsMatchMailPurpose(BuildMergedMailText(mail), purpose))
                    continue;

                candidates.Add((createdUtc ?? DateTimeOffset.MinValue, mail));
            }

            foreach (var candidate in candidates.OrderByDescending(x => x.CreatedUtc))
            {
                var code = TryExtractTelegramMailCode(candidate.Mail);
                if (!string.IsNullOrWhiteSpace(code))
                    return code;
            }

            await Task.Delay(interval, cancellationToken);
        }

        return null;
    }

    private static async Task<string?> BuildCloudMailReceiveDiagAsync(
        CloudMailClient cloudMail,
        string cloudMailBaseUrl,
        string cloudMailToken,
        string toEmail,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new CloudMailEmailListRequest
            {
                ToEmail = null,
                SendEmail = null,
                Subject = null,
                TimeSort = "desc",
                Type = null,
                IsDel = null,
                Num = 1,
                Size = 10
            };

            var all = await cloudMail.GetEmailListAsync(cloudMailBaseUrl, cloudMailToken, request, cancellationToken);
            var matched = FilterCloudMailRecipients(all, toEmail).ToList();
            if (matched.Count > 0)
            {
                var top = matched[0];
                return $"Cloud Mail 已拉到 {matched.Count} 封匹配邮件（最近主题：{(top.Subject ?? string.Empty).Trim()}，时间：{(top.CreateTime ?? string.Empty).Trim()}），但未能解析出验证码";
            }

            var any = all.FirstOrDefault();
            if (any == null)
                return "Cloud Mail emailList 返回空列表（可能是 Token/权限/收件箱同步问题）";

            return $"Cloud Mail 拉到 {all.Count} 封最近邮件，但未匹配到收件人 {toEmail}（最近一封收件人：{(any.ToEmail ?? string.Empty).Trim()}）";
        }
        catch (Exception ex)
        {
            return $"收码诊断失败：{ex.Message}";
        }
    }

    private static IEnumerable<CloudMailEmail> FilterCloudMailRecipients(IEnumerable<CloudMailEmail> emails, string toEmail)
    {
        var target = (toEmail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(target))
            return emails ?? Array.Empty<CloudMailEmail>();

        return (emails ?? Array.Empty<CloudMailEmail>())
            .Where(m =>
            {
                var candidate = (m.ToEmail ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(candidate))
                    return false;

                return string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)
                       || candidate.EndsWith(target, StringComparison.OrdinalIgnoreCase)
                       || candidate.Contains(target, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static bool IsMatchMailPurpose(string merged, TelegramMailCodePurpose purpose)
    {
        if (purpose == TelegramMailCodePurpose.Any)
            return true;

        var lower = (merged ?? string.Empty).ToLowerInvariant();
        if (purpose == TelegramMailCodePurpose.RecoveryEmail)
        {
            return (lower.Contains("recovery") && lower.Contains("email") && lower.Contains("verify"))
                   || lower.Contains("password recovery email");
        }

        if (purpose == TelegramMailCodePurpose.LoginEmail)
        {
            return lower.Contains("verify")
                   && lower.Contains("email")
                   && lower.Contains("login")
                   && !lower.Contains("recovery");
        }

        return true;
    }

    private static DateTimeOffset? TryParseUtc(string? createTime)
    {
        createTime = (createTime ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(createTime))
            return null;

        if (!DateTime.TryParse(createTime, out var dt))
            return null;

        dt = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
        return new DateTimeOffset(dt);
    }

    private static string BuildMergedMailText(CloudMailEmail mail) =>
        $"{mail.Subject ?? string.Empty}\n{mail.Text ?? string.Empty}\n{StripHtml(mail.Content ?? string.Empty)}";

    private static string? TryExtractTelegramMailCode(CloudMailEmail mail)
    {
        var merged = BuildMergedMailText(mail);

        var match = Regex.Match(merged, @"(?i)\byour\s+code\s*[-–—]\s*(\d{5,6})\b");
        if (match.Success)
            return match.Groups[1].Value;

        match = Regex.Match(merged, @"(?i)\byour\s+code\s+is\s*[:：]?\s*(\d{5,6})\b");
        if (match.Success)
            return match.Groups[1].Value;

        match = Regex.Match(merged, @"(?i)\blogin\s+code\s*[:：]?\s*(\d{5,6})\b");
        if (match.Success)
            return match.Groups[1].Value;

        var matches = Regex.Matches(merged, "\\b\\d{5,6}\\b");
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        return Regex.Replace(html, "<.*?>", " ").Replace("&nbsp;", " ").Trim();
    }

    private static async Task<(int? ExecutorId, string? Reason)> ResolveBotChannelInviteExecutorAsync(
        int botId,
        long channelTelegramId,
        int selectedAccountId,
        Account? selectedAccount,
        IReadOnlyDictionary<long, Account> accountsByUserId,
        BotTelegramService botTelegram,
        CancellationToken cancellationToken)
    {
        List<BotTelegramService.BotChatAdminInfo> admins;
        try
        {
            admins = await botTelegram.GetChatAdminsAsync(botId, channelTelegramId, cancellationToken);
        }
        catch (Exception ex)
        {
            return (null, $"无法获取频道管理员列表：{ex.Message}");
        }

        if (admins.Count == 0)
            return (null, "无法获取频道管理员列表（请确认 Bot 已加入且为管理员）");

        if (selectedAccountId > 0)
        {
            if (selectedAccount == null || selectedAccount.UserId <= 0 || !selectedAccount.IsActive || selectedAccount.Category?.ExcludeFromOperations == true)
                return (null, "所选执行账号无效或不可用于批量操作");

            var admin = admins.FirstOrDefault(x => x.UserId == selectedAccount.UserId);
            if (admin == null)
                return (null, "所选执行账号不是该频道管理员");

            if (!admin.IsCreator && !admin.CanInviteUsers)
                return (null, "所选执行账号缺少“邀请用户”权限");

            return (selectedAccount.Id, null);
        }

        var creator = admins.FirstOrDefault(x => x.IsCreator);
        if (creator != null && accountsByUserId.TryGetValue(creator.UserId, out var creatorAccount))
            return (creatorAccount.Id, null);

        foreach (var admin in admins)
        {
            if (!admin.IsCreator && !admin.CanInviteUsers)
                continue;

            if (accountsByUserId.TryGetValue(admin.UserId, out var account))
                return (account.Id, null);
        }

        return (null, "无可用执行账号（需要该频道管理员且拥有“邀请用户”权限，并且在系统中存在）");
    }

    private static async Task<(int? ExecutorId, string? Reason)> ResolveBotChannelBanExecutorAsync(
        int botId,
        long channelTelegramId,
        int selectedAccountId,
        Account? selectedAccount,
        IReadOnlyDictionary<long, Account> accountsByUserId,
        BotTelegramService botTelegram,
        CancellationToken cancellationToken)
    {
        List<BotTelegramService.BotChatAdminInfo> admins;
        try
        {
            admins = await botTelegram.GetChatAdminsAsync(botId, channelTelegramId, cancellationToken);
        }
        catch (Exception ex)
        {
            return (null, $"无法获取频道管理员列表：{ex.Message}");
        }

        if (admins.Count == 0)
            return (null, "无法获取频道管理员列表（请确认 Bot 已加入且为管理员）");

        if (selectedAccountId > 0)
        {
            if (selectedAccount == null || selectedAccount.UserId <= 0 || !selectedAccount.IsActive || selectedAccount.Category?.ExcludeFromOperations == true)
                return (null, "所选执行账号无效或不可用于批量操作");

            var admin = admins.FirstOrDefault(x => x.UserId == selectedAccount.UserId);
            if (admin == null)
                return (null, "所选执行账号不是该频道管理员");

            if (!admin.IsCreator && !admin.CanRestrictMembers)
                return (null, "所选执行账号缺少“封禁成员”权限");

            return (selectedAccount.Id, null);
        }

        var creator = admins.FirstOrDefault(x => x.IsCreator);
        if (creator != null && accountsByUserId.TryGetValue(creator.UserId, out var creatorAccount))
            return (creatorAccount.Id, null);

        foreach (var admin in admins)
        {
            if (!admin.IsCreator && !admin.CanRestrictMembers)
                continue;

            if (accountsByUserId.TryGetValue(admin.UserId, out var account))
                return (account.Id, null);
        }

        return (null, "无可用执行账号（需要该频道管理员且拥有“封禁成员”权限，并且在系统中存在）");
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);

    private static string BuildIndexedValue(string? template, Account? account, int accountId, int index)
    {
        var value = (template ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("模板不能为空");

        var digits = new string((account?.Phone ?? string.Empty).Where(char.IsDigit).ToArray());
        var last4 = digits.Length <= 4 ? digits : digits[^4..];
        return value
            .Replace("{index}", (index + 1).ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{id}", accountId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{phone}", digits, StringComparison.OrdinalIgnoreCase)
            .Replace("{last4}", last4, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static List<string> ParseTemplateLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static bool ValidateTextTemplates(
        IEnumerable<string> templates,
        ISet<string> textDictionaryNames,
        out string? error,
        bool allowBuiltInIndexedTokens = false)
    {
        error = null;
        foreach (var template in templates)
        {
            foreach (System.Text.RegularExpressions.Match token in Regex.Matches(template ?? string.Empty, @"\{(?<name>[a-zA-Z0-9_]+)\}"))
            {
                var tokenName = token.Groups["name"].Value;
                if (string.Equals(tokenName, "time", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (allowBuiltInIndexedTokens && IsBuiltInIndexedToken(tokenName))
                    continue;

                if (!textDictionaryNames.Contains(tokenName))
                {
                    error = $"不支持的文本变量：{{{tokenName}}}。请使用 {{time}} 或已启用的文本字典变量。";
                    if (allowBuiltInIndexedTokens)
                        error = $"不支持的文本变量：{{{tokenName}}}。请使用 {{time}}、{{index}}、{{id}}、{{phone}}、{{last4}} 或已启用的文本字典变量。";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsBuiltInIndexedToken(string tokenName) =>
        string.Equals(tokenName, "index", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tokenName, "id", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tokenName, "phone", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tokenName, "last4", StringComparison.OrdinalIgnoreCase);

    private static string BuildNickname(string baseName, string phone, bool appendLast4)
    {
        var name = (baseName ?? string.Empty).Trim();
        if (!appendLast4)
            return name;

        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            return name;

        var tail = digits.Length <= 4 ? digits : digits[^4..];
        return $"{name}{tail}";
    }

    private static bool TryPrepareUsername(
        string templateResult,
        HashSet<string> reserved,
        out string username,
        out string? error)
    {
        username = (templateResult ?? string.Empty).Trim().TrimStart('@');
        error = null;

        if (!Regex.IsMatch(username, "^[A-Za-z][A-Za-z0-9_]{4,31}$"))
        {
            error = "生成的用户名不符合 Telegram 规则：仅允许字母/数字/下划线，长度 5-32，且必须以字母开头。";
            return false;
        }

        if (!reserved.Add(username))
        {
            error = "本次批量操作生成了重复用户名，请调整文本字典内容或模板。";
            return false;
        }

        return true;
    }

    private static string GetDisplayStatus(BatchTask task)
    {
        if (task.Status == "failed" && task.CompletedAt.HasValue && task.Total > 0 && task.Completed >= task.Total)
            return "completed";

        return task.Status;
    }

    private static bool IsHistoryStatus(string status) =>
        status is "completed" or "failed" or "canceled";

    private enum BatchEmailResultKind
    {
        Success,
        Failed,
        Skipped
    }

    private enum TelegramMailCodePurpose
    {
        Any = 0,
        RecoveryEmail = 1,
        LoginEmail = 2
    }
}

public sealed record LoginRequestDto(string? Username, string? Password);
public sealed record AuthMeDto(bool Authenticated, string? Username, bool MustChangePassword, bool AuthEnabled, string Version);
public sealed record OperationResultDto(bool Success, string? Message, string? Code = null);
public sealed record SystemRestartResultDto(bool Success, string? Message, bool RestartScheduled);
public sealed record PagedResultDto<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record DashboardSummaryDto(
    int AccountCount,
    int ChannelCount,
    int GroupCount,
    int ActiveTaskCount,
    int EnabledScheduledTaskCount,
    int DictionaryCount,
    int NormalAccountCount,
    int LimitedAccountCount,
    int InvalidAccountCount,
    IReadOnlyList<BatchTaskDto> ActiveTasks,
    IReadOnlyList<BatchTaskDto> RecentTasks);

public sealed record AccountCategoryDto(int Id, string Name, string? Color, string? Description, bool ExcludeFromOperations, int AccountCount);
public sealed record AccountListItemDto(
    int Id,
    int DisplayNumber,
    string DisplayPhone,
    string? Nickname,
    string? Username,
    string? Remark,
    long UserId,
    bool IsActive,
    AccountCategoryDto? Category,
    int ChannelCount,
    int GroupCount,
    DateTime? EstimatedRegistrationAt,
    DateTime LastSyncAt,
    string? TelegramStatusSummary,
    string? TelegramStatusDetails,
    bool? TelegramStatusOk,
    DateTime? TelegramStatusCheckedAtUtc,
    bool UseGlobalProxy,
    AccountProxySummaryDto? Proxy);

public sealed record AccountProxySummaryDto(
    int Id,
    string Name,
    string Kind,
    string Protocol,
    string Host,
    int Port,
    string? ResinPlatform,
    bool IsEnabled,
    string TestStatus,
    string? EgressIp);

public sealed record AccountDetailDto(
    int Id,
    int DisplayNumber,
    string DisplayPhone,
    string Phone,
    string? Nickname,
    string? Username,
    string? Remark,
    long UserId,
    string SessionPath,
    string? DeviceProfileKey,
    string? TwoFactorPassword,
    int? CategoryId,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? EstimatedRegistrationAt,
    DateTime LastSyncAt,
    DateTime? LastLoginAt);

public sealed record TelegramSystemMessageDto(int Id, DateTime? DateUtc, string Text);

public sealed record TelegramAuthorizationDto(
    string Hash,
    bool Current,
    int ApiId,
    string? AppName,
    string? AppVersion,
    string? DeviceModel,
    string? Platform,
    string? SystemVersion,
    string? Ip,
    string? Country,
    string? Region,
    DateTime? CreatedAtUtc,
    DateTime? LastActiveAtUtc,
    string Title,
    string ApiFamily,
    string ApiDisplayName,
    string ApiDescription,
    string DeviceDisplayName);

public sealed record AccountChannelMembershipDto(
    int Id,
    string Title,
    string? Username,
    bool IsCreator,
    bool IsAdmin,
    string? CategoryName,
    int MemberCount,
    DateTime SyncedAt);

public sealed record AccountGroupMembershipDto(
    int Id,
    string Title,
    string? Username,
    bool IsCreator,
    bool IsAdmin,
    string? CategoryName,
    int MemberCount,
    DateTime SyncedAt);

public sealed record AccountOperationItemDto(
    int AccountId,
    string? Phone,
    bool Success,
    string Summary,
    string? Error);

public sealed record AccountBatchOperationResultDto(
    int Success,
    int Failed,
    IReadOnlyList<AccountOperationItemDto> Items);

public sealed record InviteExecuteAccountScope(
    int PrimaryAccountId,
    int? CategoryId,
    string ScopeName,
    List<int> AccountIds);

public sealed record RiskAccountDto(
    int Id,
    int DisplayNumber,
    string DisplayPhone,
    double? RiskReferenceHours,
    bool IsEstimated);

public sealed record ChatMembershipRiskResultDto(
    bool HasRiskyAccounts,
    int TotalCount,
    int RiskyCount,
    int SafeCount,
    string Message,
    string DetailedMessage,
    IReadOnlyList<RiskAccountDto> RiskyAccounts,
    IReadOnlyList<int> SafeAccountIds);

public sealed record ChatMembershipTarget(string Raw, string Input, bool IsBot, bool AssumeBotUsername);

public sealed record CleanupWasteResultDto(
    int Deleted,
    int Skipped,
    int Failed,
    IReadOnlyList<AccountOperationItemDto> Items);

public sealed record TwoFactorRecoveryEmailStatusDto(
    bool Success,
    string? Error,
    bool HasTwoFactorPassword,
    bool HasRecoveryEmail,
    string? UnconfirmedEmailPattern);

public sealed record LoginEmailStatusDto(
    bool Success,
    string? Error,
    bool HasLoginEmail,
    string? LoginEmailPattern);

public sealed record EmailOperationResultDto(bool Success, string? Error, string? EmailPattern);
public sealed record ImportResultDto(
    bool Success,
    string? Phone,
    long? UserId,
    string? Username,
    string? SessionPath,
    string? Error,
    string? SourceKey = null,
    int? ProxyLine = null,
    int? ProxyId = null,
    string? ProxyName = null,
    string? ProxyEgressIp = null);
public sealed record ImportAccountsResponseDto(
    IReadOnlyList<ImportResultDto> Results,
    IReadOnlyList<AccountListItemDto> Accounts);
public sealed record AccountLoginResponseDto(
    bool Success,
    int LoginId,
    string? NextStep,
    string? Message,
    AccountListItemDto? Account);
public sealed record AccountQrLoginResponseDto(
    bool Success,
    int LoginId,
    string Status,
    string? Message,
    string? QrLoginUrl,
    DateTimeOffset? ExpiresAtUtc,
    AccountListItemDto? Account);

public sealed record SetActiveRequestDto(bool IsActive);
public sealed record SetEnabledRequestDto(bool IsEnabled);
public sealed record SaveAccountCategoryRequestDto(string? Name, string? Color, string? Description, bool ExcludeFromOperations);
public sealed record SaveCategoryAssignmentsRequestDto(IReadOnlyList<int> AccountIds);
public sealed record UpdateAccountRequestDto(string? Remark, string? TwoFactorPassword, int? CategoryId, string? DeviceProfileKey);
public sealed record BatchAccountIdsRequestDto(IReadOnlyList<int> AccountIds, bool? ProbeCreateChannel = null);
public sealed record RefreshTelegramStatusRequestDto(bool? ProbeCreateChannel);
public sealed record BatchSetAccountCategoryRequestDto(IReadOnlyList<int> AccountIds, int? CategoryId);
public sealed record CleanupWasteAccountsRequestDto(
    string Scope,
    IReadOnlyList<int>? AccountIds,
    int? CategoryId,
    string? Search,
    bool? ProbeCreateChannel);
public sealed record ChatMembershipRiskRequestDto(IReadOnlyList<int> AccountIds);
public sealed record ChatMembershipRequestDto(
    IReadOnlyList<int> AccountIds,
    string Operation,
    IReadOnlyList<string> Links,
    bool? TreatNoBotSuffixAsBot,
    int? DelayMs);
public sealed record BatchUpdateProfileRequestDto(
    IReadOnlyList<int> AccountIds,
    string Mode,
    string? Nickname,
    string? Bio,
    string? UsernameTemplate,
    IReadOnlyList<string>? NicknameTemplates,
    bool? AppendPhoneLast4WhenDuplicate);
public sealed record ChangeTwoFactorPasswordRequestDto(
    IReadOnlyList<int> AccountIds,
    string? CurrentPassword,
    string? NewPassword,
    string? Hint,
    bool? UseStoredPasswords,
    bool? SaveNewPasswordToDb);
public sealed record ResetTwoFactorPasswordRequestDto(IReadOnlyList<int> AccountIds);
public sealed record SetTwoFactorRecoveryEmailRequestDto(string? CurrentPassword, string? Email);
public sealed record SetLoginEmailRequestDto(string? Email);
public sealed record ConfirmEmailCodeRequestDto(string? Code);
public sealed record BatchChangeRecoveryEmailRequestDto(
    IReadOnlyList<int> AccountIds,
    string? CloudMailBaseUrl,
    string? CloudMailToken,
    string? Domain,
    bool? ChangeLoginEmail,
    bool? TrySetLoginEmailWhenMissing,
    bool? UseStoredPasswords,
    string? CurrentPassword,
    bool? AutoConfirm,
    int? PollIntervalSeconds,
    int? PollTimeoutSeconds,
    string? SendEmailFilter,
    string? SubjectFilter);
public sealed record ImportStringSessionRequestDto(
    string? SessionString,
    int? CategoryId,
    string? ProxyStrategy,
    int? ProxyId,
    string? DeviceProfileKey);
public sealed record StartAccountLoginRequestDto(
    string? Phone,
    int LoginId = 0,
    string? ProxyStrategy = null,
    int? ProxyId = null,
    string? DeviceProfileKey = null);
public sealed record StartAccountQrLoginRequestDto(
    int LoginId = 0,
    string? ProxyStrategy = null,
    int? ProxyId = null,
    string? DeviceProfileKey = null);
public sealed record AccountLoginSessionRequestDto(int LoginId);
public sealed record AccountLoginCodeRequestDto(int LoginId, string? Code);
public sealed record AccountLoginPasswordRequestDto(int LoginId, string? Password, bool? SaveTwoFactorPassword = null);
public sealed record CreateTaskRequestDto(string TaskType, int Total, string? Config, string? Name = null);
public sealed record UpdateTaskRequestDto(string TaskType, int Total, string? Config, string? Name = null);
public sealed record CreateScheduledTaskRequestDto(
    string TaskType,
    int Total,
    string? ConfigJson,
    string CronExpression,
    string? Status,
    string? Name = null);
public sealed record UpdateScheduledTaskRequestDto(
    string TaskType,
    int Total,
    string? ConfigJson,
    string CronExpression,
    string? Status,
    string? Name = null);
public sealed record CleanupTasksRequestDto(string Mode);
public sealed record TaskAssetUploadResultDto(string AssetPath, string FileName, string ScopeId);
public sealed record SaveTextDictionaryRequestDto(
    int? Id,
    string Name,
    string DisplayName,
    string? Description,
    string ReadMode,
    bool IsEnabled,
    IReadOnlyList<string> Items);
public sealed record TelegramStatusDto(bool Ok, string Summary, string? Details, DateTime CheckedAtUtc);

public sealed record BatchTaskDto(
    int Id,
    string? Name,
    string TaskType,
    string Status,
    int Total,
    int Completed,
    int Failed,
    string? Config,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed record ScheduledTaskDto(
    int Id,
    string Name,
    string TaskType,
    string Status,
    int Total,
    string? ConfigJson,
    string CronExpression,
    DateTime? NextRunAtUtc,
    DateTime? LastRunAtUtc,
    int? LastBatchTaskId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record TaskDefinitionDto(
    string TaskType,
    string DisplayName,
    string Category,
    string? Description,
    string Icon,
    string? CreateRoute,
    bool CanCreate,
    bool CanPause,
    bool CanResume,
    bool CanEdit,
    bool CanRerun,
    bool AutoPauseBeforeEdit);

public sealed record TaskCenterDto(
    IReadOnlyList<BatchTaskDto> Tasks,
    IReadOnlyList<ScheduledTaskDto> ScheduledTasks,
    IReadOnlyList<TaskDefinitionDto> Definitions,
    string TimeZoneId);

public sealed record ModuleNavItemDto(
    string Title,
    string Href,
    string? Icon,
    string? Group,
    int Order,
    string ModuleId,
    string? PageKey,
    string UiMode);

public sealed record DataDictionaryDto(
    int Id,
    string Name,
    string DisplayName,
    string? Description,
    string Type,
    string ReadMode,
    bool IsEnabled,
    int NextIndex,
    int EnabledItemCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<DataDictionaryItemDto> Items);

public sealed record DataDictionaryItemDto(
    int Id,
    string? TextValue,
    string? AssetPath,
    string? FileName,
    int SortOrder,
    bool IsEnabled,
    DateTime CreatedAt);

public sealed record ModuleCenterDto(
    IReadOnlyList<ModuleOverviewDto> Modules,
    IReadOnlyList<string> Diagnostics);

public sealed record ModuleOverviewDto(
    string Id,
    bool Enabled,
    string? ActiveVersion,
    string? LastGoodVersion,
    IReadOnlyList<string> InstalledVersions,
    ModuleManifestDto? Manifest,
    string? ManifestError,
    bool BuiltIn);

public sealed record ModuleManifestDto(
    string Id,
    string Name,
    string Version,
    string? HostMin,
    string? HostMax,
    IReadOnlyList<ModuleDependencyDto> Dependencies,
    string? EntryAssembly,
    string? EntryType);

public sealed record ModuleDependencyDto(string Id, string Range);
public sealed record ModuleOperationResultDto(bool Success, string? Message, string? ModuleId, string? Version);
public sealed record ModuleActionRequestDto(bool AutoRestart);

public sealed record ExternalApiCenterDto(
    IReadOnlyList<ExternalApiDefinitionDto> Apis,
    IReadOnlyList<ExternalApiTypeDto> Types,
    IReadOnlyList<string> AvailableTypes);

public sealed record ExternalApiDefinitionDto(
    string Id,
    string Name,
    string Type,
    string TypeName,
    string? Route,
    bool TypeAvailable,
    bool Enabled,
    string ApiKey,
    JsonObject Config,
    KickApiDto? Kick);

public sealed record ExternalApiTypeDto(
    string Type,
    string DisplayName,
    string Route,
    string? Description,
    int Order);

public sealed record SettingsDto(
    string LocalConfigPath,
    bool LocalConfigExists,
    TelegramApiSettingsDto Telegram,
    GlobalProxySettingsDto GlobalProxy,
    CloudMailSettingsDto CloudMail,
    AiSettingsDto Ai,
    BatchSettingsDto Batch,
    SyncSettingsDto Sync,
    BotAutoSyncSettingsDto BotAutoSync,
    TelegramStatusAutoRefreshSettingsDto TelegramStatus,
    LoggingSettingsDto Logging,
    TimeZoneSettingsDto TimeZone,
    SystemInfoSettingsDto System);

public sealed record VersionInfoDto(
    bool Success,
    string? Error,
    bool Enabled,
    string CurrentVersion,
    string? LatestVersion,
    string? LatestTag,
    bool UpdateAvailable,
    string? Url,
    DateTimeOffset? PublishedAt,
    string? Notes,
    DateTimeOffset CheckedAtUtc,
    bool IsDocker,
    string UpdateMode,
    bool CanApply,
    string? BlockedReason,
    string? AssetName,
    long? AssetSizeBytes);

public sealed record VersionApplyResultDto(
    bool Success,
    string Message,
    bool RestartScheduled,
    string? LatestTag,
    string? LatestVersion);

public sealed record UpdateModeRequestDto(string? Mode);
public sealed record UpdateModeResultDto(bool Success, string Mode, string Message);

public sealed record TelegramApiSettingsDto(
    string ApiId,
    string ApiHash,
    IReadOnlyList<TelegramApiProfileDto>? Profiles = null,
    bool OfficialApiEnabled = true,
    IReadOnlyList<TelegramDeviceProfileDto>? DeviceProfiles = null,
    string? DefaultDeviceProfileKey = null,
    string? EffectiveApiId = null,
    string? EffectiveApiSource = null,
    string? EffectiveApiName = null,
    string? OfficialApiId = null,
    string? OfficialApiName = null,
    bool HasUsableApi = true);
public sealed record TelegramApiProfileDto(string? Name, string? ApiId, string? ApiHash, bool Enabled = true, int Weight = 1, string? Notes = null);
public sealed record TelegramDeviceProfileDto(
    string Key,
    string Name,
    string Family,
    string AppVersion,
    string DeviceModel,
    string SystemVersion,
    string SystemLangCode,
    string LangCode,
    bool BuiltIn,
    string? Notes);
public sealed record GlobalProxySettingsDto(
    bool Enabled,
    string? Protocol,
    string? Server,
    int Port,
    string? Username,
    bool HasPassword,
    bool HasSecret,
    string SourceMode = GlobalTelegramProxyConfiguration.ManualSourceMode,
    int? ProxyId = null,
    string? ProxyName = null,
    string? ProxyKind = null);
public sealed record SaveGlobalProxySettingsRequestDto(
    bool Enabled,
    string? Protocol,
    string? Server,
    int Port,
    string? Username,
    string? Password,
    string? Secret,
    bool ClearPassword = false,
    string? SourceMode = null,
    int? ProxyId = null);
public sealed record CloudMailSettingsDto(string BaseUrl, string Domain, string Token);
public sealed record GenerateCloudMailTokenRequestDto(string? BaseUrl, string? AdminEmail, string? AdminPassword);
public sealed record CloudMailTokenResultDto(string Token);
public sealed record AiSettingsDto(string Endpoint, string ApiKey, string DefaultModel, IReadOnlyList<string> PresetModels, int RetryCount);
public sealed record AiTestResultDto(bool Success, string? Model, string? ResponseText, string? Error);
public sealed record BatchSettingsDto(int DefaultDelayMs, int MaxConcurrent, int HistoryRetentionLimit, bool AutoRetry, int MaxRetries);
public sealed record SyncSettingsDto(bool AutoSyncEnabled, int IntervalHours);
public sealed record BotAutoSyncSettingsDto(bool Enabled, int IntervalSeconds);
public sealed record TelegramStatusAutoRefreshSettingsDto(bool Enabled, int IntervalMinutes, int BatchSize, int MinAgeMinutes, int DelayMs);
public sealed record LoggingSettingsDto(bool Enabled, string Level, int RetentionDays);
public sealed record TimeZoneSettingsDto(string TimeZoneId, string? EffectiveHint = null);
public sealed record SystemInfoSettingsDto(string Version, string Runtime, string Database, string? EffectiveApiId);
public sealed record ChangeAdminPasswordRequestDto(string? CurrentPassword, string? NewPassword);
public sealed record ChangeAdminUsernameRequestDto(string? CurrentPassword, string? NewUsername);
public sealed record VerifyAdminPasswordRequestDto(string? Password);

public sealed record SaveExternalApiRequestDto(
    string? Id,
    string? Name,
    string? Type,
    bool Enabled,
    string? ApiKey,
    JsonObject? Config,
    KickApiDto? Kick);

public sealed record KickApiDto(
    int BotId,
    bool UseAllChats,
    IReadOnlyList<long> ChatIds,
    bool PermanentBanDefault);

public sealed record BotOptionDto(
    int Id,
    string Name,
    string? Username,
    bool IsActive);

public sealed record BotChatOptionDto(
    int Id,
    long TelegramId,
    string Title,
    string? Username,
    bool IsBroadcast,
    int MemberCount);

public sealed record OperationAccountDto(
    int Id,
    int DisplayNumber,
    string DisplayPhone,
    string? Nickname,
    string? Username,
    bool IsActive,
    int? CategoryId,
    string? CategoryName);

public sealed record SimpleCategoryDto(
    int Id,
    string Name,
    string? Description,
    int ItemCount,
    DateTime CreatedAt);

public sealed record ChatMembershipAccountDto(
    int AccountId,
    string? DisplayPhone,
    bool IsCreator,
    bool IsAdmin,
    DateTime SyncedAt);

public sealed record ChannelListItemDto(
    int Id,
    long TelegramId,
    string Title,
    string? Username,
    int MemberCount,
    string? About,
    int? CreatorAccountId,
    string? CreatorDisplayPhone,
    int? GroupId,
    string? GroupName,
    DateTime? CreatedAt,
    DateTime? SystemCreatedAtUtc,
    DateTime SyncedAt,
    IReadOnlyList<ChatMembershipAccountDto> Accounts,
    string? Warning = null);

public sealed record GroupListItemDto(
    int Id,
    long TelegramId,
    string Title,
    string? Username,
    int MemberCount,
    string? About,
    int? CreatorAccountId,
    string? CreatorDisplayPhone,
    int? CategoryId,
    string? CategoryName,
    DateTime? CreatedAt,
    DateTime? SystemCreatedAtUtc,
    DateTime SyncedAt,
    IReadOnlyList<ChatMembershipAccountDto> Accounts,
    string? Warning = null);

public sealed record ChannelDetailDto(ChannelListItemDto Channel, IReadOnlyList<ChatMembershipAccountDto> Accounts);
public sealed record GroupDetailDto(GroupListItemDto Group, IReadOnlyList<ChatMembershipAccountDto> Accounts);

public sealed record ChatAdminDto(
    long UserId,
    string? Username,
    string DisplayName,
    bool IsCreator,
    string? Rank,
    string? Status,
    bool? CanInviteUsers,
    bool? CanPromoteMembers,
    bool? CanRestrictMembers);

public sealed record CreateChannelRequestDto(
    int AccountId,
    int? GroupId,
    string? Title,
    string? About,
    bool IsPublic,
    string? Username,
    bool AllowForwarding,
    bool IgnoreRiskWarning);

public sealed record CreateGroupRequestDto(
    int AccountId,
    int? CategoryId,
    string? Title,
    string? About,
    bool IsPublic,
    string? Username,
    bool IgnoreRiskWarning);

public sealed record SaveChatRequestDto(
    string? Title,
    string? About,
    bool IsPublic,
    string? Username,
    int? CategoryId,
    bool? ForwardingAllowed);

public sealed record SaveChatFormRequest(
    string? Title,
    string? About,
    bool IsPublic,
    string? Username,
    int? CategoryId,
    bool? ForwardingAllowed,
    IFormFile? Photo);

public sealed record SetCategoryRequestDto(int? CategoryId);
public sealed record BatchIdsRequestDto(IReadOnlyList<int> Ids);
public sealed record BatchBotChannelDeleteRequestDto(IReadOnlyList<int> Ids, IReadOnlyList<int>? BotIds);
public sealed record BatchSetCategoryRequestDto(IReadOnlyList<int> Ids, int? CategoryId);
public sealed record ChannelUserBatchRequestDto(
    IReadOnlyList<int> Ids,
    IReadOnlyList<string> Usernames,
    int? AccountId,
    int? AccountCategoryId,
    int? DelayMs);
public sealed record ChannelAdminBatchRequestDto(
    IReadOnlyList<int> Ids,
    IReadOnlyList<string> Usernames,
    int? AccountId,
    int Rights,
    string? AdminTitle,
    int? DelayMs);
public sealed record ChannelKickBatchRequestDto(
    IReadOnlyList<int> Ids,
    string? Target,
    int? AccountId,
    bool PermanentBan);
public sealed record TransferOwnerRequestDto(string? Target, string? Password, int? AccountId, int? TargetAccountId);
public sealed record SaveSimpleCategoryRequestDto(string? Name, string? Description);
public sealed record SaveResourceAssignmentsRequestDto(IReadOnlyList<int> ScopeIds, IReadOnlyList<int> SelectedIds);
public sealed record SyncChatsRequestDto(int? AccountId);
public sealed record SyncFailureDto(int AccountId, string Phone, string Error);
public sealed record SyncResultDto(
    int? TaskId,
    int TotalAccounts,
    int ProcessedAccounts,
    int TotalChannelsSynced,
    int TotalGroupsSynced,
    IReadOnlyList<SyncFailureDto> Failures,
    string Message);

public sealed record LinkResultDto(string Link);

public sealed record BotManagementDto(
    int Id,
    string Name,
    string? Username,
    bool IsActive,
    int ChannelCount,
    DateTime CreatedAt,
    DateTime? LastSyncAt);

public sealed record SaveBotRequestDto(string? Name, string? Token, string? Username);

public sealed record BotChannelListItemDto(
    int Id,
    long TelegramId,
    string Title,
    string? Username,
    bool IsBroadcast,
    int MemberCount,
    string? About,
    int? CategoryId,
    string? CategoryName,
    bool? ChannelStatusOk,
    DateTime? ChannelStatusCheckedAtUtc,
    string? ChannelStatusError,
    DateTime? CreatedAt,
    DateTime SyncedAt,
    IReadOnlyList<BotBindingDto> BoundBots);

public sealed record BotBindingDto(int Id, string Name, string? Username);
public sealed record BotChannelRemoteInfoDto(
    long TelegramId,
    string Type,
    string? Title,
    string? Username,
    string? Description,
    int? MemberCount);
public sealed record BotChannelDetailDto(BotChannelListItemDto Channel, BotChannelRemoteInfoDto? RemoteInfo);

public sealed record BotChannelIdsRequestDto(int BotId, IReadOnlyList<int> Ids);
public sealed record BotChannelBanRequestDto(
    int BotId,
    IReadOnlyList<int> Ids,
    string? Target,
    bool PermanentBan,
    bool UseAccountExecution,
    int SelectedAccountId);
public sealed record BotChannelInviteRequestDto(
    int BotId,
    IReadOnlyList<int> Ids,
    IReadOnlyList<string> Usernames,
    int SelectedAccountId,
    int? AccountCategoryId,
    int? DelayMs);
public sealed record BotAdminsByAccountTaskRequestDto(
    int BotId,
    IReadOnlyList<int> Ids,
    int SelectedAccountId,
    IReadOnlyList<string> Usernames,
    int Rights,
    string? AdminTitle,
    int? DelayMs);
public sealed record BotAdminsByBotTaskRequestDto(
    int BotId,
    IReadOnlyList<int> Ids,
    IReadOnlyList<long> UserIds,
    BotSetAdminsRightsPayload? Rights);
public sealed record TextPresetDto(string Name, IReadOnlyList<string> Values);
public sealed record NumberPresetDto(string Name, IReadOnlyList<long> Values);
public sealed record SaveTextPresetRequestDto(string? Name, IReadOnlyList<string>? Values);
public sealed record SaveNumberPresetRequestDto(string? Name, IReadOnlyList<long>? Values);
public sealed record ChannelAdminDefaultsDto(int Rights);
public sealed record BotChannelStatusFailureDto(long TelegramId, string Error);
public sealed record BotChannelStatusResultDto(
    int SuccessCount,
    int FailedCount,
    int TotalCount,
    IReadOnlyList<BotChannelStatusFailureDto> Failures);

public sealed record SyncBotChannelsRequestDto(int BotId);
