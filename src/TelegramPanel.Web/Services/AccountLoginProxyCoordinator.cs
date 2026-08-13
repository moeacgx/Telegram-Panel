using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Utils;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 登录阶段使用的不可变代理快照。
/// 账号完成授权前尚未入库，不能依赖账号 ID 去数据库解析代理。
/// </summary>
public sealed record AccountLoginProxyState(
    int LoginId,
    AccountProxyBindingInput Binding,
    string FrozenStrategy,
    AccountProxyResolution Resolution,
    int? OwnedWarpProxyId,
    ResinLeaseControlSnapshot? ResinLease,
    string? TemporaryResinKey,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// 临时独占一个仍在字典中的冻结路由，释放前过期清理和完成流程都不能取得它。
/// </summary>
public sealed class AccountLoginProxyStateLease : IDisposable
{
    private Action? _release;

    internal AccountLoginProxyStateLease(
        AccountLoginProxyState state,
        Action release)
    {
        State = state;
        _release = release;
    }

    public AccountLoginProxyState State { get; }

    public void Dispose() =>
        Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>
/// 跨请求保存手机号/二维码登录会话的代理选择。
/// </summary>
public sealed class AccountLoginProxyStateStore : IWarpProxyUsageGuard
{
    private readonly object _stateGate = new();
    private readonly Dictionary<int, AccountLoginProxyState> _states = new();
    private readonly HashSet<int> _claimedLoginIds = new();
    private readonly HashSet<int> _maintenanceWarpProxyIds = new();
    private readonly ConcurrentDictionary<int, int> _claimedWarpProxyIds = new();
    private readonly ConcurrentDictionary<string, int> _claimedPhoneLoginIds =
        new(StringComparer.Ordinal);

    public bool Contains(int loginId)
    {
        if (loginId <= 0)
            return false;

        lock (_stateGate)
            return _states.ContainsKey(loginId);
    }

    public bool OwnsWarpProxy(int proxyId)
    {
        if (proxyId <= 0)
            return false;

        lock (_stateGate)
            return OwnsWarpProxyCore(proxyId);
    }

    public bool CanUseWarpProxy(int proxyId)
    {
        if (proxyId <= 0)
            return false;

        lock (_stateGate)
            return !_maintenanceWarpProxyIds.Contains(proxyId)
                   && !OwnsWarpProxyCore(proxyId);
    }

    public IDisposable? TryAcquireUsage(int proxyId)
    {
        if (proxyId <= 0)
            return null;

        lock (_stateGate)
        {
            if (_maintenanceWarpProxyIds.Contains(proxyId)
                || OwnsWarpProxyCore(proxyId))
            {
                return null;
            }

            ClaimWarpProxy(proxyId);
        }

        return new WarpUsageLease(this, proxyId);
    }

    public IDisposable? TryAcquireMaintenance(int proxyId)
    {
        if (proxyId <= 0)
            return null;

        lock (_stateGate)
        {
            if (_maintenanceWarpProxyIds.Contains(proxyId)
                || OwnsWarpProxyCore(proxyId))
            {
                return null;
            }

            _maintenanceWarpProxyIds.Add(proxyId);
        }

        return new WarpMaintenanceLease(this, proxyId);
    }

    public void ClaimWarpProxy(int proxyId)
    {
        if (proxyId > 0)
            _claimedWarpProxyIds.AddOrUpdate(
                proxyId,
                1,
                static (_, count) => checked(count + 1));
    }

    public void ReleaseWarpProxyClaim(int proxyId)
    {
        if (proxyId <= 0)
            return;

        while (_claimedWarpProxyIds.TryGetValue(proxyId, out var count))
        {
            if (count <= 1)
            {
                if (((ICollection<KeyValuePair<int, int>>)_claimedWarpProxyIds)
                    .Remove(new KeyValuePair<int, int>(proxyId, count)))
                {
                    return;
                }

                continue;
            }

            if (_claimedWarpProxyIds.TryUpdate(proxyId, count - 1, count))
                return;
        }
    }

    public bool TryClaimPhone(int loginId, string normalizedPhone)
    {
        if (loginId <= 0
            || string.IsNullOrWhiteSpace(normalizedPhone)
            || !Contains(loginId))
        {
            return false;
        }

        while (true)
        {
            if (_claimedPhoneLoginIds.TryGetValue(normalizedPhone, out var currentLoginId))
                return currentLoginId == loginId;

            if (!_claimedPhoneLoginIds.TryAdd(normalizedPhone, loginId))
                continue;

            if (Contains(loginId))
                return true;

            ((ICollection<KeyValuePair<string, int>>)_claimedPhoneLoginIds)
                .Remove(new KeyValuePair<string, int>(normalizedPhone, loginId));
            return false;
        }
    }

    public void ReleasePhoneClaim(int loginId)
    {
        if (loginId <= 0)
            return;

        foreach (var pair in _claimedPhoneLoginIds)
        {
            if (pair.Value != loginId)
                continue;

            ((ICollection<KeyValuePair<string, int>>)_claimedPhoneLoginIds)
                .Remove(pair);
        }
    }

    public bool TryAdd(AccountLoginProxyState state) =>
        TryAdd(state, out _);

    public bool TryAdd(AccountLoginProxyState state, out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_stateGate)
        {
            if (_claimedLoginIds.Contains(state.LoginId))
            {
                error = "该登录会话正在处理，请稍后重试";
                return false;
            }

            var warpProxyId = GetFrozenWarpProxyId(state);
            if (warpProxyId.HasValue
                && (_maintenanceWarpProxyIds.Contains(warpProxyId.Value)
                    || OwnsWarpProxyCore(warpProxyId.Value)))
            {
                error = "所选 WARP 正在维护或被另一个首次连接流程使用，请稍后重试；登录出口未发生切换";
                return false;
            }

            if (!_states.TryAdd(state.LoginId, state))
            {
                error = "该登录会话已有冻结路由，请先取消原会话";
                return false;
            }

            error = null;
            return true;
        }
    }

    /// <summary>
    /// 在保留状态的同时独占登录 ID，供二维码或手机号重启复用同一冻结出口。
    /// </summary>
    public bool TryClaimExisting(
        int loginId,
        out AccountLoginProxyState? state)
    {
        lock (_stateGate)
        {
            if (loginId <= 0
                || _claimedLoginIds.Contains(loginId)
                || !_states.TryGetValue(loginId, out var current))
            {
                state = null;
                return false;
            }

            state = current with { CreatedAtUtc = DateTimeOffset.UtcNow };
            _states[loginId] = state;
            _claimedLoginIds.Add(loginId);
            return true;
        }
    }

    public void ReleaseLoginClaim(int loginId)
    {
        if (loginId <= 0)
            return;

        lock (_stateGate)
            _claimedLoginIds.Remove(loginId);
    }

    /// <summary>
    /// 严格断开失败时原子恢复原状态；独占标记保证不会覆盖新会话。
    /// </summary>
    public bool RestoreClaimedState(AccountLoginProxyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_stateGate)
        {
            if (!_claimedLoginIds.Contains(state.LoginId)
                || _states.ContainsKey(state.LoginId))
            {
                return false;
            }

            _states.Add(state.LoginId, state);
            _claimedLoginIds.Remove(state.LoginId);
            return true;
        }
    }

    public bool TryTakeForCompletion(
        int loginId,
        out AccountLoginProxyState? state)
    {
        lock (_stateGate)
        {
            if (loginId <= 0
                || _claimedLoginIds.Contains(loginId)
                || !_states.TryGetValue(loginId, out var current))
            {
                state = null;
                return false;
            }

            _claimedLoginIds.Add(loginId);
            ClaimWarpProxy(GetFrozenWarpProxyId(current).GetValueOrDefault());
            _states.Remove(loginId);
            state = current;
            return true;
        }
    }

    public bool TryTakeExpired(
        DateTimeOffset cutoffUtc,
        out AccountLoginProxyState? state)
    {
        lock (_stateGate)
        {
            var candidate = _states.FirstOrDefault(pair =>
                pair.Value.CreatedAtUtc <= cutoffUtc
                && !_claimedLoginIds.Contains(pair.Key));
            if (candidate.Value == null)
            {
                state = null;
                return false;
            }

            _claimedLoginIds.Add(candidate.Key);
            ClaimWarpProxy(GetFrozenWarpProxyId(candidate.Value).GetValueOrDefault());
            _states.Remove(candidate.Key);
            state = candidate.Value;
            return true;
        }
    }

    public bool TryTakeAny(out AccountLoginProxyState? state)
    {
        lock (_stateGate)
        {
            var candidate = _states.FirstOrDefault(pair =>
                !_claimedLoginIds.Contains(pair.Key));
            if (candidate.Value == null)
            {
                state = null;
                return false;
            }

            _claimedLoginIds.Add(candidate.Key);
            ClaimWarpProxy(GetFrozenWarpProxyId(candidate.Value).GetValueOrDefault());
            _states.Remove(candidate.Key);
            state = candidate.Value;
            return true;
        }
    }

    internal static int? GetFrozenWarpProxyId(AccountLoginProxyState state)
    {
        var proxy = state.Resolution.Proxy;
        if (proxy is { ProxyId: > 0 }
            && string.Equals(
                proxy.Kind,
                OutboundProxyKinds.Warp,
                StringComparison.Ordinal))
        {
            return proxy.ProxyId;
        }

        return state.OwnedWarpProxyId;
    }

    private bool OwnsWarpProxyCore(int proxyId) =>
        _claimedWarpProxyIds.ContainsKey(proxyId)
        || _states.Values.Any(x => GetFrozenWarpProxyId(x) == proxyId);

    private void ReleaseMaintenance(int proxyId)
    {
        lock (_stateGate)
            _maintenanceWarpProxyIds.Remove(proxyId);
    }

    private sealed class WarpMaintenanceLease : IDisposable
    {
        private AccountLoginProxyStateStore? _owner;
        private readonly int _proxyId;

        public WarpMaintenanceLease(AccountLoginProxyStateStore owner, int proxyId)
        {
            _owner = owner;
            _proxyId = proxyId;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseMaintenance(_proxyId);
    }

    private sealed class WarpUsageLease : IDisposable
    {
        private AccountLoginProxyStateStore? _owner;
        private readonly int _proxyId;

        public WarpUsageLease(AccountLoginProxyStateStore owner, int proxyId)
        {
            _owner = owner;
            _proxyId = proxyId;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseWarpProxyClaim(_proxyId);
    }
}

/// <summary>
/// 协调“登录前选路由”和“登录成功后绑定正式账号”两个阶段。
/// </summary>
public sealed class AccountLoginProxyCoordinator
{
    public const string ManagedWarpRequestPrefix = "telegram-panel.internal.login.";

    private readonly AccountLoginProxyStateStore _store;
    private readonly ProxyManagementService _proxyManagement;
    private readonly AccountManagementService _accountManagement;
    private readonly IConfiguration _configuration;
    private readonly IAccountService _accountService;
    private readonly TemporaryWarpClaimStore _temporaryWarpClaims;
    private readonly ILogger<AccountLoginProxyCoordinator> _logger;

    public AccountLoginProxyCoordinator(
        AccountLoginProxyStateStore store,
        ProxyManagementService proxyManagement,
        AccountManagementService accountManagement,
        IAccountService accountService,
        TemporaryWarpClaimStore temporaryWarpClaims,
        IConfiguration configuration,
        ILogger<AccountLoginProxyCoordinator> logger)
    {
        _store = store;
        _proxyManagement = proxyManagement;
        _accountManagement = accountManagement;
        _configuration = configuration;
        _accountService = accountService;
        _temporaryWarpClaims = temporaryWarpClaims;
        _logger = logger;
    }

    public bool HasState(int loginId) => _store.Contains(loginId);

    /// <summary>
    /// 复用已经冻结的登录出口。客户端提交的选择必须与首次选择完全一致。
    /// </summary>
    public AccountLoginProxyStateLease ClaimFrozenState(
        int loginId,
        string? strategy,
        int? proxyId)
    {
        if (!_store.TryClaimExisting(loginId, out var state) || state == null)
            throw new InvalidOperationException("登录代理会话正在处理或已失效，请稍后重试");

        try
        {
            ValidateFrozenSelection(state, strategy, proxyId);
            return new AccountLoginProxyStateLease(
                state,
                () => _store.ReleaseLoginClaim(loginId));
        }
        catch
        {
            _store.ReleaseLoginClaim(loginId);
            throw;
        }
    }

    /// <summary>
    /// 手机号重新登录前停用并释放同手机号的正式客户端。
    /// 必须在新登录客户端创建之前完成，避免旧出口并发访问 Telegram 或争用 Session 文件。
    /// </summary>
    public async Task<int?> QuiesceExistingAccountAsync(
        int loginId,
        string? phone,
        CancellationToken cancellationToken = default)
    {
        var phoneDigits = PhoneNumberFormatter.NormalizeToDigits(phone);
        if (string.IsNullOrWhiteSpace(phoneDigits))
            throw new ArgumentException("请输入有效手机号（包含国家代码）", nameof(phone));

        if (!_store.TryClaimPhone(loginId, phoneDigits))
        {
            throw new InvalidOperationException(
                "该手机号已有登录会话正在使用 Session，请先取消原登录后再试");
        }


        var existing = await _accountManagement.GetAccountByPhoneAsync(phoneDigits);
        if (existing == null)
            return null;

        await _accountManagement.SetAccountActiveStatusAsync(existing.Id, false);
        try
        {
            await _proxyManagement.ReleaseAccountClientStrictAsync(existing.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to release existing account {AccountId} before phone relogin",
                existing.Id);
            throw new InvalidOperationException(
                "同手机号既有账号客户端无法安全停止，已阻止重新登录并保持账号停用",
                ex);
        }

        return existing.Id;
    }

    public static bool IsManagedWarpRequestId(string? requestId) =>
        !string.IsNullOrWhiteSpace(requestId)
        && requestId.Trim().StartsWith(
            ManagedWarpRequestPrefix,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 在首次 Telegram 网络连接之前解析并冻结用户选择的路由。
    /// </summary>
    public async Task<AccountLoginProxyState> PrepareAsync(
        int loginId,
        string? strategy,
        int? proxyId,
        CancellationToken cancellationToken = default)
    {
        if (loginId <= 0)
            throw new ArgumentOutOfRangeException(nameof(loginId));

        if (_store.Contains(loginId))
            throw new InvalidOperationException("该登录会话已有冻结路由，请先取消原会话");

        var normalizedStrategy = (strategy ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedStrategy))
        {
            throw new ArgumentException(
                "请先明确选择登录代理；可选择已有代理、自动分配已有 WARP，或明确选择直连");
        }

        AccountProxyBindingInput binding;
        string frozenStrategy;
        AccountProxyResolution resolution;
        ResinLeaseControlSnapshot? resinLease = null;
        string? temporaryResinKey = null;
        int? ownedWarpProxyId = null;
        IDisposable? warpRequestClaim = null;

        switch (normalizedStrategy)
        {
            case "direct":
                binding = new AccountProxyBindingInput("direct");
                resolution = new AccountProxyResolution(null, false);
                frozenStrategy = "direct";
                break;

            case "global":
                {
                    var selectedGlobalId = _proxyManagement.GetEnabledGlobalProxyId();
                    var selectedGlobal = selectedGlobalId is > 0
                        ? await _proxyManagement.GetAsync(
                            selectedGlobalId.Value,
                            cancellationToken: cancellationToken)
                        : null;
                    if (selectedGlobalId is > 0 && selectedGlobal is not { IsEnabled: true })
                        throw new InvalidOperationException("全局代理引用的已有代理不存在或已停用");
                    if (selectedGlobal?.Kind == OutboundProxyKinds.Warp
                        && _temporaryWarpClaims.OwnsRequest(selectedGlobal.WarpProfile?.RequestId))
                    {
                        throw new InvalidOperationException(
                            "全局 WARP 正被另一个账号首次连接流程使用，请稍后重试");
                    }

                    temporaryResinKey = selectedGlobal?.Kind == OutboundProxyKinds.Resin
                        ? $"tg_login_{loginId}_{Guid.NewGuid():N}"
                        : null;
                    var stableLoginKey = temporaryResinKey ?? $"tg_login_{loginId}";
                    var globalProxy = selectedGlobal == null
                        ? await _proxyManagement.ResolveGlobalProxyRequiredAsync(
                            stableLoginKey,
                            cancellationToken)
                        : AccountProxyResolver.BuildConnectionOptions(
                            selectedGlobal,
                            stableLoginKey);
                    if (selectedGlobal?.Kind == OutboundProxyKinds.Resin)
                    {
                        resinLease = new ResinLeaseControlSnapshot(
                            selectedGlobal.Id,
                            selectedGlobal.ResinAdminUrl,
                            selectedGlobal.ResinAdminToken,
                            selectedGlobal.ResinPlatform);
                    }
                    binding = new AccountProxyBindingInput("global");
                    // 使用当前配置/数据库快照，而不是让临时登录 ID 再次动态解析全局设置。
                    resolution = new AccountProxyResolution(globalProxy, false);
                    frozenStrategy = "global";
                    break;
                }

            case "existing":
                {
                    if (proxyId is not > 0)
                        throw new ArgumentException("请选择已有代理");

                    var proxy = await _proxyManagement.GetAsync(
                        proxyId.Value,
                        cancellationToken: cancellationToken);
                    if (proxy is not { IsEnabled: true })
                        throw new KeyNotFoundException("所选代理不存在或已停用");
                    if (proxy.Kind == OutboundProxyKinds.Warp
                        && _temporaryWarpClaims.OwnsRequest(proxy.WarpProfile?.RequestId))
                    {
                        throw new InvalidOperationException(
                            "所选 WARP 正被另一个账号首次连接流程使用，请稍后重试");
                    }

                    temporaryResinKey = proxy.Kind == OutboundProxyKinds.Resin
                            ? $"tg_login_{loginId}_{Guid.NewGuid():N}"
                            : null;
                    var stableLoginKey = temporaryResinKey ?? $"tg_login_{loginId}";
                    var connection = AccountProxyResolver.BuildConnectionOptions(
                        proxy,
                        stableLoginKey);

                    if (proxy.Kind == OutboundProxyKinds.Resin)
                    {
                        resinLease = new ResinLeaseControlSnapshot(
                            proxy.Id,
                            proxy.ResinAdminUrl,
                            proxy.ResinAdminToken,
                            proxy.ResinPlatform);
                    }

                    binding = new AccountProxyBindingInput("existing", proxy.Id);
                    resolution = new AccountProxyResolution(connection, false);
                    frozenStrategy = "existing";
                    break;
                }

            case "warp_pool":
                {
                    var proxy = (await ListAvailableWarpPoolAsync(cancellationToken)).First();
                    var connection = AccountProxyResolver.BuildConnectionOptions(
                        proxy,
                        $"tg_login_{loginId}");

                    binding = new AccountProxyBindingInput("existing", proxy.Id);
                    resolution = new AccountProxyResolution(connection, false);
                    frozenStrategy = "warp_pool";
                    break;
                }

            default:
                throw new ArgumentException(
                    "登录代理策略仅支持 direct、global、existing 或 warp_pool");
        }

        var state = new AccountLoginProxyState(
            loginId,
            binding,
            frozenStrategy,
            resolution,
            ownedWarpProxyId,
            resinLease,
            temporaryResinKey,
            DateTimeOffset.UtcNow);
        try
        {
            if (!_store.TryAdd(state, out var stateError))
            {
                await ReleaseStateResourcesAsync(
                    state,
                    keepOwnedWarp: false,
                    CancellationToken.None);
                throw new InvalidOperationException(
                    stateError ?? "登录代理会话无法保存，请稍后重试");
            }

            return state;
        }
        finally
        {
            warpRequestClaim?.Dispose();
        }
    }

    private async Task<IReadOnlyList<OutboundProxy>> ListAvailableWarpPoolAsync(
        CancellationToken cancellationToken)
    {
        var proxies = await _proxyManagement.ListAsync(cancellationToken);
        var candidates = proxies
            .Where(x => x.IsEnabled
                        && x.Kind == OutboundProxyKinds.Warp
                        && x.WarpProfile is
                        {
                            DesiredEnabled: true,
                            Status: "active"
                        }
                        && _store.CanUseWarpProxy(x.Id)
                        && !_temporaryWarpClaims.OwnsRequest(x.WarpProfile.RequestId))
            .OrderBy(x => x.Accounts.Count)
            .ThenBy(x => x.Id)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "没有可自动分配的已有 WARP；请先在代理管理中准备并启用 WARP，系统不会为登录创建新容器");
        }

        return candidates;
    }

    /// <summary>
    /// 将登录时使用的同一路由绑定到正式账号，完成前账号必须保持停用。
    /// </summary>
    public async Task CompleteAsync(
        int loginId,
        int accountId,
        CancellationToken cancellationToken = default)
    {
        // 原子取得会话所有权，避免过期清理或重复提交与正式绑定并发执行。
        if (!_store.TryTakeForCompletion(loginId, out var state) || state == null)
            throw new InvalidOperationException("登录代理会话已失效，请重新登录");

        try
        {
            await StopLoginClientsStrictAsync(loginId);
        }
        catch (Exception stopError)
        {
            var restored = _store.RestoreClaimedState(state);
            if (!restored)
            {
                _logger.LogCritical(
                    stopError,
                    "Failed to restore frozen proxy state for login {LoginId}",
                    loginId);
                _store.ReleasePhoneClaim(loginId);
            }

            _store.ReleaseLoginClaim(loginId);
            ReleaseFrozenWarpProxyClaim(state);
            throw;
        }

        var keepOwnedWarp = false;
        try
        {
            await ValidateRouteSnapshotAsync(state, cancellationToken);

            var bindingResult = await _proxyManagement.BindAccountsAsync(
                new[] { accountId },
                state.Binding,
                cancellationToken,
                expectedConnection: state.Resolution.Proxy);
            var item = bindingResult.Items.FirstOrDefault(x => x.AccountId == accountId);
            if (item?.Success != true)
            {
                throw new InvalidOperationException(
                    item?.Error ?? item?.Summary ?? "登录代理绑定失败");
            }

            // 一旦绑定提交，WARP 即归属于正式账号；即使后续启用失败也必须保留，
            // 避免账号指向已经删除的代理资源。
            keepOwnedWarp = state.OwnedWarpProxyId.HasValue;

            if (state.ResinLease != null
                && state.Resolution.Proxy != null
                && !string.IsNullOrWhiteSpace(state.TemporaryResinKey))
            {
                var inherited = await _proxyManagement.InheritImportResinLeaseBestEffortAsync(
                    state.Resolution.Proxy,
                    state.ResinLease.Platform,
                    state.TemporaryResinKey,
                    $"tg_account_{accountId}",
                    cancellationToken);
                if (!inherited)
                {
                    throw new InvalidOperationException(
                        "Resin 无法把登录出口继承给正式账号，已保持账号停用以避免切换 IP");
                }
            }

            // 只有同一路由绑定完成后才允许后台任务看到这个账号。
            await _accountManagement.SetAccountActiveStatusAsync(accountId, true);
        }
        catch
        {
            try
            {
                await _accountManagement.SetAccountActiveStatusAsync(accountId, false);
                await _proxyManagement.ReleaseAccountClientAsync(accountId);
            }
            catch (Exception cleanupError)
            {
                _logger.LogError(
                    cleanupError,
                    "Failed to keep account {AccountId} inactive after login proxy binding failure",
                    accountId);
            }

            throw;
        }
        finally
        {
            try
            {
                await ReleaseStateResourcesAsync(
                    state,
                    keepOwnedWarp,
                    CancellationToken.None);
            }
            finally
            {
                ReleaseFrozenWarpProxyClaim(state);
                _store.ReleasePhoneClaim(loginId);
                _store.ReleaseLoginClaim(loginId);
            }
        }
    }

    public async Task AbandonAsync(
        int loginId,
        CancellationToken cancellationToken = default)
    {
        if (!_store.TryTakeForCompletion(loginId, out var state) || state == null)
            return;

        var completed = false;
        try
        {
            try
            {
                await StopLoginClientsStrictAsync(loginId);
            }
            catch (Exception stopError)
            {
                var restored = _store.RestoreClaimedState(state);
                if (!restored)
                {
                    _logger.LogCritical(
                        stopError,
                        "Failed to restore abandoned login {LoginId}",
                        loginId);
                    _store.ReleasePhoneClaim(loginId);
                }
                throw;
            }

            await ReleaseStateResourcesAsync(
                state,
                keepOwnedWarp: false,
                cancellationToken);
            completed = true;
        }
        finally
        {
            ReleaseFrozenWarpProxyClaim(state);
            if (completed)
                _store.ReleasePhoneClaim(loginId);
            _store.ReleaseLoginClaim(loginId);
        }
    }

    internal async Task ReleaseClaimedStateAsync(
        AccountLoginProxyState state,
        CancellationToken cancellationToken = default)
    {
        var completed = false;
        try
        {
            await StopLoginClientsStrictAsync(state.LoginId);
            await ReleaseStateResourcesAsync(
                state,
                keepOwnedWarp: false,
                cancellationToken);
            completed = true;
        }
        catch (Exception cleanupError)
        {
            var restored = _store.RestoreClaimedState(state);
            if (!restored)
            {
                _logger.LogCritical(
                    cleanupError,
                    "Failed to restore cleanup state for login {LoginId}",
                    state.LoginId);
                _store.ReleasePhoneClaim(state.LoginId);
            }
            throw;
        }
        finally
        {
            ReleaseFrozenWarpProxyClaim(state);
            if (completed)
                _store.ReleasePhoneClaim(state.LoginId);
            _store.ReleaseLoginClaim(state.LoginId);
        }
    }

    private async Task StopLoginClientsStrictAsync(int loginId)
    {
        await _accountService.CancelQrLoginStrictAsync(loginId);
        await _accountService.ReleaseClientStrictAsync(loginId);
    }

    private void ReleaseFrozenWarpProxyClaim(AccountLoginProxyState state)
    {
        if (AccountLoginProxyStateStore.GetFrozenWarpProxyId(state) is { } proxyId)
            _store.ReleaseWarpProxyClaim(proxyId);
    }

    private async Task ReleaseStateResourcesAsync(
        AccountLoginProxyState state,
        bool keepOwnedWarp,
        CancellationToken cancellationToken)
    {
        await ReleaseTemporaryResinLeaseAsync(state, cancellationToken);

        if (!keepOwnedWarp && state.OwnedWarpProxyId is > 0)
        {
            await DeleteOwnedWarpBestEffortAsync(
                state.OwnedWarpProxyId.Value,
                cancellationToken);
        }
    }

    private async Task DeleteOwnedWarpBestEffortAsync(
        int proxyId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _proxyManagement.DeleteAsync(proxyId, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            // 已被其它清理流程删除。
        }
        catch (ProxyInUseException ex)
        {
            _logger.LogWarning(
                ex,
                "Login-created WARP proxy {ProxyId} is already bound and will be retained",
                proxyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to clean login-created WARP proxy {ProxyId}",
                proxyId);
        }
    }

    private async Task ValidateRouteSnapshotAsync(
        AccountLoginProxyState state,
        CancellationToken cancellationToken)
    {
        var strategy = state.Binding.Strategy.Trim().ToLowerInvariant();
        ProxyConnectionOptions? current;
        if (strategy == "global")
        {
            current = await _proxyManagement.ResolveGlobalProxyAsync(
                state.TemporaryResinKey ?? $"tg_login_{state.LoginId}",
                cancellationToken);
        }
        else if (strategy == "existing" && state.Binding.ProxyId is > 0)
        {
            var proxy = await _proxyManagement.GetAsync(
                state.Binding.ProxyId.Value,
                cancellationToken: cancellationToken);
            if (proxy is not { IsEnabled: true })
                throw new KeyNotFoundException("登录期间所选代理已被删除或停用");

            current = AccountProxyResolver.BuildConnectionOptions(
                proxy,
                state.TemporaryResinKey ?? $"tg_login_{state.LoginId}");
        }
        else
        {
            current = null;
        }

        if (!SameConnection(state.Resolution.Proxy, current))
        {
            throw new InvalidOperationException(
                "登录期间代理连接参数已变化，已阻止切换出口并保持账号停用，请重新登录");
        }
    }

    private static void ValidateFrozenSelection(
        AccountLoginProxyState state,
        string? strategy,
        int? proxyId)
    {
        var requestedStrategy = (strategy ?? string.Empty).Trim().ToLowerInvariant();
        var frozenStrategy = state.FrozenStrategy.Trim().ToLowerInvariant();
        var proxyMatches = frozenStrategy == "existing"
            ? proxyId == state.Binding.ProxyId
            : proxyId is not > 0;

        if (!string.Equals(
                requestedStrategy,
                frozenStrategy,
                StringComparison.Ordinal)
            || !proxyMatches)
        {
            throw new InvalidOperationException(
                "登录期间代理出口已经冻结，不能更换策略或代理；请先取消原登录会话");
        }
    }

    private static bool SameConnection(
        ProxyConnectionOptions? expected,
        ProxyConnectionOptions? current)
    {
        if (expected == null || current == null)
            return expected == null && current == null;

        return expected.ProxyId == current.ProxyId
               && string.Equals(expected.Kind, current.Kind, StringComparison.Ordinal)
               && string.Equals(expected.Protocol, current.Protocol, StringComparison.Ordinal)
               && string.Equals(expected.Host, current.Host, StringComparison.OrdinalIgnoreCase)
               && expected.Port == current.Port
               && string.Equals(expected.Username, current.Username, StringComparison.Ordinal)
               && string.Equals(expected.Password, current.Password, StringComparison.Ordinal)
               && string.Equals(expected.Secret, current.Secret, StringComparison.Ordinal);
    }

    private async Task ReleaseTemporaryResinLeaseAsync(
        AccountLoginProxyState state,
        CancellationToken cancellationToken)
    {
        if (state.ResinLease == null
            || string.IsNullOrWhiteSpace(state.TemporaryResinKey))
        {
            return;
        }

        await _proxyManagement.ReleaseImportResinLeaseBestEffortAsync(
            state.ResinLease,
            state.TemporaryResinKey,
            cancellationToken);
    }
}
