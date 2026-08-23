using System.Text.Json;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class DataSyncTaskConfigTests
{
    [Fact]
    public void BuildSyncTaskConfig_UsesCamelCaseForFailureDetails()
    {
        var config = DataSyncService.BuildSyncTaskConfig(
            trigger: "test",
            totalAccounts: 1,
            processedAccounts: 1,
            failedAccounts: 1,
            totalChannelsSynced: 0,
            totalGroupsSynced: 0,
            failures: new[] { new DataSyncService.SyncFailureItem(42, "8613800000000", "连接失败") },
            skippedAccounts: new[] { new DataSyncService.SyncSkippedItem(7, "8613900000000", "Session 不可用") },
            error: null);

        using var document = JsonDocument.Parse(config);
        var failure = document.RootElement.GetProperty("failures")[0];

        Assert.Equal(42, failure.GetProperty("accountId").GetInt32());
        Assert.Equal("8613800000000", failure.GetProperty("phone").GetString());
        Assert.Equal("连接失败", failure.GetProperty("error").GetString());
        Assert.False(failure.TryGetProperty("AccountId", out _));

        var skipped = document.RootElement.GetProperty("skippedAccounts")[0];
        Assert.Equal(7, skipped.GetProperty("AccountId").GetInt32());
        Assert.Equal("8613900000000", skipped.GetProperty("Phone").GetString());
        Assert.Equal("Session 不可用", skipped.GetProperty("Reason").GetString());
    }
}
