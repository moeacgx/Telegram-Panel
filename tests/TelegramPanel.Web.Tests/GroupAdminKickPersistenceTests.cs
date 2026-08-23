using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using TelegramPanel.Web.Api;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class GroupAdminKickPersistenceTests
{
    [Fact]
    public async Task 踢出本系统账号后会删除其群组关联()
    {
        await using var context = await CreateContextAsync();
        var service = new StubGroupService
        {
            Result = new GroupAdminRemovalResult(true, true, true, false, "已撤销管理员权限并踢出成员")
        };

        var result = await PanelAdminApiEndpoints.KickGroupAdminAsync(
            context.GroupId,
            context.TargetTelegramUserId,
            context.GroupManagement,
            service,
            CancellationToken.None);

        AssertOperationResult(result, StatusCodes.Status200OK, expectedSuccess: true);
        Assert.Equal(context.ExecutorAccountId, service.LastAccountId);
        Assert.Equal(context.GroupTelegramId, service.LastGroupId);
        Assert.Equal(context.TargetTelegramUserId, service.LastTargetUserId);

        context.Db.ChangeTracker.Clear();
        Assert.Null(await context.Db.AccountGroups.AsNoTracking().SingleOrDefaultAsync(
            item => item.AccountId == context.TargetAccountId && item.GroupId == context.GroupId));
        Assert.NotNull(await context.Db.AccountGroups.AsNoTracking().SingleOrDefaultAsync(
            item => item.AccountId == context.ExecutorAccountId && item.GroupId == context.GroupId));
    }

    [Fact]
    public async Task 仅降权成功时保留本系统账号成员关联并返回冲突()
    {
        await using var context = await CreateContextAsync();
        var service = new StubGroupService
        {
            Result = new GroupAdminRemovalResult(
                false,
                true,
                false,
                false,
                "已撤销管理员权限，但尚未踢出成员：权限不足")
        };

        var result = await PanelAdminApiEndpoints.KickGroupAdminAsync(
            context.GroupId,
            context.TargetTelegramUserId,
            context.GroupManagement,
            service,
            CancellationToken.None);

        AssertOperationResult(result, StatusCodes.Status409Conflict, expectedSuccess: false);

        context.Db.ChangeTracker.Clear();
        var membership = await context.Db.AccountGroups.AsNoTracking().SingleAsync(
            item => item.AccountId == context.TargetAccountId && item.GroupId == context.GroupId);
        Assert.False(membership.IsCreator);
        Assert.False(membership.IsAdmin);
    }

    [Fact]
    public async Task 目标已不在群组时会删除本系统账号关联()
    {
        await using var context = await CreateContextAsync();
        var service = new StubGroupService
        {
            Result = new GroupAdminRemovalResult(true, true, false, true, "已撤销管理员权限；目标已不在群组中")
        };

        var result = await PanelAdminApiEndpoints.KickGroupAdminAsync(
            context.GroupId,
            context.TargetTelegramUserId,
            context.GroupManagement,
            service,
            CancellationToken.None);

        AssertOperationResult(result, StatusCodes.Status200OK, expectedSuccess: true);

        context.Db.ChangeTracker.Clear();
        Assert.Null(await context.Db.AccountGroups.AsNoTracking().SingleOrDefaultAsync(
            item => item.AccountId == context.TargetAccountId && item.GroupId == context.GroupId));
    }

    private static void AssertOperationResult(IResult result, int expectedStatusCode, bool expectedSuccess)
    {
        Assert.Equal(
            expectedStatusCode,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var payload = Assert.IsType<OperationResultDto>(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(expectedSuccess, payload.Success);
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var executor = CreateAccount("8613800000001", 10001, 1001);
        var target = CreateAccount("8613800000002", 10002, 2002);
        db.Accounts.AddRange(executor, target);
        await db.SaveChangesAsync();

        var group = new Group
        {
            TelegramId = 90001,
            Title = "测试群组",
            CreatorAccountId = executor.Id,
            SyncedAt = DateTime.UtcNow
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        db.AccountGroups.AddRange(
            new AccountGroup
            {
                AccountId = executor.Id,
                GroupId = group.Id,
                IsCreator = true,
                IsAdmin = true,
                SyncedAt = DateTime.UtcNow
            },
            new AccountGroup
            {
                AccountId = target.Id,
                GroupId = group.Id,
                IsCreator = false,
                IsAdmin = true,
                SyncedAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var groupManagement = new GroupManagementService(
            new GroupRepository(db),
            new AccountGroupRepository(db));
        return new TestContext(
            connection,
            db,
            groupManagement,
            group.Id,
            group.TelegramId,
            executor.Id,
            target.Id,
            target.UserId);
    }

    private static Account CreateAccount(string phone, int displayNumber, long userId) => new()
    {
        Phone = phone,
        DisplayNumber = displayNumber,
        UserId = userId,
        SessionPath = $"sessions/{displayNumber}.session",
        ApiId = 1,
        ApiHash = "test-api-hash"
    };

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            SqliteConnection connection,
            AppDbContext db,
            GroupManagementService groupManagement,
            int groupId,
            long groupTelegramId,
            int executorAccountId,
            int targetAccountId,
            long targetTelegramUserId)
        {
            Connection = connection;
            Db = db;
            GroupManagement = groupManagement;
            GroupId = groupId;
            GroupTelegramId = groupTelegramId;
            ExecutorAccountId = executorAccountId;
            TargetAccountId = targetAccountId;
            TargetTelegramUserId = targetTelegramUserId;
        }

        public SqliteConnection Connection { get; }
        public AppDbContext Db { get; }
        public GroupManagementService GroupManagement { get; }
        public int GroupId { get; }
        public long GroupTelegramId { get; }
        public int ExecutorAccountId { get; }
        public int TargetAccountId { get; }
        public long TargetTelegramUserId { get; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class StubGroupService : IGroupService
    {
        public GroupAdminRemovalResult Result { get; init; } = new(false, false, false, false, "未设置结果");
        public int? LastAccountId { get; private set; }
        public long? LastGroupId { get; private set; }
        public long? LastTargetUserId { get; private set; }

        public Task<List<GroupInfo>> GetOwnedGroupsAsync(int accountId) => throw new NotSupportedException();
        public Task<GroupInfo> CreateGroupAsync(int accountId, string title, string about, bool isPublic = false, string? username = null) => throw new NotSupportedException();
        public Task<GroupInfo> CreatePrivateGroupAsync(int accountId, string title, string about) => throw new NotSupportedException();
        public Task<List<GroupInfo>> GetVisibleGroupsAsync(int accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GroupInfo?> GetGroupInfoAsync(int accountId, long groupId) => throw new NotSupportedException();
        public Task<InviteResult> InviteUserAsync(int accountId, long groupId, string username) => throw new NotSupportedException();
        public Task<List<InviteResult>> BatchInviteUsersAsync(int accountId, long groupId, List<string> usernames, int delayMs = 2000) => throw new NotSupportedException();
        public Task<bool> SetAdminAsync(int accountId, long groupId, string username, AdminRights rights, string title = "Admin") => throw new NotSupportedException();
        public Task<List<SetAdminResult>> BatchSetAdminsAsync(int accountId, long groupId, List<AdminRequest> requests) => throw new NotSupportedException();
        public Task<bool> KickUserAsync(int accountId, long groupId, string username, bool permanentBan = false) => throw new NotSupportedException();
        public Task<bool> KickUserByUserIdAsync(int accountId, long groupId, long userId, bool permanentBan = false) => throw new NotSupportedException();

        public Task<GroupAdminRemovalResult> RemoveAdminAndKickAsync(
            int accountId,
            long groupId,
            long targetUserId,
            CancellationToken cancellationToken = default)
        {
            LastAccountId = accountId;
            LastGroupId = groupId;
            LastTargetUserId = targetUserId;
            return Task.FromResult(Result);
        }

        public Task<bool> LeaveGroupAsync(int accountId, long groupId) => throw new NotSupportedException();
        public Task<bool> DisbandGroupAsync(int accountId, long groupId) => throw new NotSupportedException();
        public Task<bool> TransferOwnershipAsync(int accountId, long groupId, string targetUsername, string password) => throw new NotSupportedException();
        public Task<string> ExportJoinLinkAsync(int accountId, long groupId) => throw new NotSupportedException();
        public Task<List<ChannelAdminInfo>> GetAdminsAsync(int accountId, long groupId) => throw new NotSupportedException();
        public Task<bool> UpdateGroupInfoAsync(int accountId, long groupId, string title, string? about) => throw new NotSupportedException();
        public Task<bool> SetGroupVisibilityAsync(int accountId, long groupId, bool isPublic, string? username = null) => throw new NotSupportedException();
        public Task<bool> SetGroupPhotoAsync(int accountId, long groupId, Stream fileStream, string fileName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
