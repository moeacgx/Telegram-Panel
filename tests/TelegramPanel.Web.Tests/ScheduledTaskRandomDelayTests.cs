using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelegramPanel.Core.Services;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ScheduledTaskRandomDelayTests
{
    [Fact]
    public async Task 计划任务重新计算下次运行时会加入随机延迟()
    {
        await using var fixture = await Fixture.CreateAsync(300);
        var fromUtc = new DateTime(2026, 8, 20, 10, 15, 0, DateTimeKind.Utc);
        var exactNextRunUtc = new DateTime(2026, 8, 20, 11, 0, 0, DateTimeKind.Utc);
        await fixture.AddScheduledTaskAsync("0 * * * *", fromUtc);

        await fixture.Service.RecalculateNextRunsAsync(fromUtc);

        var nextRunAtUtc = await fixture.GetOnlyNextRunAtUtcAsync();
        Assert.True(nextRunAtUtc > exactNextRunUtc, $"nextRunAtUtc={nextRunAtUtc:O}");
        Assert.True(nextRunAtUtc <= exactNextRunUtc.AddSeconds(300), $"nextRunAtUtc={nextRunAtUtc:O}");
    }

    [Fact]
    public async Task 计划任务随机延迟不会越过下一次Cron窗口()
    {
        await using var fixture = await Fixture.CreateAsync(300);
        var fromUtc = new DateTime(2026, 8, 20, 10, 15, 0, DateTimeKind.Utc);
        var exactNextRunUtc = new DateTime(2026, 8, 20, 10, 16, 0, DateTimeKind.Utc);
        await fixture.AddScheduledTaskAsync("* * * * *", fromUtc);

        await fixture.Service.RecalculateNextRunsAsync(fromUtc);

        var nextRunAtUtc = await fixture.GetOnlyNextRunAtUtcAsync();
        Assert.True(nextRunAtUtc > exactNextRunUtc, $"nextRunAtUtc={nextRunAtUtc:O}");
        Assert.True(nextRunAtUtc <= exactNextRunUtc.AddSeconds(59), $"nextRunAtUtc={nextRunAtUtc:O}");
    }

    [Fact]
    public async Task 随机延迟设置为零时保留精确Cron时间()
    {
        await using var fixture = await Fixture.CreateAsync(0);
        var fromUtc = new DateTime(2026, 8, 20, 10, 15, 0, DateTimeKind.Utc);
        var exactNextRunUtc = new DateTime(2026, 8, 20, 11, 0, 0, DateTimeKind.Utc);
        await fixture.AddScheduledTaskAsync("0 * * * *", fromUtc);

        await fixture.Service.RecalculateNextRunsAsync(fromUtc);

        var nextRunAtUtc = await fixture.GetOnlyNextRunAtUtcAsync();
        Assert.Equal(exactNextRunUtc, nextRunAtUtc);
    }

    [Fact]
    public async Task 手动触发不会在上次任务仍处于停止中时创建重复任务()
    {
        await using var fixture = await Fixture.CreateAsync(0);
        var scheduleId = await fixture.AddScheduledTaskWithLastRunAsync("pausing");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RunNowAsync(
            scheduleId,
            "host.system",
            "batch"));

        Assert.Contains("尚未结束", error.Message);
        Assert.Equal(1, await fixture.GetBatchTaskCountAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly AppDbContext _db;

        private Fixture(SqliteConnection connection, AppDbContext db, ScheduledTaskService service)
        {
            _connection = connection;
            _db = db;
            Service = service;
        }

        public ScheduledTaskService Service { get; }

        public static async Task<Fixture> CreateAsync(int randomDelaySeconds)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ScheduledTasks:RandomDelaySeconds"] = randomDelaySeconds.ToString()
                })
                .Build();
            var service = CreateService(db, configuration);
            return new Fixture(connection, db, service);
        }

        public async Task AddScheduledTaskAsync(string cronExpression, DateTime timestampUtc)
        {
            _db.ScheduledTasks.Add(new ScheduledTask
            {
                Name = "定时测试任务",
                TaskType = "account_auto_sync",
                Status = ScheduledTaskStatuses.Enabled,
                Total = 1,
                CronExpression = cronExpression,
                CreatedAt = timestampUtc,
                UpdatedAt = timestampUtc
            });
            await _db.SaveChangesAsync();
        }

        public async Task<DateTime> GetOnlyNextRunAtUtcAsync()
        {
            _db.ChangeTracker.Clear();
            var value = await _db.ScheduledTasks
                .AsNoTracking()
                .Select(x => x.NextRunAtUtc)
                .SingleAsync();
            Assert.True(value.HasValue);
            return DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        }

        public async Task<int> AddScheduledTaskWithLastRunAsync(string status)
        {
            var batchTask = new BatchTask
            {
                TaskType = "account_auto_sync",
                Status = status,
                Total = 1,
                CreatedAt = DateTime.UtcNow
            };
            _db.BatchTasks.Add(batchTask);
            await _db.SaveChangesAsync();

            var scheduledTask = new ScheduledTask
            {
                Name = "停止屏障测试任务",
                TaskType = "account_auto_sync",
                Status = ScheduledTaskStatuses.Enabled,
                Total = 1,
                CronExpression = "0 * * * *",
                LastBatchTaskId = batchTask.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.ScheduledTasks.Add(scheduledTask);
            await _db.SaveChangesAsync();
            return scheduledTask.Id;
        }

        public Task<int> GetBatchTaskCountAsync() => _db.BatchTasks.CountAsync();

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static ScheduledTaskService CreateService(AppDbContext db, IConfiguration configuration)
        {
            var batchTasks = new BatchTaskManagementService(
                new BatchTaskRepository(db),
                configuration,
                NullLogger<BatchTaskManagementService>.Instance);
            var timeZone = new PanelTimeZoneService(
                new TestOptionsMonitor<PanelTimeZoneOptions>(new PanelTimeZoneOptions { TimeZoneId = "UTC" }));
            var imageStorage = new ImageAssetStorageService(
                configuration,
                new TestWebHostEnvironment(),
                NullLogger<ImageAssetStorageService>.Instance);
            return new ScheduledTaskService(
                new ScheduledTaskRepository(db),
                new CronExpressionService(),
                batchTasks,
                timeZone,
                imageStorage,
                configuration);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "TelegramPanel.Web.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
