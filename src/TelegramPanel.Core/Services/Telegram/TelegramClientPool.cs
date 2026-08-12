using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TL;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// Telegram客户端池管理
/// 负责管理多个Telegram账号的客户端实例
/// </summary>
public class TelegramClientPool : ITelegramClientPool, IDisposable
{
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramClientPool> _logger;
    private readonly TelegramAccountUpdateHub _updateHub;
    private readonly IAccountProxyResolver _proxyResolver;
    private readonly ISessionPathResolver _sessionPathResolver;
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _removeAllLock = new(1, 1);
    private long _poolGeneration;
    private bool _clientCreationBlocked;
    private volatile bool _disposed;
    private static int _wtelegramLogConfigured;
    private readonly ConcurrentDictionary<int, UpdateManager> _updateManagers = new();

    public TelegramClientPool(
        IConfiguration configuration,
        ILogger<TelegramClientPool> logger,
        TelegramAccountUpdateHub updateHub,
        IAccountProxyResolver proxyResolver,
        ISessionPathResolver sessionPathResolver)
    {
        _configuration = configuration;
        _logger = logger;
        _updateHub = updateHub;
        _proxyResolver = proxyResolver;
        _sessionPathResolver = sessionPathResolver;
        ConfigureWTelegramLoggingOnce();
    }

    public int ActiveClientCount => _clients.Count;

    public Task<Client> GetOrCreateClientAsync(
        int accountId,
        int apiId,
        string apiHash,
        string sessionPath,
        string? sessionKey = null,
        string? phoneNumber = null,
        long? userId = null)
    {
        return GetOrCreateClientCoreAsync(
            accountId,
            apiId,
            apiHash,
            sessionPath,
            sessionKey,
            phoneNumber,
            userId,
            proxyOverride: null);
    }

    public Task<Client> GetOrCreateClientAsync(
        int accountId,
        int apiId,
        string apiHash,
        string sessionPath,
        string? sessionKey,
        string? phoneNumber,
        long? userId,
        AccountProxyResolution proxyResolution)
    {
        ArgumentNullException.ThrowIfNull(proxyResolution);
        return GetOrCreateClientCoreAsync(
            accountId,
            apiId,
            apiHash,
            sessionPath,
            sessionKey,
            phoneNumber,
            userId,
            proxyResolution);
    }

    private async Task<Client> GetOrCreateClientCoreAsync(
        int accountId,
        int apiId,
        string apiHash,
        string sessionPath,
        string? sessionKey,
        string? phoneNumber,
        long? userId,
        AccountProxyResolution? proxyOverride)
    {
        sessionPath = _sessionPathResolver.Resolve(sessionPath);

        Client? existingClient;
        var lockObj = GetAccountLock(accountId);
        await lockObj.WaitAsync();

        try
        {
            long creationGeneration;
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_clientCreationBlocked)
                {
                    throw new InvalidOperationException(
                        "Telegram 客户端池正在严格清理旧出口，已阻止创建新连接");
                }

                creationGeneration = _poolGeneration;

                // 读取现有客户端必须和全池失效操作使用同一把生命周期锁。
                // 这样返回发生在失效前时一定会进入清理快照，失效后则直接闭锁。
                if (_clients.TryGetValue(accountId, out existingClient)
                    && existingClient.User != null)
                {
                    if (proxyOverride != null)
                    {
                        throw new InvalidOperationException(
                            $"登录临时 ID {accountId} 已被现有客户端占用，已阻止复用未知出口");
                    }

                    return existingClient;
                }
            }

            // 客户端存在但未登录，移除并重新创建。这里不能调用
            // RemoveClientAsync，否则会在同一个账号锁上发生异步重入死锁。
            if (_clients.ContainsKey(accountId))
                await RemoveClientCoreAsync(accountId, throwOnDisposeFailure: true);

            _logger.LogInformation("Creating new Telegram client for account {AccountId}", accountId);

            // 手动登录使用尚未入库的临时 ID，必须优先采用调用方在首次连接前
            // 明确解析的路由，禁止因为数据库查不到账号而降级到全局或直连。
            var proxyResolution = proxyOverride
                                  ?? await _proxyResolver.ResolveAsync(accountId);
            var accountProxy = proxyResolution.Proxy
                               ?? (proxyResolution.UseGlobalProxy
                                   ? throw new InvalidOperationException(
                                       "全局代理路由未在客户端创建前解析，已阻止降级为直连；旧出口客户端不会被复用")
                                   : null);

            // 使用 config 回调设置 session 路径
            phoneNumber = NormalizePhone(phoneNumber);
            var deviceProfile = TelegramClientDeviceProfile.ForStableKey($"{apiId}:{phoneNumber}:{sessionPath}");
            string Config(string what)
            {
                return what switch
                {
                    "api_id" => apiId.ToString(),
                    "api_hash" => apiHash,
                    "session_pathname" => sessionPath,
                    "session_key" => string.IsNullOrWhiteSpace(sessionKey) ? null! : sessionKey,
                    "phone_number" => string.IsNullOrWhiteSpace(phoneNumber) ? null! : phoneNumber,
                    "user_id" => userId.HasValue && userId.Value > 0 ? userId.Value.ToString() : null!,
                    "app_id" => apiId.ToString(),
                    "app_hash" => apiHash,
                    "app_version" or "device_model" or "system_version" or "system_lang_code" or "lang_code" => deviceProfile.GetConfigValue(what)!,


                    _ => null!  // 使用 null! 抑制警告，这是 WTelegramClient 的预期行为
                };
            }

            var client = new Client(Config);
            TelegramClientProxyConfigurator.Apply(client, accountProxy);
            if (accountProxy is { Protocol: OutboundProxyProtocols.Http or OutboundProxyProtocols.Socks5 })
            {
                _logger.LogInformation(
                    "Account {AccountId} uses {ProxyKind} proxy {ProxyId} ({Protocol})",
                    accountId,
                    accountProxy.Kind,
                    accountProxy.ProxyId,
                    accountProxy.Protocol);
            }
            else if (accountProxy is { Protocol: OutboundProxyProtocols.MtProto })
            {
                _logger.LogInformation(
                    "Account {AccountId} uses MTProxy {ProxyId}",
                    accountId,
                    accountProxy.ProxyId);
            }
            TryRebindApiIdForLoadedSession(client, apiId);

            UpdateManager? updateManager = null;
            updateManager = client.WithUpdateManager(update => HandleTelegramUpdateAsync(accountId, updateManager!, update));

            // 设置日志回调
            client.OnOther += (update) =>
            {
                _logger.LogDebug("Account {AccountId} received update: {Update}", accountId, update.GetType().Name);
                return Task.CompletedTask;
            };

            var accepted = false;
            var staleGeneration = false;
            lock (_lifecycleGate)
            {
                staleGeneration = creationGeneration != _poolGeneration;
                if (!_disposed && !_clientCreationBlocked && !staleGeneration)
                {
                    _updateManagers[accountId] = updateManager;
                    _clients[accountId] = client;
                    accepted = true;
                }
            }

            if (!accepted)
            {
                await client.DisposeAsync();
                if (!_disposed)
                {
                    throw new InvalidOperationException(
                        staleGeneration
                            ? "全局连接配置已刷新，本次旧出口客户端创建已取消"
                            : "Telegram 客户端池正在严格清理旧出口，已阻止旧连接写回");
                }
                throw new ObjectDisposedException(nameof(TelegramClientPool));
            }
            return client;
        }
        finally
        {
            lockObj.Release();
        }
    }

    public async Task RemoveAllClientsAsync()
    {
        await _removeAllLock.WaitAsync();
        try
        {
            int[] accountIds;
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _clientCreationBlocked = true;
                _poolGeneration++;
                accountIds = _clients.Keys.ToArray();
            }

            var failures = new List<Exception>();
            foreach (var accountId in accountIds)
            {
                try
                {
                    await RemoveClientStrictAsync(accountId);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            lock (_lifecycleGate)
            {
                if (failures.Count == 0 && _clients.IsEmpty)
                {
                    _clientCreationBlocked = false;
                }
                else if (failures.Count == 0)
                {
                    failures.Add(new InvalidOperationException(
                        "严格清理完成后仍检测到旧 Telegram 客户端"));
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"有 {failures.Count} 个旧 Telegram 客户端无法确认已断开，客户端池已保持闭锁",
                    new AggregateException(failures));
            }
        }
        finally
        {
            _removeAllLock.Release();
        }
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var digits = new char[phone.Length];
        var count = 0;
        foreach (var ch in phone)
        {
            if (ch >= '0' && ch <= '9')
                digits[count++] = ch;
        }
        return count == 0 ? null : new string(digits, 0, count);
    }

    public Client? GetClient(int accountId)
    {
        return _clients.TryGetValue(accountId, out var client) ? client : null;
    }

    public async Task RemoveClientAsync(int accountId)
    {
        await RemoveClientWithLockAsync(accountId, throwOnDisposeFailure: false);
    }

    public async Task RemoveClientStrictAsync(int accountId)
    {
        await RemoveClientWithLockAsync(accountId, throwOnDisposeFailure: true);
    }

    private async Task RemoveClientWithLockAsync(
        int accountId,
        bool throwOnDisposeFailure)
    {
        var lockObj = GetAccountLock(accountId);
        await lockObj.WaitAsync();
        try
        {
            await RemoveClientCoreAsync(accountId, throwOnDisposeFailure);
        }
        finally
        {
            lockObj.Release();
        }
    }

    private SemaphoreSlim GetAccountLock(int accountId)
    {
        if (accountId <= 0)
            throw new ArgumentOutOfRangeException(nameof(accountId));

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        }
    }

    private async Task RemoveClientCoreAsync(
        int accountId,
        bool throwOnDisposeFailure = false)
    {
        Client? client;
        lock (_lifecycleGate)
        {
            // 所有释放路径都必须先保留引用，直到 DisposeAsync 确认成功。
            // 非严格清理可以不抛异常，但不能让随后执行的严格全池清理漏掉旧连接。
            if (!_clients.TryGetValue(accountId, out client))
                return;
        }

        _logger.LogInformation("Removing Telegram client for account {AccountId}", accountId);

        try
        {
            await client.DisposeAsync();
            lock (_lifecycleGate)
            {
                if (_clients.TryGetValue(accountId, out var current)
                    && ReferenceEquals(current, client))
                {
                    _clients.TryRemove(accountId, out _);
                    _updateManagers.TryRemove(accountId, out _);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing client for account {AccountId}", accountId);
            if (throwOnDisposeFailure)
            {
                throw new InvalidOperationException(
                    $"账号 {accountId} 的旧 Telegram 客户端无法确认已断开",
                    ex);
            }
        }
    }

    public bool IsClientConnected(int accountId)
    {
        return _clients.TryGetValue(accountId, out var client) && client.User != null;
    }

    public void Dispose()
    {
        Client[] clients;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            clients = _clients.Values.ToArray();
            _clients.Clear();
            _updateManagers.Clear();

            // 不主动 Dispose 账号锁。关闭过程中可能仍有异步创建/移除正在等待，
            // 提前释放 SemaphoreSlim 会让这些任务以 ObjectDisposedException 结束。
            _locks.Clear();
        }

        foreach (var client in clients)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // 忽略清理时的错误
            }
        }

        GC.SuppressFinalize(this);
    }

    private void TryRebindApiIdForLoadedSession(Client client, int desiredApiId)
    {
        try
        {
            var clientType = typeof(Client);
            var sessionField = clientType.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
            var sessionObj = sessionField?.GetValue(client);
            if (sessionObj == null)
                return;

            var sessionType = sessionObj.GetType();

            int? currentApiId = null;
            var apiIdField = sessionType.GetField("ApiId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (apiIdField?.FieldType == typeof(int))
                currentApiId = (int)apiIdField.GetValue(sessionObj)!;

            var apiIdProp = sessionType.GetProperty("ApiId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (currentApiId == null && apiIdProp?.PropertyType == typeof(int) && apiIdProp.CanRead)
                currentApiId = (int)apiIdProp.GetValue(sessionObj)!;

            if (currentApiId.HasValue && currentApiId.Value == desiredApiId)
                return;

            var updated = false;
            if (apiIdField?.FieldType == typeof(int))
            {
                apiIdField.SetValue(sessionObj, desiredApiId);
                updated = true;
            }
            else if (apiIdProp?.PropertyType == typeof(int) && apiIdProp.CanWrite)
            {
                apiIdProp.SetValue(sessionObj, desiredApiId);
                updated = true;
            }

            var clientApiIdField = clientType.GetField("_api_id", BindingFlags.Instance | BindingFlags.NonPublic);
            if (clientApiIdField?.FieldType == typeof(int))
            {
                clientApiIdField.SetValue(client, desiredApiId);
                updated = true;
            }

            if (!updated)
                return;

            var save = sessionType.GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic);
            if (save != null)
            {
                lock (sessionObj) save.Invoke(sessionObj, null);
            }

            _logger.LogInformation(
                "Rebound WTelegram session ApiId from {OldApiId} to {NewApiId} to apply new global Telegram API config",
                currentApiId,
                desiredApiId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to rebind WTelegram session ApiId (ignored)");
        }
    }

    private void ConfigureWTelegramLoggingOnce()
    {
        // WTelegramClient 默认会把大量底层网络/RPC trace 直接写到 Console，严重干扰面板日志查看。
        // 这里把它重定向到宿主 ILogger，以便用 Serilog 的 MinimumLevel/Override 控制输出量。
        if (Interlocked.Exchange(ref _wtelegramLogConfigured, 1) == 1)
            return;

        Helpers.Log = (level, message) =>
        {
            message = (message ?? "").TrimEnd();
            if (message.Length == 0)
                return;

            if (message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase)
                || message.Contains("RpcError", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("WTelegram({Level}): {Message}", level, message);
                return;
            }

            // WTelegram 的底层收发包 trace 量非常大。生产环境开启 Debug 时如果继续写入 stdout，
            // Docker 日志管道可能反压应用线程，表现为容器还在但 Kestrel 不再响应。
            // 这里默认丢弃底层 trace，只保留上面的 RPC 错误/限流日志。
        };
    }

    private async Task HandleTelegramUpdateAsync(int accountId, UpdateManager updateManager, Update update)
    {
        switch (update)
        {
            case UpdateNewChannelMessage { message: Message message }:
                await PublishMessageUpdateAsync(accountId, updateManager, message, isEdited: false);
                return;
            case UpdateNewMessage { message: Message message }:
                await PublishMessageUpdateAsync(accountId, updateManager, message, isEdited: false);
                return;
            case UpdateEditChannelMessage { message: Message message }:
                await PublishMessageUpdateAsync(accountId, updateManager, message, isEdited: true);
                return;
            case UpdateEditMessage { message: Message message }:
                await PublishMessageUpdateAsync(accountId, updateManager, message, isEdited: true);
                return;
        }
    }

    private async Task PublishMessageUpdateAsync(int accountId, UpdateManager updateManager, Message message, bool isEdited)
    {
        try
        {
            var senderUserId = (message.from_id as PeerUser)?.user_id;
            var senderChatId = (message.from_id as PeerChannel)?.channel_id
                               ?? (message.from_id as PeerChat)?.chat_id;
            User? sender = null;
            if (senderUserId.HasValue
                && updateManager.Users.TryGetValue(senderUserId.Value, out var userBase)
                && userBase is User user)
            {
                sender = user;
            }
            string? senderChatUsername = null;
            if (senderChatId.HasValue
                && updateManager.Chats.TryGetValue(senderChatId.Value, out var chatBase))
            {
                switch (chatBase)
                {
                    case Channel channel:
                        senderChatUsername = channel.username;
                        break;
                }
            }

            var buttons = ExtractInlineButtons(message.reply_markup);
            var reply = message.reply_to as MessageReplyHeader;
            var update = new TelegramAccountMessageUpdate(
                AccountId: accountId,
                ReceivedAtUtc: DateTimeOffset.UtcNow,
                IsEdited: isEdited,
                Message: message,
                SenderUserId: senderUserId,
                SenderUsername: sender?.username,
                SenderIsBot: sender?.IsBot == true,
                SenderChatId: senderChatId,
                SenderChatUsername: senderChatUsername,
                SenderPostAuthor: message.post_author,
                ReplyToMessageId: reply?.reply_to_msg_id,
                ThreadId: reply?.reply_to_top_id,
                Buttons: buttons);

            await _updateHub.PublishAsync(update);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish Telegram message update (accountId={AccountId}, messageId={MessageId})", accountId, message.id);
        }
    }

    private static IReadOnlyList<TelegramInlineButton> ExtractInlineButtons(ReplyMarkup? replyMarkup)
    {
        if (replyMarkup is not ReplyInlineMarkup inlineMarkup || inlineMarkup.rows == null || inlineMarkup.rows.Length == 0)
            return Array.Empty<TelegramInlineButton>();

        var result = new List<TelegramInlineButton>();
        var index = 0;
        for (var rowIndex = 0; rowIndex < inlineMarkup.rows.Length; rowIndex++)
        {
            var row = inlineMarkup.rows[rowIndex];
            var buttons = row?.buttons;
            if (buttons == null || buttons.Length == 0)
                continue;

            for (var columnIndex = 0; columnIndex < buttons.Length; columnIndex++)
            {
                if (buttons[columnIndex] is not KeyboardButtonCallback callback)
                    continue;

                var text = (callback.text ?? string.Empty).Trim();
                if (text.Length == 0 || callback.data == null || callback.data.Length == 0)
                    continue;

                result.Add(new TelegramInlineButton(index, rowIndex, columnIndex, text, callback.data));
                index++;
            }
        }

        return result;
    }
}
