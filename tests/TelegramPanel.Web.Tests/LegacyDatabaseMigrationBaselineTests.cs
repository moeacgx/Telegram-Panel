using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TelegramPanel.Data;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class LegacyDatabaseMigrationBaselineTests
{
    [Fact]
    public async Task Apply_ReconstructsPreviousMigrationHistoryAndUpgradesWithoutDuplicatingSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        var migrator = db.Database.GetService<IMigrator>();

        const string previousMigration = "20260316000000_AddAccountRemark";
        await migrator.MigrateAsync(previousMigration);
        await InsertAccountAsync(db, "8613800000001");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory;");

        var migrations = db.Database.GetMigrations().ToList();
        var baselined = global::LegacyDatabaseMigrationBaseline.Apply(connection, migrations);

        Assert.Equal(previousMigration, baselined[^1]);
        await migrator.MigrateAsync();

        Assert.Equal("8613800000001", await db.Accounts.AsNoTracking().Select(account => account.Phone).SingleAsync());
        Assert.True(await db.Database.CanConnectAsync());
        Assert.Equal(migrations.Count, await CountHistoryRowsAsync(connection));
        Assert.True(await TableExistsAsync(connection, "OutboundProxies"));
        Assert.True(await ColumnExistsAsync(connection, "Accounts", "UseGlobalProxy"));
        Assert.True(await ColumnExistsAsync(connection, "ScheduledTasks", "Name"));
        Assert.True(await ColumnExistsAsync(connection, "BatchTasks", "Name"));
    }

    [Fact]
    public async Task Apply_BaselinesCurrentEnsureCreatedDatabaseWithoutReplayingMigrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        await db.Database.EnsureCreatedAsync();
        await InsertAccountAsync(db, "8613800000002");
        var migrations = db.Database.GetMigrations().ToList();

        var baselined = global::LegacyDatabaseMigrationBaseline.Apply(connection, migrations);
        await db.Database.MigrateAsync();

        Assert.Equal(migrations, baselined);
        Assert.Equal(migrations.Count, await CountHistoryRowsAsync(connection));
        Assert.Equal("8613800000002", await db.Accounts.AsNoTracking().Select(account => account.Phone).SingleAsync());
    }

    [Fact]
    public async Task Apply_RejectsUnknownSchemaWithoutWritingHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE UnknownLegacyTable (Id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        var migrations = db.Database.GetMigrations().ToList();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            global::LegacyDatabaseMigrationBaseline.Apply(connection, migrations));

        Assert.Contains("无法与任何已知迁移版本可靠匹配", exception.Message, StringComparison.Ordinal);
        Assert.False(await TableExistsAsync(connection, "__EFMigrationsHistory"));
    }

    private static Task<int> InsertAccountAsync(AppDbContext db, string phone)
    {
        return db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Accounts"
                ("Phone", "UserId", "SessionPath", "ApiId", "ApiHash", "IsActive", "CreatedAt", "LastSyncAt")
            VALUES
                ({{phone}}, 10001, 'sessions/baseline.session', 1, 'hash', 1,
                 '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            """);
    }

    private static async Task<int> CountHistoryRowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM __EFMigrationsHistory;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}');";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
