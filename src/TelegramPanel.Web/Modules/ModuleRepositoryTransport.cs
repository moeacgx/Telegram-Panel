using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace TelegramPanel.Web.Modules;

/// <summary>仓库下载专用通道：固定已检查的公网地址，禁止代理、自动重定向及凭据跨源传播。</summary>
public sealed class ModuleRepositoryTransport
{
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    public ModuleRepositoryTransport() { }
    internal ModuleRepositoryTransport(Func<HttpMessageHandler> handlerFactory) => _handlerFactory = handlerFactory;
    public static Uri ValidateUrl(string value)
    {
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("仓库和下载地址必须是 HTTPS（443 端口），不能包含账号密码或片段");
        if (IPAddress.TryParse(uri.DnsSafeHost, out var address) && !IsPublicAddress(address))
            throw new InvalidOperationException("仓库地址不能指向本机或内网");
        return uri;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
                && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || (b[1] == 88 && b[2] == 99)))
                && !(b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100)))
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        // 仅允许全球单播，排除文档、Teredo、6to4 等隧道及特殊分配地址。
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] == 1 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8)))
            && !(b[0] == 0x20 && b[1] == 2)
            && !(b[0] == 0x3f && b[1] == 0xff);
    }

    internal static bool CanSendToken(Uri uri, StoredModuleRepository repository)
    {
        var index = ModuleRepositoryStore.IndexUri(repository);
        if (uri.GetLeftPart(UriPartial.Authority) != index.GetLeftPart(UriPartial.Authority)) return false;
        return repository.Kind != "github"
            || uri.AbsolutePath.StartsWith($"/repos/{repository.Location}/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<byte[]> DownloadAsync(Uri initial, StoredModuleRepository repository, string? token, int limit, bool index, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var handler = _handlerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectAsync
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var uri = initial;
        var credentialsAllowed = true;
        for (var hop = 0; hop <= 5; hop++)
        {
            ValidateUrl(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("TelegramPanel-ModuleRepository/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(index ? "application/vnd.github.raw+json" : "application/octet-stream"));
            if (credentialsAllowed && !string.IsNullOrEmpty(token) && CanSendToken(uri, repository))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ?? throw new InvalidOperationException("仓库重定向缺少目标地址");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.GetLeftPart(UriPartial.Authority) != next.GetLeftPart(UriPartial.Authority)) credentialsAllowed = false;
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"仓库请求失败（HTTP {(int)response.StatusCode}）；请检查地址、分支与令牌读取权限");
            if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("仓库响应超过大小限制");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (output.Length + read > limit) throw new InvalidOperationException("仓库响应超过大小限制");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new InvalidOperationException("仓库重定向次数过多");
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a)))
            throw new InvalidOperationException("仓库域名解析到本机、内网或保留地址，已拒绝连接");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("无法连接模块仓库");
    }
}
