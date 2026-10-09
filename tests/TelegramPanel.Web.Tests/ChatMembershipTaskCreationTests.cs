using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Services;
using TelegramPanel.Data;
using TelegramPanel.Data.Repositories;
using TelegramPanel.Web.Api;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ChatMembershipTaskCreationTests
{
    [Theory]
    [InlineData("join", null, 2000)]
    [InlineData("LEAVE", -1, 0)]
    [InlineData(" join ", 70000, 60000)]
    public async Task 专用入口规范请求并持久化可执行任务(string operation, int? delayMs, int expectedDelay)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var tasks = CreateTasks(db);

        var result = await CreateTaskAsync(new ChatMembershipRequestDto(
            new[] { 2, 2, -1, 3 }, operation,
            new[] { " @example \n @EXAMPLE ", "@examplebot,@another" }, true, delayMs), tasks);

        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var dto = Assert.IsType<BatchTaskDto>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        var saved = Assert.Single(await db.BatchTasks.AsNoTracking().ToListAsync());
        Assert.Equal(saved.Id, dto.Id);
        Assert.Equal(BatchTaskTypes.UserJoinSubscribe, saved.TaskType);
        Assert.Equal("builtin.tasks", saved.OwnerModuleId);
        Assert.Equal("batch", saved.ExecutionKind);
        Assert.Equal("pending", saved.Status);
        Assert.Equal(6, saved.Total);
        Assert.Equal(0, saved.Completed);
        Assert.Equal(0, saved.Failed);
        var config = JsonSerializer.Deserialize<UserJoinSubscribeTaskConfig>(saved.Config!)!;
        Assert.Equal(new[] { 2, 3 }, config.AccountIds);
        Assert.Equal(new[] { "@example", "@examplebot", "@another" }, config.Links);
        Assert.Equal(operation.Trim().ToLowerInvariant(), config.Operation);
        Assert.True(config.TreatNoBotSuffixAsBot);
        Assert.Equal(expectedDelay, config.DelayMs);
        Assert.False(config.Canceled);
        Assert.Null(config.Error);
        Assert.Empty(config.Failures);
    }

    [Theory]
    [InlineData("join", false, true, "请先选择账号")]
    [InlineData("join", true, false, "请填写链接或用户名")]
    [InlineData("invalid", true, true, "操作类型无效")]
    public async Task 无效请求不创建任务(string operation, bool hasAccounts, bool hasLinks, string message)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var result = await CreateTaskAsync(new ChatMembershipRequestDto(
            hasAccounts ? new[] { 1 } : new[] { 0, -1 }, operation,
            hasLinks ? new[] { "@example" } : new[] { " , \n " }, false, null), CreateTasks(db));

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var error = Assert.IsType<OperationResultDto>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.False(error.Success);
        Assert.Equal(message, error.Message);
        Assert.Empty(await db.BatchTasks.ToListAsync());
    }

    private static Task<IResult> CreateTaskAsync(ChatMembershipRequestDto request, BatchTaskManagementService tasks) =>
        PanelAdminApiEndpoints.CreateChatMembershipTaskAsync(request, tasks, CancellationToken.None);

    private static AppDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

    private static BatchTaskManagementService CreateTasks(AppDbContext db) =>
        new(new BatchTaskRepository(db), new ConfigurationBuilder().Build(),
            NullLogger<BatchTaskManagementService>.Instance);
}
