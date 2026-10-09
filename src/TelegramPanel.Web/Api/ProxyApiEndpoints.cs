using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;
using TelegramPanel.Web.Services;

namespace TelegramPanel.Web.Api;

public static class ProxyApiEndpoints
{
    public static RouteGroupBuilder MapProxyManagementApi(this RouteGroupBuilder group)
    {
        group.MapWgcfWarpApi();
        group.MapGet("/network/egress", ProbePanelEgressAsync);

        group.MapGet("/proxies", ListAsync);
        group.MapGet("/proxy-categories", ListCategoriesAsync);
        group.MapPost("/proxy-categories", CreateCategoryAsync);
        group.MapPut("/proxy-categories/{id:int}", UpdateCategoryAsync);
        group.MapDelete("/proxy-categories/{id:int}", DeleteCategoryAsync);
        group.MapGet("/proxies/{id:int}", GetAsync);
        group.MapPost("/proxies", CreateAsync);
        group.MapPut("/proxies/{id:int}", UpdateAsync);
        group.MapDelete("/proxies/{id:int}", DeleteAsync);
        group.MapPost("/proxies/{id:int}/test", TestAsync);
        group.MapPost("/proxies/import", ImportAsync);
        group.MapPost("/proxies/batch/category", BatchSetCategoryAsync);
        group.MapPost("/proxies/batch/enabled", BatchSetEnabledAsync);
        group.MapPost("/proxies/batch/test", BatchTestAsync);
        group.MapPost("/proxies/batch/delete", BatchDeleteAsync);
        group.MapGet("/proxies/warp/status", GetWarpStatusAsync);
        group.MapPost("/proxies/warp", CreateWarpAsync);
        group.MapPost("/proxies/{id:int}/warp/refresh", RefreshWarpAsync);
        group.MapPost("/proxies/warp/refresh-all", RefreshAllWarpAsync);

        group.MapPost("/accounts/{id:int}/proxy", BindAccountAsync);
        group.MapPost("/accounts/batch/proxy", BindAccountsAsync);
        group.MapGet("/accounts/{id:int}/proxy/egress", ProbeAccountEgressAsync);
        return group;
    }

    private static async Task<IResult> ProbePanelEgressAsync(
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ProbePanelAsync(cancellationToken);
        return Results.Ok(ToDto(result, "panel_direct"));
    }

    internal static async Task<IResult> ListAsync(
        string? usage,
        int? categoryId,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxies = await service.ListAsync(usage, categoryId, cancellationToken);
            var globalId = service.GetEnabledGlobalProxyId();
            var globalAccountCount = await service.GetGlobalFallbackAccountCountAsync(cancellationToken);
            return Results.Ok(proxies.Select(proxy => ToDto(
                proxy,
                proxy.Id == globalId,
                globalAccountCount)).ToList());
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> GetAsync(
        int id,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        var proxy = await service.GetAsync(id, includeAccounts: true, cancellationToken);
        return proxy == null
            ? Results.NotFound(new OperationResultDto(false, "代理不存在"))
            : Results.Ok(ToDto(
                proxy,
                proxy.Id == service.GetEnabledGlobalProxyId(),
                await service.GetGlobalFallbackAccountCountAsync(cancellationToken)));
    }

    private static async Task<IResult> CreateAsync(
        ProxySaveRequestDto request,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxy = await service.CreateAsync(ToInput(request), cancellationToken);
            return Results.Ok(ToDto(proxy));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        ProxySaveRequestDto request,
        ProxyManagementService service,
        IWarpProxyUsageGuard warpProxyUsageGuard,
        TemporaryWarpClaimStore temporaryWarpClaims,
        CancellationToken cancellationToken)
    {
        try
        {
            using var usageLease = await AcquireWarpMutationLeaseAsync(
                id,
                service,
                warpProxyUsageGuard,
                temporaryWarpClaims,
                cancellationToken);
            var proxy = await service.UpdateAsync(id, ToInput(request), cancellationToken);
            return Results.Ok(ToDto(proxy));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> DeleteAsync(
        int id,
        ProxyManagementService service,
        IWarpProxyUsageGuard warpProxyUsageGuard,
        TemporaryWarpClaimStore temporaryWarpClaims,
        CancellationToken cancellationToken)
    {
        try
        {
            using var usageLease = await AcquireWarpMutationLeaseAsync(
                id,
                service,
                warpProxyUsageGuard,
                temporaryWarpClaims,
                cancellationToken);
            await service.DeleteAsync(id, cancellationToken);
            return Results.Ok(new OperationResultDto(true, "代理已删除"));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> TestAsync(
        int id,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxy = await service.TestAsync(id, cancellationToken);
            return Results.Ok(ToDto(proxy));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> ImportAsync(
        ProxyImportRequestDto request,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxies = await service.ImportAsync(
                request.Text,
                request.TestAfterImport,
                cancellationToken);
            return Results.Ok(proxies.Select(proxy => ToDto(proxy)).ToList());
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> GetWarpStatusAsync(
        ProxyManagementService service,
        IConfiguration configuration,
        WarpMaintenanceState maintenanceState,
        CancellationToken cancellationToken)
    {
        var status = await service.GetWarpStatusAsync(cancellationToken);
        var options = WarpMaintenanceOptions.From(configuration);
        return Results.Ok(status with
        {
            Maintenance = maintenanceState.Snapshot(options)
        });
    }

    private static async Task<IResult> ListCategoriesAsync(
        ProxyManagementService service,
        CancellationToken cancellationToken) =>
        Results.Ok((await service.ListCategoriesAsync(cancellationToken))
            .Select(ToDto)
            .ToList());

    private static async Task<IResult> CreateCategoryAsync(
        ProxyCategorySaveRequestDto request,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(ToDto(await service.CreateCategoryAsync(
                new ProxyCategoryInput(request.Name, request.Color, request.Description),
                cancellationToken)));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> UpdateCategoryAsync(
        int id,
        ProxyCategorySaveRequestDto request,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(ToDto(await service.UpdateCategoryAsync(
                id,
                new ProxyCategoryInput(request.Name, request.Color, request.Description),
                cancellationToken)));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> DeleteCategoryAsync(
        int id,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.DeleteCategoryAsync(id, cancellationToken);
            return Results.Ok(new OperationResultDto(true, "代理分类已删除"));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> BatchSetCategoryAsync(
        ProxyBatchCategoryRequestDto request,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var count = await service.SetCategoriesAsync(
                request.ProxyIds ?? Array.Empty<int>(),
                request.CategoryId is > 0 ? request.CategoryId : null,
                cancellationToken);
            return Results.Ok(new OperationResultDto(true, $"已更新 {count} 个代理的分类"));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    internal static async Task<IResult> BatchSetEnabledAsync(
        ProxyBatchEnabledRequestDto request,
        ProxyManagementService service,
        IWarpProxyUsageGuard warpProxyUsageGuard,
        TemporaryWarpClaimStore temporaryWarpClaims,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = ProxyManagementService.NormalizeProxyIds(
                request.ProxyIds ?? Array.Empty<int>());
            var items = new List<ProxyBatchItemDto>(ids.Length);
            foreach (var id in ids)
            {
                try
                {
                    using var usageLease = await AcquireWarpMutationLeaseAsync(
                        id,
                        service,
                        warpProxyUsageGuard,
                        temporaryWarpClaims,
                        cancellationToken);
                    var proxy = await service.GetAsync(id, cancellationToken: cancellationToken)
                        ?? throw new KeyNotFoundException("代理不存在");
                    _ = await service.UpdateAsync(
                        id,
                        new OutboundProxyInput(
                            proxy.Name,
                            proxy.Kind,
                            proxy.Protocol,
                            proxy.Host,
                            proxy.Port,
                            proxy.Username,
                            null,
                            proxy.Secret,
                            proxy.ResinPlatform,
                            proxy.ResinAdminUrl,
                            null,
                            request.IsEnabled,
                            false,
                            false,
                            false,
                            proxy.CategoryId),
                        cancellationToken);
                    items.Add(new ProxyBatchItemDto(
                        id,
                        true,
                        request.IsEnabled ? "已启用" : "已停用",
                        null));
                }
                catch (Exception ex) when (IsClientError(ex))
                {
                    items.Add(new ProxyBatchItemDto(id, false, "操作失败", ex.Message));
                }
            }
            return Results.Ok(ToBatchResult(items));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    internal static async Task<IResult> BatchTestAsync(
        ProxyBatchIdsRequestDto request,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = ProxyManagementService.NormalizeProxyIds(
                request.ProxyIds ?? Array.Empty<int>());
            var items = new List<ProxyBatchItemDto>(ids.Length);
            foreach (var id in ids)
            {
                try
                {
                    var proxy = await service.TestAsync(id, cancellationToken);
                    items.Add(new ProxyBatchItemDto(
                        id,
                        proxy.TestStatus == "ok",
                        "检测完成",
                        proxy.LastError));
                }
                catch (Exception ex) when (IsClientError(ex))
                {
                    items.Add(new ProxyBatchItemDto(id, false, "检测失败", ex.Message));
                }
            }
            return Results.Ok(ToBatchResult(items));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    internal static async Task<IResult> BatchDeleteAsync(
        ProxyBatchIdsRequestDto request,
        ProxyManagementService service,
        IWarpProxyUsageGuard warpProxyUsageGuard,
        TemporaryWarpClaimStore temporaryWarpClaims,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = ProxyManagementService.NormalizeProxyIds(
                request.ProxyIds ?? Array.Empty<int>());
            var items = new List<ProxyBatchItemDto>(ids.Length);
            foreach (var id in ids)
            {
                try
                {
                    using var usageLease = await AcquireWarpMutationLeaseAsync(
                        id,
                        service,
                        warpProxyUsageGuard,
                        temporaryWarpClaims,
                        cancellationToken);
                    await service.DeleteAsync(id, cancellationToken);
                    items.Add(new ProxyBatchItemDto(id, true, "已删除", null));
                }
                catch (Exception ex) when (IsClientError(ex))
                {
                    items.Add(new ProxyBatchItemDto(id, false, "删除失败", ex.Message));
                }
            }
            return Results.Ok(ToBatchResult(items));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> CreateWarpAsync(
        WarpCreateRequestDto request,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            if (AccountLoginProxyCoordinator.IsManagedWarpRequestId(request.RequestId)
                || AccountImportService.IsManagedWarpRequestId(request.RequestId))
                throw new ArgumentException("该 WARP 请求 ID 前缀为系统内部保留值");

            var proxy = await service.CreateWarpAsync(
                request.Name,
                request.RequestId,
                cancellationToken,
                request.Protocol);
            return Results.Ok(ToDto(proxy));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> RefreshWarpAsync(
        int id,
        ProxyManagementService service,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.MaintainWarpAsync(
                id,
                WarpMaintenanceOptions.From(configuration),
                forceRestart: true,
                cancellationToken);
            return Results.Ok(result);
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> RefreshAllWarpAsync(
        ProxyManagementService service,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var result = await service.MaintainAllWarpAsync(
            WarpMaintenanceOptions.From(configuration),
            forceRestart: true,
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> BindAccountAsync(
        int id,
        AccountProxyBindingRequestDto request,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.BindAccountsAsync(
                new[] { id },
                new AccountProxyBindingInput(
                    request.Strategy ?? string.Empty,
                    request.ProxyId,
                    request.ExpectedProxyId,
                    ExpectedUseGlobalProxy: request.ExpectedUseGlobalProxy),
                cancellationToken);
            return Results.Ok(result);
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> BindAccountsAsync(
        BatchAccountProxyBindingRequestDto request,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(request.Strategy, "proxy_per_account", StringComparison.OrdinalIgnoreCase))
            {
                var ids = (request.AccountIds ?? Array.Empty<int>())
                    .Where(x => x > 0)
                    .Distinct()
                    .ToArray();
                var assignments = await service.PrepareAccountProxyAssignmentsAsync(
                    request.ProxyText,
                    ids.Length,
                    cancellationToken);
                var items = new List<AccountProxyOperationResult>(ids.Length);
                for (var index = 0; index < ids.Length; index++)
                {
                    var accountId = ids[index];
                    var assignment = assignments[index];
                    try
                    {
                        var item = (await service.BindAccountsAsync(
                                new[] { accountId },
                                new AccountProxyBindingInput(
                                    "existing",
                                    assignment.ProxyId,
                                    ExpectedConnection: assignment.ExpectedConnection),
                                cancellationToken))
                            .Items
                            .Single();
                        items.Add(item with
                        {
                            Summary = item.Success
                                ? $"已绑定第 {assignment.SourceLine} 行代理 {assignment.ProxyName}"
                                : item.Summary
                        });
                    }
                    catch (Exception ex) when (IsClientError(ex))
                    {
                        items.Add(new AccountProxyOperationResult(
                            accountId,
                            null,
                            false,
                            "逐账号代理绑定失败",
                            ex.Message,
                            assignment.ProxyId));
                    }
                }

                return Results.Ok(new AccountProxyBatchResult(
                    items.Count(x => x.Success),
                    items.Count(x => !x.Success),
                    items));
            }

            var result = await service.BindAccountsAsync(
                request.AccountIds ?? Array.Empty<int>(),
                new AccountProxyBindingInput(
                    request.Strategy ?? string.Empty,
                    request.ProxyId),
                cancellationToken);
            return Results.Ok(result);
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static async Task<IResult> ProbeAccountEgressAsync(
        int id,
        ProxyManagementService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.ProbeAccountAsync(id, cancellationToken);
            return Results.Ok(ToDto(result, "account_effective_route"));
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            return ToError(ex);
        }
    }

    private static OutboundProxyInput ToInput(ProxySaveRequestDto request) =>
        new(
            request.Name,
            request.Kind,
            request.Protocol,
            request.Host,
            request.Port,
            request.Username,
            request.Password,
            request.Secret,
            request.ResinPlatform,
            request.ResinAdminUrl,
            request.ResinAdminToken,
            request.IsEnabled,
            request.TestAfterSave,
            request.ClearPassword,
            request.ClearResinAdminToken,
            request.CategoryId);

    private static async Task<IDisposable?> AcquireWarpMutationLeaseAsync(
        int proxyId,
        ProxyManagementService service,
        IWarpProxyUsageGuard warpProxyUsageGuard,
        TemporaryWarpClaimStore temporaryWarpClaims,
        CancellationToken cancellationToken)
    {
        var proxy = await service.GetAsync(
            proxyId,
            cancellationToken: cancellationToken);
        if (proxy?.Kind != OutboundProxyKinds.Warp || proxy.WarpProfile == null)
            return null;

        if (temporaryWarpClaims.OwnsRequest(proxy.WarpProfile.RequestId))
        {
            throw new ProxyInUseException(
                "WARP 正在维护或被账号首次连接流程使用，已阻止修改或删除");
        }

        return warpProxyUsageGuard.TryAcquireMaintenance(proxyId)
            ?? throw new ProxyInUseException(
                "WARP 正在维护或被账号首次连接流程使用，已阻止修改或删除");
    }

    public static ProxyDto ToDto(
        OutboundProxy proxy,
        bool isGlobal = false,
        int globalAccountCount = 0) =>
        new(
            proxy.Id,
            proxy.Name,
            proxy.Kind,
            proxy.Protocol,
            proxy.Host,
            proxy.Port,
            proxy.Username,
            !string.IsNullOrWhiteSpace(proxy.Password),
            !string.IsNullOrWhiteSpace(proxy.Secret),
            proxy.ResinPlatform,
            proxy.ResinAdminUrl,
            !string.IsNullOrWhiteSpace(proxy.ResinAdminToken),
            proxy.IsEnabled,
            proxy.TestStatus,
            proxy.LastError,
            proxy.LastLatencyMs,
            proxy.EgressIp,
            proxy.EgressCountry,
            proxy.EgressCity,
            proxy.EgressIsp,
            proxy.WarpProfile?.WarpStatus,
            proxy.WarpProfile?.Status,
            proxy.WarpProfile?.ConsecutiveFailures ?? 0,
            proxy.WarpProfile?.LastRecoveryAttemptAtUtc,
            proxy.WarpProfile?.LastRecoveredAtUtc,
            proxy.WarpProfile?.RecoveryCount ?? 0,
            proxy.LastTestedAtUtc,
            proxy.FirstBoundAtUtc,
            proxy.Accounts?.Count ?? 0,
            proxy.Category == null ? null : ToDto(proxy.Category),
            isGlobal,
            isGlobal || (proxy.Accounts?.Count ?? 0) > 0,
            (proxy.Accounts?.Count ?? 0) + (isGlobal ? globalAccountCount : 0),
            proxy.CreatedAtUtc,
            proxy.UpdatedAtUtc,
            proxy.ManagedWgcfProfile);

    private static ProxyCategoryDto ToDto(ProxyCategory category) =>
        new(
            category.Id,
            category.Name,
            category.Color,
            category.Description,
            category.Proxies?.Count ?? 0);

    private static ProxyBatchResultDto ToBatchResult(IReadOnlyList<ProxyBatchItemDto> items) =>
        new(items.Count(x => x.Success), items.Count(x => !x.Success), items);

    private static EgressProbeDto ToDto(EgressProbeResult result, string source) =>
        new(
            result.Success,
            result.Ip,
            result.Country,
            result.City,
            result.Isp,
            result.WarpStatus,
            result.LatencyMs,
            result.CheckedAtUtc,
            result.Error,
            source);

    private static bool IsClientError(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or KeyNotFoundException;

    private static IResult ToError(Exception exception) => exception switch
    {
        ProxyInUseException or ProxyBindingConflictException =>
            Results.Conflict(new OperationResultDto(false, exception.Message)),
        KeyNotFoundException =>
            Results.NotFound(new OperationResultDto(false, exception.Message)),
        _ => Results.BadRequest(new OperationResultDto(false, exception.Message))
    };
}

public sealed record ProxySaveRequestDto(
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

public sealed record ProxyImportRequestDto(string? Text, bool TestAfterImport = false);
public sealed record WarpCreateRequestDto(
    string? Name,
    string? RequestId,
    string? Protocol = null);
public sealed record AccountProxyBindingRequestDto(
    string? Strategy,
    int? ProxyId,
    int? ExpectedProxyId,
    bool? ExpectedUseGlobalProxy = null);
public sealed record BatchAccountProxyBindingRequestDto(
    IReadOnlyList<int>? AccountIds,
    string? Strategy,
    int? ProxyId,
    string? ProxyText);

public sealed record ProxyCategorySaveRequestDto(
    string? Name,
    string? Color,
    string? Description);
public sealed record ProxyBatchIdsRequestDto(IReadOnlyList<int>? ProxyIds);
public sealed record ProxyBatchCategoryRequestDto(IReadOnlyList<int>? ProxyIds, int? CategoryId);
public sealed record ProxyBatchEnabledRequestDto(IReadOnlyList<int>? ProxyIds, bool IsEnabled);
public sealed record ProxyBatchItemDto(int ProxyId, bool Success, string Summary, string? Error);
public sealed record ProxyBatchResultDto(int Success, int Failed, IReadOnlyList<ProxyBatchItemDto> Items);

public sealed record ProxyDto(
    int Id,
    string Name,
    string Kind,
    string Protocol,
    string Host,
    int Port,
    string? Username,
    bool HasPassword,
    bool HasSecret,
    string? ResinPlatform,
    string? ResinAdminUrl,
    bool HasResinAdminToken,
    bool IsEnabled,
    string TestStatus,
    string? LastError,
    int? LastLatencyMs,
    string? EgressIp,
    string? EgressCountry,
    string? EgressCity,
    string? EgressIsp,
    string? WarpStatus,
    string? WarpRuntimeStatus,
    int WarpConsecutiveFailures,
    DateTime? WarpLastRecoveryAttemptAtUtc,
    DateTime? WarpLastRecoveredAtUtc,
    int WarpRecoveryCount,
    DateTime? LastTestedAtUtc,
    DateTime? FirstBoundAtUtc,
    int AccountCount,
    ProxyCategoryDto? Category,
    bool IsGlobal,
    bool IsInUse,
    int UsageCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string? ManagedWgcfProfile = null);

public sealed record ProxyCategoryDto(
    int Id,
    string Name,
    string? Color,
    string? Description,
    int ProxyCount = 0);

public sealed record EgressProbeDto(
    bool Success,
    string? Ip,
    string? Country,
    string? City,
    string? Isp,
    string? WarpStatus,
    int? LatencyMs,
    DateTime CheckedAtUtc,
    string? Error,
    string Source);
