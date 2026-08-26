using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Services;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class BatchTaskNamePersistenceTests
{
    [Fact]
    public async Task EditableDraftUpdate_canRenameAndClearBatchTaskName()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        int taskId;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.BatchTasks.Add(new BatchTask
            {
                Name = "旧名称",
                TaskType = "user_chat_active",
                Status = "paused",
                Total = 1,
                Completed = 1,
                Config = "{\"targets\":[]}",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            taskId = db.BatchTasks.Single().Id;
        }

        await using (var db = new AppDbContext(options))
        {
            var service = CreateService(db);
            var renamed = await service.TryUpdateEditableTaskDraftAsync(
                taskId,
                total: 2,
                config: "{\"targets\":[\"@demo\"]}",
                name: "新任务名称");

            Assert.True(renamed);
        }

        await using (var db = new AppDbContext(options))
        {
            var renamedTask = await db.BatchTasks.AsNoTracking().SingleAsync();
            Assert.Equal("新任务名称", renamedTask.Name);
            Assert.Equal(2, renamedTask.Total);
            Assert.Equal("{\"targets\":[\"@demo\"]}", renamedTask.Config);
        }

        await using (var db = new AppDbContext(options))
        {
            var service = CreateService(db);
            var cleared = await service.TryUpdateEditableTaskDraftAsync(
                taskId,
                total: 3,
                config: null,
                name: null);

            Assert.True(cleared);
        }

        await using (var db = new AppDbContext(options))
        {
            var clearedTask = await db.BatchTasks.AsNoTracking().SingleAsync();
            Assert.Null(clearedTask.Name);
            Assert.Equal(3, clearedTask.Total);
            Assert.Null(clearedTask.Config);
        }
    }

    [Fact]
    public async Task TerminalTask_cannotBeEditedInPlace()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.BatchTasks.Add(new BatchTask
        {
            TaskType = "test",
            Status = "completed",
            Total = 1,
            Completed = 1,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var updated = await service.TryUpdateEditableTaskDraftAsync(
            db.BatchTasks.Single().Id,
            total: 2,
            config: "{}",
            name: "不应写入");

        Assert.False(updated);
    }

    [Fact]
    public async Task RunningTask_mustPassThroughPausingBeforeItBecomesEditable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.BatchTasks.Add(new BatchTask
        {
            TaskType = "persistent-test",
            ExecutionKind = "persistent",
            Status = "running",
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var taskId = db.BatchTasks.Single().Id;
        var repository = new BatchTaskRepository(db);
        Assert.True(await repository.TryBeginPauseAsync(taskId));
        Assert.Equal("pausing", (await repository.GetFreshByIdAsync(taskId))!.Status);
        Assert.False(await repository.TryUpdateEditableDraftAsync(taskId, 1, "{}"));

        Assert.True(await repository.TryConfirmPausedAsync(taskId));
        Assert.Equal("paused", (await repository.GetFreshByIdAsync(taskId))!.Status);
        Assert.True(await repository.TryUpdateEditableDraftAsync(taskId, 1, "{}"));
    }

    private static BatchTaskManagementService CreateService(AppDbContext db)
    {
        return new BatchTaskManagementService(
            new BatchTaskRepository(db),
            new ConfigurationBuilder().Build(),
            NullLogger<BatchTaskManagementService>.Instance);
    }
}
