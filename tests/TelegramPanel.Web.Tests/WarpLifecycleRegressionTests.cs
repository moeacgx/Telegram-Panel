using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Web.Api;
using TelegramPanel.Web.Services;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class WarpLifecycleRegressionTests
{
    [Fact]
    public async Task 已删除WARP记录允许复用历史主机端口()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.WarpProfiles.Add(Profile("deleted-profile", "deleted", 42080));
        await db.SaveChangesAsync();

        db.WarpProfiles.Add(Profile("active-profile", "active", 42080));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.WarpProfiles.CountAsync());
    }

    [Fact]
    public async Task 未删除WARP记录仍禁止重复主机端口()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.WarpProfiles.Add(Profile("active-profile", "active", 42080));
        await db.SaveChangesAsync();

        db.WarpProfiles.Add(Profile("starting-profile", "starting", 42080));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task 创建失败的WARP记录允许复用历史主机端口()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.WarpProfiles.Add(Profile("failed-profile", "failed", 42080));
        await db.SaveChangesAsync();

        db.WarpProfiles.Add(Profile("active-profile", "active", 42080));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.WarpProfiles.CountAsync());
    }

    [Fact]
    public async Task 待清理WARP记录会继续占用主机端口()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.WarpProfiles.Add(Profile("cleanup-pending-profile", "cleanup_pending", 42080));
        await db.SaveChangesAsync();

        db.WarpProfiles.Add(Profile("new-profile", "creating", 42080));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static WarpProfile Profile(string profileId, string status, int hostPort) => new()
    {
        ProfileId = profileId,
        ContainerName = $"container-{profileId}",
        VolumeName = $"volume-{profileId}",
        HostPort = hostPort,
        Status = status,
        DesiredEnabled = status is not "deleted"
    };

}
