using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Models;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Proxy;

public sealed partial class ProxyManagementService
{
    internal async ValueTask<MutationLease> AcquireMutationLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        await MutationLock.WaitAsync(cancellationToken);
        return new MutationLease(this);
    }

    /// <summary>
    /// 在修改全局代理配置前，回收旧全局 Resin 为账号分配的稳定 Lease。
    ///
    /// 配置文件写入由调用方通过回调完成；整个过程持有代理变更锁，避免
    /// 账号切换在“旧 Lease 已释放、新全局配置尚未落盘”的窗口中重新建立连接。
    /// 如果清理或配置写入失败，受影响账号保持停用状态，调用方可在修复后重试。
    /// </summary>
    public async Task ExecuteGlobalProxyChangeAsync(
        bool nextEnabled,
        string? nextSourceMode,
        int? nextProxyId,
        Func<CancellationToken, Task> applyChangeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applyChangeAsync);

        var normalizedSourceMode = (nextSourceMode ?? GlobalTelegramProxyConfiguration.ManualSourceMode)
            .Trim()
            .ToLowerInvariant();
        if (normalizedSourceMode is not (
                GlobalTelegramProxyConfiguration.ManualSourceMode
                or GlobalTelegramProxyConfiguration.ExistingSourceMode))
        {
            throw new ArgumentException("全局代理来源仅支持 manual 或 existing", nameof(nextSourceMode));
        }
        if (nextEnabled
            && normalizedSourceMode == GlobalTelegramProxyConfiguration.ExistingSourceMode
            && nextProxyId is not > 0)
        {
            throw new ArgumentException("启用已有全局代理时必须提供代理 ID", nameof(nextProxyId));
        }

        await using var mutationLease = await AcquireMutationLeaseAsync(cancellationToken);
        if (nextEnabled
            && normalizedSourceMode == GlobalTelegramProxyConfiguration.ExistingSourceMode)
        {
            var selectedProxy = await _db.OutboundProxies
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == nextProxyId!.Value && x.IsEnabled,
                    cancellationToken);
            if (selectedProxy == null)
            {
                throw new KeyNotFoundException(
                    "所选全局代理不存在或已停用，未修改全局代理配置");
            }
            if (selectedProxy.ManagedWgcfProfile != null)
                throw new InvalidOperationException("受管 WireGuard 出口仅允许绑定单个账号，不能作为全局代理");
            EnsureWireGuardWarpReadyForBinding(selectedProxy);
        }

        var previousGlobalResin = await ResolveConfiguredGlobalResinProxyAsync(cancellationToken);
        var keepsSameResinRoute = nextEnabled
            && normalizedSourceMode == GlobalTelegramProxyConfiguration.ExistingSourceMode
            && previousGlobalResin != null
            && nextProxyId == previousGlobalResin.Id;

        var affectedAccounts = previousGlobalResin != null && !keepsSameResinRoute
            ? await _db.Accounts
                .Where(x => x.ProxyId == null && x.UseGlobalProxy)
                .ToListAsync(cancellationToken)
            : new List<Account>();
        var activationStates = affectedAccounts.ToDictionary(
            x => x.Id,
            x => x.IsActive);

        if (affectedAccounts.Count > 0)
        {
            foreach (var account in affectedAccounts)
                account.IsActive = false;
            if (activationStates.Values.Any(x => x))
                await _db.SaveChangesAsync(cancellationToken);

            await ReleaseClientsStrictAsync(affectedAccounts.Select(x => x.Id));
            foreach (var account in affectedAccounts)
                await ReleaseResinLeaseAsync(
                    previousGlobalResin!,
                    account.Id,
                    cancellationToken);
        }

        // 只有旧 Lease 全部释放后才允许写入新配置。异常时保持失败闭锁，
        // 防止账号在旧出口身份未清理的情况下继续运行。
        await applyChangeAsync(cancellationToken);

        if (affectedAccounts.Count == 0)
            return;

        foreach (var account in affectedAccounts)
            account.IsActive = activationStates[account.Id];
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<OutboundProxy?> ResolveConfiguredGlobalResinProxyAsync(
        CancellationToken cancellationToken)
    {
        if (_configuration == null
            || GlobalTelegramProxyConfiguration.GetSourceMode(_configuration)
                != GlobalTelegramProxyConfiguration.ExistingSourceMode)
        {
            return null;
        }

        // 即使当前配置被停用，也保留 ProxyId；删除或切换账号时仍需回收
        // 之前创建的稳定 Lease。这里不要求代理当前 IsEnabled。
        var proxyId = GlobalTelegramProxyConfiguration.GetSelectedProxyId(
            _configuration,
            requireEnabled: false);
        if (proxyId is not > 0)
            return null;

        return await _db.OutboundProxies
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == proxyId.Value && x.Kind == OutboundProxyKinds.Resin,
                cancellationToken);
    }

    /// <summary>
    /// 解除账号的代理绑定，并回收不再使用的账号专属代理资源。
    /// </summary>
    public async Task ReleaseAccountBindingAsync(
        int accountId,
        int? expectedProxyId = null,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            return;

        await using var lease = await AcquireMutationLeaseAsync(cancellationToken);
        await ReleaseAccountBindingWithinMutationAsync(
            lease,
            accountId,
            expectedProxyId,
            deactivateAccount: false,
            cancellationToken);
    }

    /// <summary>
    /// 在调用方已持有代理变更锁时解除绑定。锁租约会阻止误用和重复加锁。
    /// </summary>
    internal async Task ReleaseAccountBindingWithinMutationAsync(
        MutationLease lease,
        int accountId,
        int? expectedProxyId,
        bool deactivateAccount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureHeldBy(this);
        if (accountId <= 0)
            return;

        var account = await _db.Accounts
            .FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account == null)
        {
            await ReleaseClientsStrictAsync(new[] { accountId });
            return;
        }

        var hadAccountProxy = account.ProxyId.HasValue;
        var currentProxyId = account.ProxyId ?? 0;
        if (expectedProxyId.HasValue && currentProxyId != expectedProxyId.Value)
            throw new ProxyBindingConflictException("账号代理绑定已变化，未执行删除清理");

        if (deactivateAccount && account.IsActive)
        {
            account.IsActive = false;
            await _db.SaveChangesAsync(cancellationToken);
        }

        // 只有确认旧客户端已断开后，才允许解除绑定或回收独立代理资源。
        await ReleaseClientsStrictAsync(new[] { accountId });

        OutboundProxy? oldProxy = null;
        if (account.ProxyId.HasValue)
        {
            oldProxy = await _db.OutboundProxies
                .Include(x => x.WarpProfile)
                .FirstOrDefaultAsync(x => x.Id == account.ProxyId.Value, cancellationToken);

            if (oldProxy?.Kind == OutboundProxyKinds.Resin)
            {
                await ReleaseResinLeaseAsync(oldProxy, accountId, cancellationToken);
            }

            account.ProxyId = null;
            account.Proxy = null;
        }
        else if (account.UseGlobalProxy)
        {
            // 全局代理没有账号级 ProxyId，但全局 Resin 仍会以
            // tg_account_{id} 创建稳定 Lease；删除账号前必须使用当前配置
            // 解析并释放该 Lease，不能只依赖导航属性。
            oldProxy = await ResolveConfiguredGlobalResinProxyAsync(cancellationToken);
            if (oldProxy != null)
                await ReleaseResinLeaseAsync(oldProxy, accountId, cancellationToken);
        }

        if (!hadAccountProxy && oldProxy == null)
            return;

        await _db.SaveChangesAsync(cancellationToken);

        if (oldProxy?.Kind != OutboundProxyKinds.Warp)
            return;

        try
        {
            await CleanupReplacedWarpProxiesAsync(
                new[] { oldProxy.Id },
                targetProxyId: null,
                cancellationToken);
        }
        catch (Exception cleanupError)
        {
            var message = deactivateAccount
                ? "WARP 资源清理失败，账号已解除代理绑定并停用，可重试删除旧 WARP"
                : "WARP 资源清理失败，账号已解除旧代理绑定，可重试删除旧 WARP";
            throw new InvalidOperationException(message, cleanupError);
        }
    }

    public async Task<AccountProxyBatchResult> BindAccountsAsync(
        IReadOnlyCollection<int> accountIds,
        AccountProxyBindingInput input,
        CancellationToken cancellationToken = default,
        ProxyConnectionOptions? expectedConnection = null)
    {
        var ids = accountIds.Where(x => x > 0).Distinct().ToArray();
        if (ids.Length == 0)
            throw new ArgumentException("请选择账号");
        if (ids.Length > 500)
            throw new ArgumentException("单次最多处理 500 个账号");

        var strategy = NormalizeStrategy(input.Strategy);
        expectedConnection ??= input.ExpectedConnection;
        if (strategy == "warp_per_account" && ids.Length > 10)
            throw new ArgumentException("逐账号创建 WARP 单次最多处理 10 个账号");

        if (strategy == "warp_per_account")
        {
            var warpResults = new List<AccountProxyOperationResult>(ids.Length);
            foreach (var accountId in ids)
            {
                try
                {
                    warpResults.Add(await BindOneWarpAsync(
                        accountId,
                        ids.Length == 1 ? input.ExpectedProxyId : null,
                        cancellationToken));
                }
                catch (Exception ex)
                {
                    var phone = await _db.Accounts.AsNoTracking()
                        .Where(x => x.Id == accountId)
                        .Select(x => x.Phone)
                        .FirstOrDefaultAsync(cancellationToken);
                    warpResults.Add(new AccountProxyOperationResult(
                        accountId,
                        phone,
                        false,
                        "WARP 创建或绑定失败",
                        SafeError(ex)));
                }
            }

            return ToBatchResult(warpResults);
        }

        var targetProxyId = strategy is "direct" or "global"
            ? null
            : input.ProxyId is > 0
                ? input.ProxyId
                : throw new ArgumentException("请选择已有代理");

        await MutationLock.WaitAsync(cancellationToken);
        try
        {
            OutboundProxy? targetProxy = null;
            if (targetProxyId.HasValue)
            {
                targetProxy = await _db.OutboundProxies
                    .FirstOrDefaultAsync(
                        x => x.Id == targetProxyId.Value && x.IsEnabled,
                        cancellationToken)
                    ?? throw new KeyNotFoundException("所选代理不存在或已停用");
                EnsureWireGuardWarpReadyForBinding(targetProxy);
                if (targetProxy.ManagedWgcfProfile != null)
                {
                    if (ids.Length != 1 || IsEnabledGlobalProxy(targetProxy.Id)
                        || await _db.Accounts.AnyAsync(
                            account => account.ProxyId == targetProxy.Id && account.Id != ids[0], cancellationToken))
                        throw new InvalidOperationException("受管 WireGuard 出口只能由一个账号独占，未修改任何绑定");
                }
            }

            ProxyConnectionOptions? currentGlobalConnection = null;
            if (strategy == "global")
            {
                currentGlobalConnection = await ResolveGlobalProxyRequiredAsync(
                    "tg_binding_validation",
                    cancellationToken);
            }

            if (expectedConnection != null)
            {
                var currentConnection = strategy switch
                {
                    "global" => currentGlobalConnection,
                    "existing" when targetProxy != null =>
                        BuildPhysicalConnectionOptions(targetProxy),
                    "direct" => null,
                    _ => null
                };
                if (!SameBindingConnection(expectedConnection, currentConnection))
                {
                    throw new ProxyBindingConflictException(
                        "导入或登录期间代理连接参数已变化，已阻止把账号绑定到不同出口");
                }
            }

            var accounts = await _db.Accounts
                .Include(x => x.Proxy)
                .Where(x => ids.Contains(x.Id))
                .ToListAsync(cancellationToken);
            var found = accounts.Select(x => x.Id).ToHashSet();
            var results = new List<AccountProxyOperationResult>(ids.Length);

            if (ids.Length == 1 && (input.ExpectedProxyId.HasValue || input.ExpectedUseGlobalProxy.HasValue))
            {
                var account = accounts.FirstOrDefault();
                var current = account?.ProxyId ?? 0;
                if ((input.ExpectedProxyId.HasValue && current != input.ExpectedProxyId.Value)
                    || (input.ExpectedUseGlobalProxy.HasValue && account?.UseGlobalProxy != input.ExpectedUseGlobalProxy.Value))
                    throw new ProxyBindingConflictException("账号代理绑定已变化，请刷新后重试");
            }

            var oldBindings = accounts
                .Where(x => x.ProxyId.HasValue && x.Proxy != null)
                .Select(x => new PreviousProxyBinding(x.Id, x.Proxy!))
                .ToList();

            // global 路由没有账号级 ProxyId。切换到 direct/专属代理时，
            // 仍需把当前全局 Resin 加入旧绑定清单，以释放稳定账号身份。
            if (strategy != "global")
            {
                var globalResin = await ResolveConfiguredGlobalResinProxyAsync(
                    cancellationToken);
                if (globalResin != null)
                {
                    oldBindings.AddRange(
                        accounts
                            .Where(x => x.ProxyId == null && x.UseGlobalProxy)
                            .Select(x => new PreviousProxyBinding(x.Id, globalResin)));
                }
            }

            var activationStates = accounts.ToDictionary(x => x.Id, x => x.IsActive);
            foreach (var account in accounts)
                account.IsActive = false;
            if (activationStates.Values.Any(x => x))
                await _db.SaveChangesAsync(cancellationToken);

            // 提交新路由前必须确认旧客户端已经断开。否则数据库虽然显示已切换，
            // 内存中的已登录客户端仍可能继续使用旧出口，造成“伪切换”。
            await ReleaseClientsStrictAsync(accounts.Select(x => x.Id));

            var resinBindings = oldBindings
                .Where(x => x.Proxy.Kind == OutboundProxyKinds.Resin
                            && x.Proxy.Id != targetProxyId)
                .ToList();
            foreach (var old in resinBindings)
                await ReleaseResinLeaseAsync(old.Proxy, old.AccountId, cancellationToken);

            var useGlobalProxy = strategy == "global";
            foreach (var account in accounts)
            {
                account.ProxyId = targetProxyId;
                account.UseGlobalProxy = useGlobalProxy;
                account.IsActive = activationStates[account.Id];
            }
            if (targetProxy != null && targetProxy.FirstBoundAtUtc == null)
                targetProxy.FirstBoundAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            foreach (var account in accounts)
            {
                results.Add(new AccountProxyOperationResult(
                    account.Id,
                    account.Phone,
                    true,
                    targetProxy == null
                        ? useGlobalProxy ? "已切换为全局代理设置" : "已切换为直连"
                        : $"已绑定代理 {targetProxy.Name}",
                    null,
                    targetProxyId));
            }
            foreach (var missing in ids.Where(x => !found.Contains(x)))
            {
                results.Add(new AccountProxyOperationResult(
                    missing,
                    null,
                    false,
                    "账号不存在",
                    "账号不存在"));
            }

            try
            {
                // 切换到共享运行器出口后保留旧官方 WARP，供操作者明确回滚或清理。
                if (targetProxy?.ManagedWgcfProfile == null)
                    await CleanupReplacedWarpProxiesAsync(
                        oldBindings.Select(x => x.Proxy.Id).Distinct(),
                        targetProxyId,
                        cancellationToken);
            }
            catch (Exception cleanupError)
            {
                // 新路由已经提交，不能再把账号指回已停用或已部分删除的旧 WARP。
                throw new InvalidOperationException(
                    "代理切换已提交，但旧 WARP 资源清理失败，请重试删除旧 WARP",
                    cleanupError);
            }

            return ToBatchResult(results);
        }
        finally
        {
            MutationLock.Release();
        }
    }

    public async Task ValidateBindingInputAsync(
        AccountProxyBindingInput input,
        CancellationToken cancellationToken = default)
    {
        var strategy = NormalizeStrategy(input.Strategy);
        if (strategy == "global")
        {
            _ = await ResolveGlobalProxyRequiredAsync(
                "tg_binding_validation",
                cancellationToken);
        }
        else if (strategy == "existing")
        {
            if (input.ProxyId is not > 0)
                throw new ArgumentException("请选择已有代理");
            var proxy = await _db.OutboundProxies
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == input.ProxyId && x.IsEnabled, cancellationToken);
            if (proxy == null)
                throw new KeyNotFoundException("所选代理不存在或已停用");
            if (proxy.ManagedWgcfProfile != null)
                throw new InvalidOperationException("受管 WireGuard 只支持已入库账号单独绑定，不能用于导入或登录首次连接");
            EnsureWireGuardWarpReadyForBinding(proxy);
        }
        else if (strategy == "warp_per_account")
        {
            var status = await _warpManager.GetStatusAsync(cancellationToken);
            if (!status.PlatformSupported || !status.Enabled || !status.DockerAvailable)
                throw new InvalidOperationException(status.Error ?? "WARP 运行条件不可用");
        }
    }

    private static void EnsureWireGuardWarpReadyForBinding(OutboundProxy proxy)
    {
        if (proxy.Kind != OutboundProxyKinds.WireGuardWarp)
            return;

        if (proxy.TestStatus != "ok" || string.IsNullOrWhiteSpace(proxy.EgressIp))
        {
            throw new InvalidOperationException(
                "外部 WireGuard WARP 端点必须先检测成功并确认 Cloudflare Trace 报告 WARP 已启用，才能绑定账号");
        }
    }

    private async Task<AccountProxyOperationResult> BindOneWarpAsync(
        int accountId,
        int? expectedProxyId,
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _db.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new KeyNotFoundException("账号不存在");
        var currentProxyId = accountSnapshot.ProxyId ?? 0;
        if (expectedProxyId.HasValue && currentProxyId != expectedProxyId.Value)
            throw new ProxyBindingConflictException("账号代理绑定已变化，未创建 WARP");

        var requestId = $"account-{accountId}-{Guid.NewGuid():N}";
        using var temporaryWarpClaim = _temporaryWarpClaims?.ClaimRequest(requestId);
        var newProxy = await _warpManager.CreateAsync(
            $"WARP · {accountSnapshot.DisplayPhone}",
            requestId,
            cancellationToken);
        var keep = false;
        try
        {
            var result = await BindAccountsAsync(
                new[] { accountId },
                new AccountProxyBindingInput("existing", newProxy.Id, currentProxyId),
                cancellationToken);
            var item = result.Items.Single();
            keep = item.Success;
            return item with
            {
                Summary = $"已创建并绑定独立 WARP {newProxy.Name}",
                ProxyId = newProxy.Id
            };
        }
        finally
        {
            if (!keep)
            {
                try
                {
                    await DeleteAsync(newProxy.Id, CancellationToken.None);
                }
                catch (Exception cleanupError)
                {
                    _logger.LogWarning(
                        cleanupError,
                        "Failed to compensate unbound WARP proxy {ProxyId}",
                        newProxy.Id);
                }
            }
        }
    }

    private async Task CleanupReplacedWarpProxiesAsync(
        IEnumerable<int> oldProxyIds,
        int? targetProxyId,
        CancellationToken cancellationToken)
    {
        foreach (var oldId in oldProxyIds.Where(x => x != targetProxyId).Distinct())
        {
            if (IsEnabledGlobalProxy(oldId))
                continue;

            var oldProxy = await _db.OutboundProxies
                .Include(x => x.WarpProfile)
                .FirstOrDefaultAsync(x => x.Id == oldId, cancellationToken);
            if (oldProxy?.Kind != OutboundProxyKinds.Warp)
                continue;

            using var cleanupLease = _warpProxyUsageGuard?.TryAcquireMaintenance(oldId);
            if (_warpProxyUsageGuard != null && cleanupLease == null)
            {
                _logger.LogInformation(
                    "Skipped cleanup of WARP proxy {ProxyId} because a first-connection flow is using it",
                    oldId);
                continue;
            }

            var stillUsed = await _db.Accounts
                .AnyAsync(x => x.ProxyId == oldId, cancellationToken);
            if (stillUsed)
                continue;

            await DeleteUnusedProxyCoreAsync(oldProxy, cancellationToken);
        }
    }

    private async Task ReleaseClientsAsync(IEnumerable<int> accountIds)
    {
        foreach (var accountId in accountIds.Distinct())
        {
            try
            {
                await _clientPool.RemoveClientAsync(accountId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release Telegram client {AccountId}", accountId);
            }
        }
    }

    private async Task ReleaseClientsStrictAsync(IEnumerable<int> accountIds)
    {
        foreach (var accountId in accountIds.Distinct())
            await _clientPool.RemoveClientStrictAsync(accountId);
    }

    private static AccountProxyBatchResult ToBatchResult(
        IReadOnlyList<AccountProxyOperationResult> items) =>
        new(
            items.Count(x => x.Success),
            items.Count(x => !x.Success),
            items);

    private static ProxyConnectionOptions BuildPhysicalConnectionOptions(
        OutboundProxy proxy) =>
        new(
            proxy.Id,
            proxy.Name,
            proxy.Kind,
            proxy.Protocol,
            proxy.Host,
            proxy.Port,
            proxy.Username,
            proxy.Password,
            proxy.Secret);

    private static bool SameBindingConnection(
        ProxyConnectionOptions expected,
        ProxyConnectionOptions? actual)
    {
        if (actual == null
            || expected.ProxyId != actual.ProxyId
            || !string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal)
            || !string.Equals(expected.Protocol, actual.Protocol, StringComparison.Ordinal)
            || !string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
            || expected.Port != actual.Port
            || !string.Equals(expected.Password, actual.Password, StringComparison.Ordinal)
            || !string.Equals(expected.Secret, actual.Secret, StringComparison.Ordinal))
        {
            return false;
        }

        // Resin 的用户名包含临时/稳定账号身份，不能作为代理连接快照的
        // 变化判据；其余代理的认证用户名必须保持一致。
        return string.Equals(expected.Kind, OutboundProxyKinds.Resin, StringComparison.Ordinal)
               || string.Equals(expected.Username, actual.Username, StringComparison.Ordinal);
    }

    private sealed record PreviousProxyBinding(
        int AccountId,
        OutboundProxy Proxy);

    internal sealed class MutationLease : IAsyncDisposable
    {
        private ProxyManagementService? _owner;

        internal MutationLease(ProxyManagementService owner)
        {
            _owner = owner;
        }

        internal void EnsureHeldBy(ProxyManagementService owner)
        {
            if (!ReferenceEquals(Volatile.Read(ref _owner), owner))
                throw new InvalidOperationException("代理变更锁租约无效或已释放");
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _owner, null) != null)
                MutationLock.Release();
            return ValueTask.CompletedTask;
        }
    }
}
