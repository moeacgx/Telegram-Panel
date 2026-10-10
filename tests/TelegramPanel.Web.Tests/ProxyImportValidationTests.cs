using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ProxyImportValidationTests
{
    [Fact]
    public async Task 批量导入包含HTTPS代理时不写入部分数据()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var probe = new ProxyEgressProbeService();
        var service = new ProxyManagementService(
            db,
            new EmptyClientPool(),
            probe,
            NullLogger<ProxyManagementService>.Instance);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ImportAsync(
                "http://127.0.0.1:8001\nhttps://127.0.0.1:8002",
                testAfterImport: false));

        Assert.Contains("不支持 HTTPS 代理地址", error.Message);
        Assert.Empty(await db.OutboundProxies.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 批量导入格式错误不会在响应中回显代理凭据()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var probe = new ProxyEgressProbeService();
        var service = new ProxyManagementService(
            db,
            new EmptyClientPool(),
            probe,
            NullLogger<ProxyManagementService>.Instance);
        const string secret = "top-secret-proxy-token";
        var raw = $"http://proxy-user:{secret}@";

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ImportAsync(raw, testAfterImport: false));

        Assert.Equal("代理地址格式无效，请检查协议、主机和端口", error.Message);
        Assert.DoesNotContain(secret, error.Message);
        Assert.DoesNotContain(raw, error.Message);
        Assert.Empty(await db.OutboundProxies.AsNoTracking().ToListAsync());
    }
    private sealed class EmptyClientPool : ITelegramClientPool
    {
        public int ActiveClientCount => 0;

        public Task<Client> GetOrCreateClientAsync(
            int accountId,
            int apiId,
            string apiHash,
            string sessionPath,
            string? sessionKey = null,
            string? phoneNumber = null,
            long? userId = null) =>
            throw new NotSupportedException();

        public Client? GetClient(int accountId) => null;
        public Task RemoveClientAsync(int accountId) => Task.CompletedTask;
        public Task RemoveAllClientsAsync() => Task.CompletedTask;
        public bool IsClientConnected(int accountId) => false;
    }
}
