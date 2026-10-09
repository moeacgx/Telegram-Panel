using Microsoft.EntityFrameworkCore;
using TelegramPanel.Core.Models;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Proxy;

public sealed partial class ProxyManagementService
{
    public const int MaxPerAccountProxyBatchSize = 100;
    public const int MaxPerAccountProxyTextLength = 100_000;
    private const int BatchProxyProbeConcurrency = 4;

    /// <summary>
    /// 为 ZIP 账号导入准备逐账号代理。全部代理先完成解析和出口检测，
    /// 只有全部成功后才会一次性新增或复用代理记录。
    /// </summary>
    public async Task<IReadOnlyList<PreparedAccountImportProxy>>
        PreparePerAccountImportProxiesAsync(
            string? text,
            int expectedCount,
            CancellationToken cancellationToken = default)
    {
        if (expectedCount <= 0)
            throw new AccountImportProxyBatchException("压缩包内没有可匹配的账号");

        var assignments = await PrepareAccountProxyAssignmentsCoreAsync(
            text,
            expectedCount,
            $"压缩包识别到 {expectedCount} 个账号",
            cancellationToken);
        return assignments
            .Select(item => new PreparedAccountImportProxy(
                item.Slot,
                item.SourceLine,
                item.ProxyId,
                item.ProxyName,
                item.EgressIp,
                item.ExpectedConnection))
            .ToArray();
    }

    /// <summary>
    /// 为已选择账号准备逐账号代理。全部代理先完成解析和出口检测，
    /// 只有全部成功后才会一次性新增或复用代理记录。
    /// </summary>
    public async Task<IReadOnlyList<PreparedAccountProxyAssignment>>
        PrepareAccountProxyAssignmentsAsync(
            string? text,
            int expectedCount,
            CancellationToken cancellationToken = default)
    {
        if (expectedCount <= 0)
            throw new AccountImportProxyBatchException("请选择账号");

        return await PrepareAccountProxyAssignmentsCoreAsync(
            text,
            expectedCount,
            $"已选择 {expectedCount} 个账号",
            cancellationToken);
    }

    private async Task<IReadOnlyList<PreparedAccountProxyAssignment>>
        PrepareAccountProxyAssignmentsCoreAsync(
            string? text,
            int expectedCount,
            string expectedCountDescription,
            CancellationToken cancellationToken)
    {
        if (expectedCount > MaxPerAccountProxyBatchSize)
        {
            throw new AccountImportProxyBatchException(
                $"逐账号批量代理单次最多处理 {MaxPerAccountProxyBatchSize} 个账号");
        }

        var orderedInputs = ParseOrderedImportInputs(text);
        if (orderedInputs.Count != expectedCount)
        {
            throw new AccountImportProxyBatchException(
                $"账号与代理数量不一致：{expectedCountDescription}，代理文本识别到 {orderedInputs.Count} 条有效代理");
        }

        EnsureNoCredentialConflicts(orderedInputs);
        var uniqueInputs = orderedInputs
            .GroupBy(item => item.ConnectionKey)
            .Select(group => group.First())
            .ToArray();
        foreach (var item in uniqueInputs)
            await ValidateUnmanagedEndpointAsync(item.Input.Host!, item.Input.Port, cancellationToken);
        var probes = await ProbeImportInputsAsync(uniqueInputs, cancellationToken);
        ThrowIfProbeFailed(orderedInputs, probes);

        await using var mutationLease = await AcquireMutationLeaseAsync(cancellationToken);
        foreach (var item in uniqueInputs)
            await ValidateUnmanagedEndpointAsync(item.Input.Host!, item.Input.Port, cancellationToken);
        var existingManualProxies = await _db.OutboundProxies
            .Where(proxy => proxy.Kind == OutboundProxyKinds.Manual)
            .ToListAsync(cancellationToken);
        var existingByConnection = ResolveExistingImportProxies(
            uniqueInputs,
            existingManualProxies);

        var now = DateTime.UtcNow;
        var proxyByConnection = new Dictionary<ProxyImportConnectionKey, OutboundProxy>();
        foreach (var item in uniqueInputs)
        {
            var probe = probes[item.ConnectionKey];
            if (!existingByConnection.TryGetValue(item.ConnectionKey, out var proxy))
            {
                var input = item.Input;
                proxy = new OutboundProxy
                {
                    Name = input.Name!,
                    Kind = input.Kind!,
                    Protocol = input.Protocol!,
                    Host = input.Host!,
                    Port = input.Port,
                    Username = input.Username,
                    Password = input.Password,
                    Secret = input.Secret,
                    IsEnabled = true,
                    TestStatus = "ok",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                _db.OutboundProxies.Add(proxy);
            }

            ApplySuccessfulProbe(proxy, probe, now);
            proxyByConnection[item.ConnectionKey] = proxy;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return orderedInputs
            .Select((item, index) =>
            {
                var proxy = proxyByConnection[item.ConnectionKey];
                return new PreparedAccountProxyAssignment(
                    index + 1,
                    item.SourceLine,
                    proxy.Id,
                    item.Input.Name!,
                    proxy.EgressIp,
                    BuildPhysicalConnectionOptions(proxy));
            })
            .ToArray();
    }

    private static IReadOnlyList<OrderedProxyImportInput> ParseOrderedImportInputs(string? text)
    {
        text ??= string.Empty;
        if (text.Length > MaxPerAccountProxyTextLength)
            throw new AccountImportProxyBatchException("批量代理文本不能超过 100000 个字符");

        var result = new List<OrderedProxyImportInput>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (result.Count >= MaxPerAccountProxyBatchSize)
            {
                throw new AccountImportProxyBatchException(
                    $"逐账号批量代理单次最多处理 {MaxPerAccountProxyBatchSize} 条代理");
            }

            OutboundProxyInput normalized;
            try
            {
                normalized = NormalizeInput(
                    ParseImportLine(line, testAfterImport: false),
                    existing: null);
            }
            catch (ArgumentException ex)
            {
                throw new AccountImportProxyBatchException(
                    $"第 {index + 1} 行代理格式无效：{SanitizeBatchError(ex.Message, null)}");
            }

            if (ContainsControlCharacters(normalized.Username)
                || ContainsControlCharacters(normalized.Password)
                || ContainsControlCharacters(normalized.Secret))
            {
                throw new AccountImportProxyBatchException(
                    $"第 {index + 1} 行代理认证信息包含不支持的控制字符");
            }

            if (normalized.Kind != OutboundProxyKinds.Manual
                || normalized.Protocol is not (
                    OutboundProxyProtocols.Http or OutboundProxyProtocols.Socks5))
            {
                throw new AccountImportProxyBatchException(
                    $"第 {index + 1} 行代理不支持出口检测；逐账号批量代理仅支持 HTTP 或 SOCKS5");
            }

            var identityKey = ProxyImportIdentityKey.From(normalized);
            var connectionKey = ProxyImportConnectionKey.From(identityKey, normalized);
            result.Add(new OrderedProxyImportInput(
                index + 1,
                normalized,
                identityKey,
                connectionKey));
        }

        if (result.Count == 0)
            throw new AccountImportProxyBatchException("请填写逐账号批量代理，每行一个代理地址");
        return result;
    }

    private static void EnsureNoCredentialConflicts(
        IReadOnlyList<OrderedProxyImportInput> inputs)
    {
        foreach (var group in inputs.GroupBy(item => item.IdentityKey))
        {
            if (group.Select(item => item.ConnectionKey).Distinct().Skip(1).Any())
            {
                var lines = string.Join("、", group.Select(item => item.SourceLine));
                throw new AccountImportProxyBatchException(
                    $"第 {lines} 行使用了相同代理端点但认证信息不同，无法安全复用同一代理记录");
            }
        }
    }

    private async Task<IReadOnlyDictionary<ProxyImportConnectionKey, EgressProbeResult>>
        ProbeImportInputsAsync(
            IReadOnlyList<OrderedProxyImportInput> inputs,
            CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(
            BatchProxyProbeConcurrency,
            BatchProxyProbeConcurrency);
        var tasks = inputs.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var input = item.Input;
                var options = new ProxyConnectionOptions(
                    0,
                    $"批量代理第 {item.SourceLine} 行",
                    input.Kind!,
                    input.Protocol!,
                    input.Host!,
                    input.Port,
                    input.Username,
                    input.Password,
                    input.Secret);
                var probe = await _probeService.ProbeProxyAsync(
                    options,
                    requireWarp: false,
                    cancellationToken);
                return (item.ConnectionKey, Probe: probe);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(item => item.ConnectionKey, item => item.Probe);
    }

    private static void ThrowIfProbeFailed(
        IReadOnlyList<OrderedProxyImportInput> inputs,
        IReadOnlyDictionary<ProxyImportConnectionKey, EgressProbeResult> probes)
    {
        var failures = inputs
            .Where(item => !probes[item.ConnectionKey].Success)
            .DistinctBy(item => item.ConnectionKey)
            .Take(10)
            .Select(item =>
            {
                var probe = probes[item.ConnectionKey];
                var error = SanitizeBatchError(probe.Error, item.Input);
                return $"第 {item.SourceLine} 行：{error}";
            })
            .ToArray();
        if (failures.Length == 0)
            return;

        throw new AccountImportProxyBatchException(
            $"批量代理检测未全部通过，尚未连接任何 Telegram 账号：{string.Join("；", failures)}");
    }

    private static IReadOnlyDictionary<ProxyImportConnectionKey, OutboundProxy>
        ResolveExistingImportProxies(
            IReadOnlyList<OrderedProxyImportInput> inputs,
            IReadOnlyList<OutboundProxy> existingProxies)
    {
        var result = new Dictionary<ProxyImportConnectionKey, OutboundProxy>();
        foreach (var item in inputs)
        {
            var matches = existingProxies
                .Where(proxy => item.IdentityKey.Matches(proxy))
                .ToArray();
            if (matches.Length > 1)
            {
                throw new AccountImportProxyBatchException(
                    $"第 {item.SourceLine} 行对应的代理端点在代理库中存在重复记录，请先合并后重试");
            }
            if (matches.Length == 0)
                continue;

            var existing = matches[0];
            if (!item.ConnectionKey.MatchesCredentials(existing))
            {
                throw new AccountImportProxyBatchException(
                    $"第 {item.SourceLine} 行对应的代理已存在，但认证信息不同；为避免影响已绑定账号，未自动覆盖");
            }
            if (!existing.IsEnabled)
            {
                throw new AccountImportProxyBatchException(
                    $"第 {item.SourceLine} 行对应的代理已存在但已停用，请先启用后重试");
            }

            result[item.ConnectionKey] = existing;
        }

        return result;
    }

    private static void ApplySuccessfulProbe(
        OutboundProxy proxy,
        EgressProbeResult probe,
        DateTime now)
    {
        proxy.TestStatus = "ok";
        proxy.LastError = null;
        proxy.LastLatencyMs = probe.LatencyMs;
        proxy.EgressIp = probe.Ip;
        proxy.EgressCountry = probe.Country;
        proxy.EgressCity = probe.City;
        proxy.EgressIsp = probe.Isp;
        proxy.LastTestedAtUtc = probe.CheckedAtUtc;
        proxy.UpdatedAtUtc = now;
    }

    private static string SanitizeBatchError(
        string? error,
        OutboundProxyInput? input)
    {
        var sanitized = string.IsNullOrWhiteSpace(error)
            ? "代理出口检测失败"
            : error.Trim();
        var sensitiveValues = new[] { input?.Username, input?.Password, input?.Secret }
            .Where(value => !string.IsNullOrEmpty(value))
            .SelectMany(value => new[] { value!, Uri.EscapeDataString(value!) })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length);
        foreach (var secret in sensitiveValues)
        {
            sanitized = sanitized.Replace(secret, "***", StringComparison.Ordinal);
        }
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= 300 ? sanitized : sanitized[..300];
    }

    private static bool ContainsControlCharacters(string? value) =>
        !string.IsNullOrEmpty(value) && value.Any(char.IsControl);

    private sealed record OrderedProxyImportInput(
        int SourceLine,
        OutboundProxyInput Input,
        ProxyImportIdentityKey IdentityKey,
        ProxyImportConnectionKey ConnectionKey);

    private sealed record ProxyImportIdentityKey(
        string Kind,
        string Protocol,
        string Host,
        int Port,
        string? Username,
        string? ResinPlatform)
    {
        public static ProxyImportIdentityKey From(OutboundProxyInput input) =>
            new(
                input.Kind!,
                input.Protocol!,
                input.Host!,
                input.Port,
                input.Username,
                input.ResinPlatform);

        public bool Matches(OutboundProxy proxy) =>
            string.Equals(Kind, proxy.Kind, StringComparison.Ordinal)
            && string.Equals(Protocol, proxy.Protocol, StringComparison.Ordinal)
            && string.Equals(Host, proxy.Host, StringComparison.OrdinalIgnoreCase)
            && Port == proxy.Port
            && string.Equals(Username, proxy.Username, StringComparison.Ordinal)
            && string.Equals(ResinPlatform, proxy.ResinPlatform, StringComparison.Ordinal);
    }

    private sealed record ProxyImportConnectionKey(
        ProxyImportIdentityKey Identity,
        string? Password,
        string? Secret)
    {
        public static ProxyImportConnectionKey From(
            ProxyImportIdentityKey identity,
            OutboundProxyInput input) =>
            new(identity, input.Password, input.Secret);

        public bool MatchesCredentials(OutboundProxy proxy) =>
            string.Equals(Password, proxy.Password, StringComparison.Ordinal)
            && string.Equals(Secret, proxy.Secret, StringComparison.Ordinal);
    }
}
