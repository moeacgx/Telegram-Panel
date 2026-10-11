using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Web.Api;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ProxyBoundAccountsTests
{
    [Fact]
    public async Task 仅返回专属绑定并隐藏手机号和凭据()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bound = fixture.AddAccount(1, 1);
        bound.Remark = "测试备注";
        bound.Nickname = "测试昵称";
        fixture.AddAccount(2, 2);
        fixture.AddAccount(3, null).UseGlobalProxy = true;
        fixture.AddAccount(4, null).UseGlobalProxy = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var dto = Read(await fixture.ListAsync(1));

        Assert.Equal(1, dto.Total);
        var account = Assert.Single(dto.Items);
        Assert.Equal(bound.Id, account.Id);
        Assert.Equal(bound.DisplayNumber, account.DisplayNumber);
        Assert.Equal("测试备注", account.Remark);
        Assert.Equal("测试昵称", account.Nickname);
        Assert.Equal("0001", account.PhoneLastFour);
        Assert.Equal(1, account.ProxyId);
        Assert.False(account.UseGlobalProxy);
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        var json = JsonSerializer.Serialize(account, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "id", "displayNumber", "remark", "nickname", "phoneLastFour", "proxyId", "useGlobalProxy" },
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain(bound.Phone, json);
        Assert.DoesNotContain(bound.ApiHash, json);
        Assert.DoesNotContain(bound.SessionPath, json);
        Assert.DoesNotContain(bound.TwoFactorPassword!, json);
    }

    [Fact]
    public async Task 分页稳定并限制单页数量且处理极大页码()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var id = 205; id >= 1; id--)
            fixture.AddAccount(id, 1);
        await fixture.Db.SaveChangesAsync();

        var first = Read(await fixture.ListAsync(1, -1, 999));
        Assert.Equal(1, first.Page);
        Assert.Equal(200, first.PageSize);
        Assert.Equal(205, first.Total);
        Assert.Equal(Enumerable.Range(1, 200), first.Items.Select(account => account.Id));

        var second = Read(await fixture.ListAsync(1, 2, 200));
        Assert.Equal(Enumerable.Range(201, 5), second.Items.Select(account => account.Id));
        Assert.Equal(205, second.Total);

        var minimum = Read(await fixture.ListAsync(1, 0, 0));
        Assert.Equal(1, minimum.PageSize);
        Assert.Equal(1, Assert.Single(minimum.Items).Id);
        var beyond = Read(await fixture.ListAsync(1, int.MaxValue, 200));
        Assert.Empty(beyond.Items);
        Assert.Equal(205, beyond.Total);
    }

    [Fact]
    public async Task 解绑后重新查询返回空列表且不存在代理返回404()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = fixture.AddAccount(1, 1);
        await fixture.Db.SaveChangesAsync();
        Assert.Single(Read(await fixture.ListAsync(1)).Items);

        account.ProxyId = null;
        account.UseGlobalProxy = true;
        await fixture.Db.SaveChangesAsync();
        var empty = Read(await fixture.ListAsync(1));
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.Total);
        Assert.Equal(1, empty.Page);
        Assert.Equal(50, empty.PageSize);
        var missing = await fixture.ListAsync(999);
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(missing).StatusCode);
        Assert.Equal("代理不存在", Assert.IsType<OperationResultDto>(Assert.IsAssignableFrom<IValueHttpResult>(missing).Value).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    public async Task 不足四位的手机号不回传原值(string phone)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddAccount(1, 1).Phone = phone;
        await fixture.Db.SaveChangesAsync();
        Assert.Empty(Assert.Single(Read(await fixture.ListAsync(1)).Items).PhoneLastFour);
    }

    private static ProxyBoundAccountsDto Read(IResult result)
    {
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        return Assert.IsType<ProxyBoundAccountsDto>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    }

    private sealed class Fixture(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.OutboundProxies.AddRange(
                new OutboundProxy { Id = 1, Name = "出口一", Host = "127.0.0.1", Port = 1080 },
                new OutboundProxy { Id = 2, Name = "出口二", Host = "127.0.0.1", Port = 1081 });
            await db.SaveChangesAsync();
            return new Fixture(connection, db);
        }

        public Account AddAccount(int id, int? proxyId)
        {
            var account = new Account
            {
                Id = id,
                DisplayNumber = id + 1000,
                UserId = id,
                Phone = $"+8613800{id:D6}",
                SessionPath = $"private-session-{id}",
                ApiHash = $"private-api-hash-{id}",
                TwoFactorPassword = $"private-password-{id}",
                ProxyId = proxyId,
                UseGlobalProxy = false
            };
            Db.Accounts.Add(account);
            return account;
        }

        public Task<IResult> ListAsync(int id, int? page = null, int? pageSize = null) =>
            ProxyApiEndpoints.ListBoundAccountsAsync(id, page, pageSize, Db, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
