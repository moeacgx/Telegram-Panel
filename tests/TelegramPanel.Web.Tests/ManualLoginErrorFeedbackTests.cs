using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Reflection;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Services.Telegram;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

[Collection("WTelegram 日志隔离")]
public sealed class ManualLoginErrorFeedbackTests
{
    [Fact]
    public void 底层Rpc日志也会遮蔽人机验证挑战串()
    {
        // WTelegram 日志是进程级委托；独占执行并在测试结束后恢复原状态。
        var configuredField = typeof(TelegramClientPool).GetField("_wtelegramLogConfigured", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldConfigured = configuredField.GetValue(null);
        var oldLog = Helpers.Log;
        var logger = new CapturingLogger<TelegramClientPool>();
        var configuration = new ConfigurationBuilder().Build();
        try
        {
            configuredField.SetValue(null, 0);
            using var pool = new TelegramClientPool(configuration, logger, new TelegramAccountUpdateHub(),
                new UnusedProxyResolver(), new SessionPathResolver(configuration));
            Helpers.Log(4, "RpcError 400 RECAPTCHA_CHECK_signup__test-challenge-key");
            Helpers.Log(4, "RpcError 420 FLOOD_WAIT_30");

            Assert.DoesNotContain("test-challenge-key", string.Join("\n", logger.Messages));
            Assert.Contains(logger.Messages, message => message.Contains("人机验证"));
            Assert.Contains(logger.Messages, message => message.Contains("FLOOD_WAIT_30"));
        }
        finally
        {
            Helpers.Log = oldLog;
            configuredField.SetValue(null, oldConfigured);
        }
    }

    [Theory]
    [InlineData("RECAPTCHA_CHECK_signup__test-challenge-key")]
    [InlineData("RECAPTCHA_CHECK_login__test-challenge-key")]
    [InlineData("RECAPTCHA_CHECK_signup__")]
    public async Task 官方人机验证失败会解释限制且不会输出原始挑战串(string challenge)
    {
        var (result, pool, logger) = await StartWithErrorAsync(new TL.RpcException(400, challenge));

        Assert.False(result.Success);
        Assert.Null(result.NextStep);
        Assert.Null(result.Account);
        Assert.Contains("人机验证", result.Message);
        Assert.Contains("官方 Telegram 客户端", result.Message);
        Assert.Contains("无法", result.Message);
        Assert.DoesNotContain(challenge, result.Message);
        Assert.DoesNotContain("test-challenge-key", string.Join("\n", logger.Messages));
        Assert.DoesNotContain(challenge, string.Join("\n", logger.Messages));
        Assert.Contains(logger.Messages, message => message.Contains("人机验证"));
        Assert.Equal(1, pool.CreateCount);
        Assert.Equal(new[] { 90173 }, pool.RemovedIds);
    }

    [Fact]
    public async Task 普通登录失败保留原有提示和异常诊断()
    {
        var (result, pool, logger) = await StartWithErrorAsync(new TL.RpcException(420, "FLOOD_WAIT_30"));

        Assert.False(result.Success);
        Assert.Null(result.NextStep);
        Assert.Contains("FLOOD_WAIT_30", result.Message);
        Assert.Contains(logger.Messages, message => message.Contains("FLOOD_WAIT_30"));
        Assert.Equal(1, pool.CreateCount);
        Assert.Equal(new[] { 90173 }, pool.RemovedIds);
    }

    private static async Task<(LoginResult Result, FailingClientPool Pool, CapturingLogger<AccountService> Logger)> StartWithErrorAsync(Exception error)
    {
        var sessionsPath = Path.Combine(Path.GetTempPath(), $"telegram-panel-login-error-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:SessionsPath"] = sessionsPath })
            .Build();
        var pool = new FailingClientPool(error);
        var logger = new CapturingLogger<AccountService>();
        var service = new AccountService(pool, logger, configuration);
        try
        {
            var result = await service.StartLoginAsync(
                90173,
                "+8613800000000",
                new AccountProxyResolution(null, false),
                new TelegramApiCredentials(12345, "0123456789abcdef0123456789abcdef"));
            return (result, pool, logger);
        }
        finally
        {
            // 仅清理本测试创建的 GUID 临时目录，避免目录不存在或残留文件遮蔽原断言。
            if (Directory.Exists(sessionsPath))
                Directory.Delete(sessionsPath, recursive: true);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add($"{formatter(state, exception)} {exception}");
    }

    private sealed class UnusedProxyResolver : IAccountProxyResolver
    {
        public Task<AccountProxyResolution> ResolveAsync(int accountId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("日志测试不应连接 Telegram");
    }

    private sealed class FailingClientPool(Exception error) : ITelegramClientPool
    {
        public int CreateCount { get; private set; }
        public List<int> RemovedIds { get; } = new();
        public int ActiveClientCount => 0;
        public bool IsClientConnected(int accountId) => false;
        public Client? GetClient(int accountId) => null;
        public Task RemoveAllClientsAsync() => Task.CompletedTask;
        public Task RemoveClientAsync(int accountId)
        {
            RemovedIds.Add(accountId);
            return Task.CompletedTask;
        }

        public Task<Client> GetOrCreateClientAsync(int accountId, int apiId, string apiHash, string sessionPath,
            string? sessionKey = null, string? phoneNumber = null, long? userId = null) =>
            throw new InvalidOperationException("测试必须使用显式登录路由");

        public Task<Client> GetOrCreateClientAsync(int accountId, int apiId, string apiHash, string sessionPath,
            string? sessionKey, string? phoneNumber, long? userId, AccountProxyResolution proxyResolution)
        {
            CreateCount++;
            return Task.FromException<Client>(error);
        }
    }
}

[CollectionDefinition("WTelegram 日志隔离", DisableParallelization = true)]
public sealed class WTelegramLoggingCollection;
