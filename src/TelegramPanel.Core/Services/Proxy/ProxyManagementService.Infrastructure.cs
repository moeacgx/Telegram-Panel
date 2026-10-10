using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Models;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Proxy;

public sealed partial class ProxyManagementService
{
    private const int MaxResinControlPlaneResponseBytes = 1024 * 1024;

    private async Task<OutboundProxy> TestAsyncCore(
        OutboundProxy proxy,
        CancellationToken cancellationToken)
    {
        AccountProxyResolver.RejectRetiredWarp(proxy);
        var probeKey = $"telegram_panel_probe_{proxy.Id}";
        try
        {
            EgressProbeResult result;
            if (proxy.Kind == OutboundProxyKinds.Resin)
            {
                var controlError = await ValidateResinControlPlaneAsync(proxy, cancellationToken);
                result = controlError == null
                    ? await _probeService.ProbeProxyAsync(
                        proxy,
                        probeKey,
                        cancellationToken)
                    : new EgressProbeResult(
                        false,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        DateTime.UtcNow,
                        controlError);
            }
            else
            {
                result = await _probeService.ProbeProxyAsync(
                    proxy,
                    probeKey,
                    cancellationToken);
            }

            proxy.TestStatus = result.Success ? "ok" : "fail";
            proxy.LastError = result.Error;
            proxy.LastLatencyMs = result.Success ? result.LatencyMs : null;
            proxy.LastTestedAtUtc = result.CheckedAtUtc;
            proxy.UpdatedAtUtc = DateTime.UtcNow;
            if (result.Success)
            {
                proxy.EgressIp = result.Ip;
                proxy.EgressCountry = result.Country;
                proxy.EgressCity = result.City;
                proxy.EgressIsp = result.Isp;
            }
            else
            {
                proxy.EgressIp = null;
                proxy.EgressCountry = null;
                proxy.EgressCity = null;
                proxy.EgressIsp = null;
            }

            await _db.SaveChangesAsync(cancellationToken);
            return proxy;
        }
        finally
        {
            if (proxy.Kind == OutboundProxyKinds.Resin)
            {
                await ReleaseResinLeaseBestEffortAsync(
                    proxy,
                    probeKey,
                    $"检测身份 {probeKey}",
                    CancellationToken.None);
            }
        }
    }

    private async Task<string?> ValidateResinControlPlaneAsync(
        OutboundProxy proxy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(proxy.ResinAdminUrl))
            return null;

        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var client = new HttpClient(handler)
            {
                BaseAddress = BuildResinAdminBaseAddress(proxy.ResinAdminUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var healthRequest = new HttpRequestMessage(HttpMethod.Get, "healthz");
            using var health = await client.SendAsync(
                healthRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!health.IsSuccessStatusCode)
                return $"Resin 健康检查返回 HTTP {(int)health.StatusCode}";

            if (!string.IsNullOrWhiteSpace(proxy.ResinAdminToken))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    proxy.ResinAdminToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/system/info");
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return $"Resin 管理鉴权返回 HTTP {(int)response.StatusCode}";

                var authVersionError = await ValidateResinAuthVersionAsync(
                    response,
                    cancellationToken);
                if (authVersionError != null)
                    return authVersionError;

                var platformId = await ResolveResinPlatformIdAsync(
                    client,
                    proxy,
                    cancellationToken);
                if (platformId == null)
                    return $"Resin Platform {proxy.ResinPlatform ?? "Default"} 不存在";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Resin 控制面不可用：{SafeError(ex)}";
        }
    }

    private static Uri BuildResinAdminBaseAddress(string adminUrl) =>
        new($"{adminUrl.TrimEnd('/')}/", UriKind.Absolute);

    private static async Task<string?> ValidateResinAuthVersionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("auth_version", out var authVersionElement)
            || authVersionElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var authVersion = authVersionElement.GetString();
        return string.IsNullOrWhiteSpace(authVersion)
               || string.Equals(authVersion, "V1", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Resin 当前认证模式为 {authVersion}，账号粘性接入需要 RESIN_AUTH_VERSION=V1";
    }

    private async Task ReleaseResinLeaseAsync(
        OutboundProxy proxy,
        int accountId,
        CancellationToken cancellationToken) =>
        await ReleaseResinLeaseAsync(
            proxy,
            $"tg_account_{accountId}",
            $"账号 {accountId}",
            cancellationToken);

    public async Task ReleaseImportResinLeaseBestEffortAsync(
        ResinLeaseControlSnapshot snapshot,
        string stableImportKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(stableImportKey))
            return;

        // 不重新查询数据库：代理可能已被删除或修改，必须使用创建 Lease 时的旧控制面身份。
        var proxy = new OutboundProxy
        {
            Id = snapshot.ProxyId,
            Kind = OutboundProxyKinds.Resin,
            ResinAdminUrl = snapshot.AdminUrl,
            ResinAdminToken = snapshot.AdminToken,
            ResinPlatform = snapshot.Platform
        };

        await ReleaseResinLeaseBestEffortAsync(
            proxy,
            stableImportKey.Trim(),
            $"导入身份 {stableImportKey.Trim()}",
            cancellationToken);
    }

    /// <summary>
    /// 将导入验证阶段的临时 Resin Lease 复制到账号的稳定身份。
    /// Resin 的该接口使用数据面 Proxy Token，而不是 Admin Token。
    /// </summary>
    public async Task<bool> InheritImportResinLeaseBestEffortAsync(
        ProxyConnectionOptions connection,
        string? platform,
        string temporaryAccountKey,
        string stableAccountKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Kind != OutboundProxyKinds.Resin
            || string.IsNullOrWhiteSpace(connection.Host)
            || connection.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(temporaryAccountKey)
            || string.IsNullOrWhiteSpace(stableAccountKey))
        {
            return false;
        }

        temporaryAccountKey = temporaryAccountKey.Trim();
        stableAccountKey = stableAccountKey.Trim();
        if (string.Equals(temporaryAccountKey, stableAccountKey, StringComparison.Ordinal))
            return true;

        var platformName = string.IsNullOrWhiteSpace(platform) ? "Default" : platform.Trim();
        try
        {
            await SendResinLeaseInheritanceAsync(
                connection,
                platformName,
                temporaryAccountKey,
                stableAccountKey,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 兼容尚未提供 inherit-lease Action 的旧版 Resin；导入仍可使用最终稳定身份。
            _logger.LogWarning(
                ex,
                "Failed to inherit Resin lease {TemporaryAccountKey} to "
                + "{StableAccountKey} for proxy {ProxyId}",
                temporaryAccountKey,
                stableAccountKey,
                connection.ProxyId);
            return false;
        }
    }

    private static async Task SendResinLeaseInheritanceAsync(
        ProxyConnectionOptions connection,
        string platform,
        string temporaryAccountKey,
        string stableAccountKey,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new UriBuilder(
                Uri.UriSchemeHttp,
                connection.Host,
                connection.Port).Uri,
            Timeout = TimeSpan.FromSeconds(10)
        };
        var body = JsonSerializer.Serialize(new
        {
            parent_account = temporaryAccountKey,
            new_account = stableAccountKey
        });
        // Resin 在 Proxy Token 为空时仍接受任意占位路径段调用该 Action。
        var proxyTokenPath = Uri.EscapeDataString(
            string.IsNullOrEmpty(connection.Password) ? "telegram-panel" : connection.Password);
        var platformPath = Uri.EscapeDataString(platform);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/{proxyTokenPath}/api/v1/{platformPath}/actions/inherit-lease")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Resin Lease 继承返回 HTTP {(int)response.StatusCode}");
        }
    }

    internal Task ReleaseAccountClientAsync(int accountId)
    {
        return accountId > 0
            ? _clientPool.RemoveClientAsync(accountId)
            : Task.CompletedTask;
    }

    internal Task ReleaseAccountClientStrictAsync(int accountId)
    {
        return accountId > 0
            ? _clientPool.RemoveClientStrictAsync(accountId)
            : Task.CompletedTask;
    }

    private async Task ReleaseResinLeaseAsync(
        OutboundProxy proxy,
        string accountKey,
        string subject,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(proxy.ResinAdminUrl)
            || string.IsNullOrWhiteSpace(proxy.ResinAdminToken))
        {
            return;
        }

        var platform = string.IsNullOrWhiteSpace(proxy.ResinPlatform)
            ? "Default"
            : proxy.ResinPlatform;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = BuildResinAdminBaseAddress(proxy.ResinAdminUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            proxy.ResinAdminToken);
        var platformId = await ResolveResinPlatformIdAsync(client, proxy, cancellationToken);
        if (platformId == null)
        {
            _logger.LogInformation(
                "Resin Platform {Platform} no longer exists; lease {AccountKey} is already absent",
                platform,
                accountKey);
            return;
        }

        accountKey = Uri.EscapeDataString(accountKey);
        var platformKey = Uri.EscapeDataString(platformId);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"api/v1/platforms/{platformKey}/leases/{accountKey}");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Resin Lease 释放返回 HTTP {(int)response.StatusCode}，{subject} 的状态保持不变");
        }
    }

    private async Task ReleaseResinLeaseBestEffortAsync(
        OutboundProxy proxy,
        int accountId,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReleaseResinLeaseAsync(proxy, accountId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to release Resin lease for account {AccountId}",
                accountId);
        }
    }

    private async Task ReleaseResinLeaseBestEffortAsync(
        OutboundProxy proxy,
        string accountKey,
        string subject,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReleaseResinLeaseAsync(proxy, accountKey, subject, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to release Resin lease {AccountKey} for proxy {ProxyId}",
                accountKey,
                proxy.Id);
        }
    }

    private static async Task<string?> ResolveResinPlatformIdAsync(
        HttpClient client,
        OutboundProxy proxy,
        CancellationToken cancellationToken)
    {
        const int pageLimit = 100;
        const int maxPages = 100;
        var platformName = string.IsNullOrWhiteSpace(proxy.ResinPlatform)
            ? "Default"
            : proxy.ResinPlatform.Trim();
        var keyword = Uri.EscapeDataString(platformName);
        for (var page = 0; page < maxPages; page++)
        {
            var offset = page * pageLimit;
            var path = $"api/v1/platforms?keyword={keyword}"
                       + $"&limit={pageLimit}&offset={offset}";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Resin Platform 查询返回 HTTP {(int)response.StatusCode}");
            }

            using var document = await ReadLimitedResinJsonAsync(
                response,
                "Resin Platform 查询",
                cancellationToken);
            if (!document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Resin Platform 查询响应缺少 items 数组");
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var name)
                    || !string.Equals(
                        name.GetString(),
                        platformName,
                        StringComparison.OrdinalIgnoreCase)
                    || !item.TryGetProperty("id", out var id)
                    || string.IsNullOrWhiteSpace(id.GetString()))
                {
                    continue;
                }
                return id.GetString();
            }

            var itemCount = items.GetArrayLength();
            if (itemCount < pageLimit || ReachedResinPlatformTotal(document, offset, itemCount))
                return null;
        }

        throw new InvalidDataException($"Resin Platform 查询超过 {maxPages * pageLimit} 条上限");
    }

    private static bool ReachedResinPlatformTotal(
        JsonDocument document,
        int offset,
        int itemCount)
    {
        return document.RootElement.TryGetProperty("total", out var totalElement)
               && totalElement.TryGetInt32(out var total)
               && offset + itemCount >= total;
    }

    private static async Task<JsonDocument> ReadLimitedResinJsonAsync(
        HttpResponseMessage response,
        string subject,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResinControlPlaneResponseBytes)
        {
            throw new InvalidDataException(
                $"{subject}响应超过 {MaxResinControlPlaneResponseBytes / 1024}KB 限制");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var limited = new MemoryStream(capacity: Math.Min(
            (int)(response.Content.Headers.ContentLength ?? 0),
            MaxResinControlPlaneResponseBytes));
        var buffer = new byte[81920];
        var totalBytes = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            totalBytes += read;
            if (totalBytes > MaxResinControlPlaneResponseBytes)
            {
                throw new InvalidDataException(
                    $"{subject}响应超过 {MaxResinControlPlaneResponseBytes / 1024}KB 限制");
            }

            await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        limited.Position = 0;
        return await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken);
    }

    private async Task DeleteUnusedProxyCoreAsync(
        OutboundProxy proxy,
        CancellationToken cancellationToken)
    {
        if (IsEnabledGlobalProxy(proxy.Id))
            throw new ProxyInUseException("该代理正在作为账号全局代理使用");

        var stillUsed = await _db.Accounts.AnyAsync(
            x => x.ProxyId == proxy.Id,
            cancellationToken);
        if (stillUsed)
            throw new ProxyInUseException("代理仍被账号使用");

        // 历史容器已由部署迁移清理；此处只删除未被使用的数据库记录。
        if (proxy.WarpProfile != null)
            _db.WarpProfiles.Remove(proxy.WarpProfile);

        _db.OutboundProxies.Remove(proxy);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string SafeError(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception;
             current != null && messages.Count < 3;
             current = current.InnerException)
        {
            var message = current.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (message.Length > 500)
                message = message[..500];
            if (message.Length > 0)
                messages.Add(message);
        }
        return messages.Count == 0 ? "未知错误" : string.Join(" | ", messages.Distinct());
    }
}
