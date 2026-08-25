using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using TelegramPanel.Modules;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class PersistentModuleTaskDeferralTests
{
    [Fact]
    public async Task 延后任务在到期前不可领取且到期后只领取一次()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var now = new DateTime(2026, 8, 26, 1, 0, 0, DateTimeKind.Utc);
        var eligibleAt = now.AddMinutes(5);

        fixture.Db.BatchTasks.Add(new BatchTask
        {
            Id = 1,
            TaskType = "module.deferred",
            OwnerModuleId = "test.module",
            ExecutionKind = ModuleTaskExecutionKinds.Persistent,
            Status = "running",
            Total = 1,
            RequiresAttention = true,
            CreatedAt = now.AddMinutes(-1),
            StartedAt = now
        });
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.Repository.TryDeferAsync(1, eligibleAt, "等待账号冷却", now));
        var deferred = await fixture.Repository.GetFreshByIdAsync(1);
        Assert.NotNull(deferred);
        Assert.Equal("pending", deferred!.Status);
        Assert.Equal(eligibleAt, deferred.NextEligibleAtUtc);
        Assert.Null(deferred.StartedAt);
        Assert.Equal("deferred", deferred.RuntimePhase);
        Assert.Equal("等待账号冷却", deferred.RuntimeMessage);
        Assert.Equal(now, deferred.HeartbeatAtUtc);
        Assert.False(deferred.RequiresAttention);

        Assert.False(await fixture.Repository.TryStartAsync(1, eligibleAt.AddTicks(-1)));
        Assert.True(await fixture.Repository.TryStartAsync(1, eligibleAt));
        Assert.False(await fixture.Repository.TryStartAsync(1, eligibleAt.AddSeconds(1)));

        var running = await fixture.Repository.GetFreshByIdAsync(1);
        Assert.Equal("running", running!.Status);
        Assert.Null(running.NextEligibleAtUtc);
    }

    [Fact]
    public async Task 暂停或取消延后任务会清除下次领取时间()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var eligibleAt = DateTime.UtcNow.AddHours(1);
        fixture.Db.BatchTasks.AddRange(
            new BatchTask
            {
                Id = 3,
                TaskType = "module.pausing",
                OwnerModuleId = "test.module",
                ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                Status = "pending",
                NextEligibleAtUtc = eligibleAt
            },
            new BatchTask
            {
                Id = 4,
                TaskType = "module.canceled",
                OwnerModuleId = "test.module",
                ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                Status = "pending",
                NextEligibleAtUtc = eligibleAt
            });
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.Repository.TryBeginPauseAsync(3));
        Assert.True(await fixture.Repository.TryCancelAsync(4, DateTime.UtcNow));

        Assert.Null((await fixture.Repository.GetFreshByIdAsync(3))!.NextEligibleAtUtc);
        Assert.Null((await fixture.Repository.GetFreshByIdAsync(4))!.NextEligibleAtUtc);
    }

    [Fact]
    public async Task 手工恢复会清除旧的延后时间()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        fixture.Db.BatchTasks.Add(new BatchTask
        {
            Id = 2,
            TaskType = "module.paused",
            OwnerModuleId = "test.module",
            ExecutionKind = ModuleTaskExecutionKinds.Persistent,
            Status = "paused",
            Total = 1,
            NextEligibleAtUtc = DateTime.UtcNow.AddDays(1)
        });
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.Repository.TryResumeAsync(2));
        var resumed = await fixture.Repository.GetFreshByIdAsync(2);
        Assert.Equal("pending", resumed!.Status);
        Assert.Null(resumed.NextEligibleAtUtc);
    }

    [Fact]
    public void 持久任务宿主公开延后与显式完成合同()
    {
        var methods = typeof(IModulePersistentTaskExecutionHost)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(IModulePersistentTaskExecutionHost.DeferAsync), methods);
        Assert.Contains(nameof(IModulePersistentTaskExecutionHost.CompleteAsync), methods);
    }

    [Fact]
    public async Task 持久任务显式完成以单次状态转换提交全部结果()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var completedAt = new DateTime(2026, 8, 26, 2, 0, 0, DateTimeKind.Utc);
        fixture.Db.BatchTasks.AddRange(
            new BatchTask
            {
                Id = 9,
                TaskType = "module.complete",
                OwnerModuleId = "test.module",
                ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                Status = "running",
                RuntimePhase = "sending"
            },
            new BatchTask
            {
                Id = 10,
                TaskType = "module.raced",
                OwnerModuleId = "test.module",
                ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                Status = "running",
                RuntimePhase = "sending"
            });
        await fixture.Db.SaveChangesAsync();

        Assert.True(await fixture.Repository.TryCompletePersistentAsync(
            9, 7, 2, "发送结束", completedAt));
        Assert.True(await fixture.Repository.TryCancelAsync(10, completedAt));
        Assert.False(await fixture.Repository.TryCompletePersistentAsync(
            10, 8, 0, "不应覆盖取消状态", completedAt.AddSeconds(1)));

        var completed = await fixture.Repository.GetFreshByIdAsync(9);
        Assert.Equal("completed", completed!.Status);
        Assert.Equal(7, completed.Completed);
        Assert.Equal(2, completed.Failed);
        Assert.Equal("completed", completed.RuntimePhase);
        Assert.Equal("发送结束", completed.RuntimeMessage);
        Assert.Equal(completedAt, completed.CompletedAt);

        var canceled = await fixture.Repository.GetFreshByIdAsync(10);
        Assert.Equal("canceled", canceled!.Status);
        Assert.Equal("sending", canceled.RuntimePhase);
        Assert.Equal(0, canceled.Completed);
    }

    [Fact]
    public async Task 持久任务候选查询在数据库侧排除批任务和未到期任务()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var now = new DateTime(2026, 8, 26, 3, 0, 0, DateTimeKind.Utc);
        fixture.Db.BatchTasks.AddRange(
            Candidate(5, ModuleTaskExecutionKinds.Persistent, null, now.AddMinutes(-2)),
            Candidate(6, ModuleTaskExecutionKinds.Persistent, now.AddMinutes(1), now.AddMinutes(-3)),
            Candidate(7, ModuleTaskExecutionKinds.Batch, null, now.AddMinutes(-4)),
            Candidate(8, ModuleTaskExecutionKinds.Persistent, now, now.AddMinutes(-1)));
        await fixture.Db.SaveChangesAsync();

        var candidates = await fixture.Repository.GetEligiblePersistentTasksAsync(now);

        Assert.Equal(new[] { 5, 8 }, candidates.Select(task => task.Id));
    }

    [Fact]
    public async Task 内部提交状态保持在任务中心可见()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        fixture.Db.BatchTasks.AddRange(
            new BatchTask
            {
                Id = 11,
                TaskType = "module.initializing",
                OwnerModuleId = "test.module",
                ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                Status = "initializing"
            },
            new BatchTask
            {
                Id = 12,
                TaskType = "module.updating",
                OwnerModuleId = "test.module",
                ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                Status = "updating"
            });
        await fixture.Db.SaveChangesAsync();

        var taskCenter = await fixture.Repository.GetTaskCenterItemsAsync();

        Assert.Equal(2, await fixture.Repository.CountActiveTasksAsync());
        Assert.Equal(new[] { 11, 12 }, taskCenter.Select(task => task.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task 延后时间迁移可升级并回滚()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        var migrator = db.Database.GetService<IMigrator>();
        const string previousMigration = "20260826090000_AddBatchTaskExecutionOwnership";

        await migrator.MigrateAsync(previousMigration);
        Assert.False(await SchemaObjectExistsAsync(
            connection,
            "SELECT COUNT(1) FROM pragma_table_info('BatchTasks') WHERE name = 'NextEligibleAtUtc';"));

        await migrator.MigrateAsync();
        Assert.True(await SchemaObjectExistsAsync(
            connection,
            "SELECT COUNT(1) FROM pragma_table_info('BatchTasks') WHERE name = 'NextEligibleAtUtc';"));
        Assert.True(await SchemaObjectExistsAsync(
            connection,
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'IX_BatchTasks_ExecutionKind_Status_NextEligibleAtUtc';"));

        await migrator.MigrateAsync(previousMigration);
        Assert.False(await SchemaObjectExistsAsync(
            connection,
            "SELECT COUNT(1) FROM pragma_table_info('BatchTasks') WHERE name = 'NextEligibleAtUtc';"));
        Assert.True(await SchemaObjectExistsAsync(
            connection,
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'IX_BatchTasks_ExecutionKind_Status';"));
    }

    private static BatchTask Candidate(
        int id,
        string executionKind,
        DateTime? nextEligibleAtUtc,
        DateTime createdAt) => new()
    {
        Id = id,
        TaskType = "module.candidate",
        OwnerModuleId = "test.module",
        ExecutionKind = executionKind,
        Status = "pending",
        NextEligibleAtUtc = nextEligibleAtUtc,
        CreatedAt = createdAt
    };

    private static async Task<bool> SchemaObjectExistsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private RepositoryFixture(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
            Repository = new BatchTaskRepository(db);
        }

        public AppDbContext Db { get; }
        public BatchTaskRepository Repository { get; }

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new RepositoryFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
