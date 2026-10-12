using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class PersistentStorageBootstrapperTests
{
    [Fact]
    public void Initialize_MigratesLegacyFilesWithoutOverwritingExistingTargets()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "persistent");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            Directory.CreateDirectory(Path.Combine(legacyRoot, "sessions"));
            Directory.CreateDirectory(Path.Combine(persistentRoot, "sessions"));

            CreateSqliteDatabase(Path.Combine(legacyRoot, "telegram_panel.db"), "legacy-db");
            File.WriteAllText(
                Path.Combine(legacyRoot, "admin_auth.json"),
                CreateCredentialJson("legacy-user"));
            File.WriteAllText(Path.Combine(legacyRoot, "sessions", "100.session"), "legacy-session");

            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = persistentRoot,
                ["ConnectionStrings:DefaultConnection"] = "Data Source=telegram_panel.db",
                ["AdminAuth:CredentialsPath"] = "admin_auth.json",
                ["Telegram:SessionsPath"] = "sessions"
            });

            var paths = PersistentStorageBootstrapper.Initialize(
                configuration,
                new TestEnvironment(legacyRoot));

            Assert.Equal("legacy-db", ReadDatabaseMarker(paths.DatabasePath));
            Assert.Equal("legacy-user", ReadCredentialUsername(paths.CredentialsPath));
            Assert.Equal("legacy-session", File.ReadAllText(Path.Combine(paths.SessionsPath, "100.session")));
            Assert.Equal(paths.SessionsPath, configuration["Telegram:SessionsPath"]);
            Assert.Equal(paths.CredentialsPath, configuration["AdminAuth:CredentialsPath"]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_RestoresFromPreviousSelfUpdateDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var currentRoot = Path.Combine(root, "app-current");
            var previousRoot = Path.Combine(root, "app-previous");
            Directory.CreateDirectory(currentRoot);
            Directory.CreateDirectory(previousRoot);
            Directory.CreateDirectory(Path.Combine(previousRoot, "sessions"));
            CreateSqliteDatabase(Path.Combine(previousRoot, "telegram_panel.db"), "previous-db");
            File.WriteAllText(
                Path.Combine(previousRoot, "admin_auth.json"),
                CreateCredentialJson("previous-user"));
            File.WriteAllText(Path.Combine(previousRoot, "sessions", "200.session"), "previous-session");

            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = root,
                ["ConnectionStrings:DefaultConnection"] = "Data Source=telegram_panel.db",
                ["AdminAuth:CredentialsPath"] = "admin_auth.json",
                ["Telegram:SessionsPath"] = "sessions"
            });

            var paths = PersistentStorageBootstrapper.Initialize(
                configuration,
                new TestEnvironment(currentRoot));

            Assert.Equal("previous-db", ReadDatabaseMarker(paths.DatabasePath));
            Assert.Equal("previous-user", ReadCredentialUsername(paths.CredentialsPath));
            Assert.Equal("previous-session", File.ReadAllText(Path.Combine(paths.SessionsPath, "200.session")));
            Assert.Equal("previous-db", ReadDatabaseMarker(Path.Combine(previousRoot, "telegram_panel.db")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_DoesNotOverwriteExistingPersistentFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "persistent");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            CreateSqliteDatabase(Path.Combine(legacyRoot, "telegram_panel.db"), "legacy-db");
            CreateSqliteDatabase(Path.Combine(persistentRoot, "telegram_panel.db"), "current-db");

            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = persistentRoot,
                ["ConnectionStrings:DefaultConnection"] = "Data Source=telegram_panel.db"
            });

            var paths = PersistentStorageBootstrapper.Initialize(
                configuration,
                new TestEnvironment(legacyRoot));

            Assert.Equal("current-db", ReadDatabaseMarker(paths.DatabasePath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_ReplacesZeroLengthDatabaseFromValidatedLegacySnapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "persistent");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);

            CreateSqliteDatabase(Path.Combine(legacyRoot, "telegram_panel.db"), "recovered-db");
            var targetPath = Path.Combine(persistentRoot, "telegram_panel.db");
            File.WriteAllBytes(targetPath, Array.Empty<byte>());

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal("recovered-db", ReadDatabaseMarker(paths.DatabasePath));
            var preservedTargets = Directory.GetFiles(
                persistentRoot,
                "telegram_panel.db.invalid-*",
                SearchOption.TopDirectoryOnly);
            var preservedTarget = Assert.Single(preservedTargets);
            Assert.Equal(0, new FileInfo(preservedTarget).Length);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_SkipsInvalidNewerDatabaseCandidateAndRecoversOlderValidDatabase()
    {
        var root = CreateTempDirectory();
        try
        {
            var currentRoot = Path.Combine(root, "app-current");
            var previousRoot = Path.Combine(root, "app-previous");
            Directory.CreateDirectory(currentRoot);
            Directory.CreateDirectory(previousRoot);
            File.WriteAllBytes(Path.Combine(currentRoot, "telegram_panel.db"), Array.Empty<byte>());
            CreateSqliteDatabase(Path.Combine(previousRoot, "telegram_panel.db"), "previous-valid-db");
            var reports = new List<string>();

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(root),
                new TestEnvironment(currentRoot),
                reports.Add);

            Assert.Equal("previous-valid-db", ReadDatabaseMarker(paths.DatabasePath));
            Assert.Contains(
                reports,
                report => report.Contains("跳过不可用的旧数据库候选", StringComparison.Ordinal)
                    && report.Contains("文件为空", StringComparison.Ordinal));
            Assert.Equal(0, new FileInfo(Path.Combine(currentRoot, "telegram_panel.db")).Length);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_RestoresNewestCandidateWithAccountsAndBacksUpTarget()
    {
        var root = CreateTempDirectory();
        try
        {
            var currentRoot = Path.Combine(root, "app-current");
            var newerRoot = Path.Combine(root, "app-previous-newer");
            var olderRoot = Path.Combine(root, "app-previous-older");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(currentRoot);
            Directory.CreateDirectory(newerRoot);
            Directory.CreateDirectory(olderRoot);
            Directory.CreateDirectory(persistentRoot);

            var targetPath = Path.Combine(persistentRoot, "telegram_panel.db");
            CreateAccountsDatabase(targetPath, 0);
            var newerPath = Path.Combine(newerRoot, "telegram_panel.db");
            var olderPath = Path.Combine(olderRoot, "telegram_panel.db");
            CreateAccountsDatabase(newerPath, 1);
            CreateAccountsDatabase(olderPath, 3);
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(newerPath, now.AddMinutes(-1));
            File.SetLastWriteTimeUtc(olderPath, now.AddMinutes(-2));
            File.WriteAllBytes(olderPath + "-shm", Array.Empty<byte>());
            File.SetLastWriteTimeUtc(olderPath + "-shm", now);

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(currentRoot));

            Assert.Equal(1, ReadAccountCount(paths.DatabasePath));
            var backups = Directory.GetFiles(
                persistentRoot,
                "telegram_panel.db.invalid-*",
                SearchOption.TopDirectoryOnly);
            Assert.Single(backups);
            Assert.Equal(0, ReadAccountCount(backups[0]));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_DoesNotReapplyLegacyDataAfterMigrationCompletes()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            var legacySessions = Path.Combine(legacyRoot, "sessions");
            Directory.CreateDirectory(legacySessions);
            Directory.CreateDirectory(persistentRoot);
            CreateAccountsDatabase(Path.Combine(legacyRoot, "telegram_panel.db"), 2);
            File.WriteAllText(
                Path.Combine(legacyRoot, "admin_auth.json"),
                CreateCredentialJson("legacy-user", "legacy-password", mustChangePassword: false));
            File.WriteAllText(Path.Combine(legacySessions, "100.session"), "legacy-session");

            var first = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal(2, ReadAccountCount(first.DatabasePath));
            Assert.Equal("legacy-user", ReadCredentialUsername(first.CredentialsPath));
            var migratedSession = Path.Combine(first.SessionsPath, "100.session");
            Assert.True(File.Exists(migratedSession));
            Assert.Single(Directory.GetFiles(
                persistentRoot,
                ".storage-database-migration-v1-*.complete",
                SearchOption.TopDirectoryOnly));
            Assert.True(File.Exists(Path.Combine(
                persistentRoot,
                ".storage-credential-migration-v1.complete")));
            Assert.True(File.Exists(Path.Combine(
                persistentRoot,
                ".storage-session-migration-v1.complete")));

            using (var connection = new SqliteConnection(
                       $"Data Source={first.DatabasePath};Pooling=False"))
            {
                connection.Open();
                ExecuteNonQuery(connection, "DELETE FROM Accounts;");
            }
            File.Delete(migratedSession);
            File.WriteAllText(
                first.CredentialsPath,
                CreateCredentialJson("tgpanel", "tgpanel123", mustChangePassword: true));

            var second = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal(0, ReadAccountCount(second.DatabasePath));
            Assert.Equal("tgpanel", ReadCredentialUsername(second.CredentialsPath));
            Assert.False(File.Exists(migratedSession));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_DatabasePathChangeDoesNotReapplyCredentialOrSessionMigration()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            var legacySessions = Path.Combine(legacyRoot, "sessions");
            Directory.CreateDirectory(legacySessions);
            Directory.CreateDirectory(persistentRoot);
            CreateAccountsDatabase(Path.Combine(legacyRoot, "telegram_panel.db"), 1);
            File.WriteAllText(
                Path.Combine(legacyRoot, "admin_auth.json"),
                CreateCredentialJson("legacy-user", "legacy-password", mustChangePassword: false));
            File.WriteAllText(Path.Combine(legacySessions, "100.session"), "legacy-session");

            var first = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));
            var migratedSession = Path.Combine(first.SessionsPath, "100.session");
            File.Delete(migratedSession);
            File.WriteAllText(
                first.CredentialsPath,
                CreateCredentialJson("tgpanel", "tgpanel123", mustChangePassword: true));

            var changedConfiguration = CreateConfiguration(persistentRoot);
            changedConfiguration["ConnectionStrings:DefaultConnection"] =
                "Data Source=alternate.db";
            var second = PersistentStorageBootstrapper.Initialize(
                changedConfiguration,
                new TestEnvironment(legacyRoot));

            Assert.Equal("tgpanel", ReadCredentialUsername(second.CredentialsPath));
            Assert.False(File.Exists(migratedSession));
            Assert.Equal(2, Directory.GetFiles(
                persistentRoot,
                ".storage-database-migration-v1-*.complete",
                SearchOption.TopDirectoryOnly).Length);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_RejectsConcurrentLegacyMigrationForSameWritableRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(persistentRoot);
            var lockPath = Path.Combine(persistentRoot, ".storage-migration-v1.lock");
            using var heldLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PersistentStorageBootstrapper.Initialize(
                    CreateConfiguration(persistentRoot),
                    new TestEnvironment(root)));

            Assert.Contains("另一个面板进程", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                "*.complete",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void CompleteDatabaseMigration_MarksFreshDatabaseAfterSchemaCreation()
    {
        var root = CreateTempDirectory();
        try
        {
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(persistentRoot);

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(root));

            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                ".storage-database-migration-v1-*.complete",
                SearchOption.TopDirectoryOnly));

            CreateAccountsDatabase(paths.DatabasePath, 0);
            PersistentStorageBootstrapper.CompleteDatabaseMigration(paths);

            Assert.Single(Directory.GetFiles(
                persistentRoot,
                ".storage-database-migration-v1-*.complete",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_DoesNotFallBackToLegacyDatabaseAfterCompletedTargetIsCorrupted()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            CreateAccountsDatabase(Path.Combine(legacyRoot, "telegram_panel.db"), 2);

            var first = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));
            File.WriteAllText(first.DatabasePath, "corrupted-persistent-database");

            var exception = Assert.Throws<InvalidDataException>(() =>
                PersistentStorageBootstrapper.Initialize(
                    CreateConfiguration(persistentRoot),
                    new TestEnvironment(legacyRoot)));

            Assert.Contains("旧目录迁移已完成", exception.Message, StringComparison.Ordinal);
            Assert.Equal("corrupted-persistent-database", File.ReadAllText(first.DatabasePath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_PrefersCandidateWithAccountsOverNewerAuxiliaryData()
    {
        var root = CreateTempDirectory();
        try
        {
            var currentRoot = Path.Combine(root, "app-current");
            var auxiliaryRoot = Path.Combine(root, "app-previous-newer");
            var accountsRoot = Path.Combine(root, "app-previous-older");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(currentRoot);
            Directory.CreateDirectory(auxiliaryRoot);
            Directory.CreateDirectory(accountsRoot);
            Directory.CreateDirectory(persistentRoot);

            var targetPath = Path.Combine(persistentRoot, "telegram_panel.db");
            var auxiliaryPath = Path.Combine(auxiliaryRoot, "telegram_panel.db");
            var accountsPath = Path.Combine(accountsRoot, "telegram_panel.db");
            CreateAccountsDatabase(targetPath, 0);
            CreateSqliteDatabase(auxiliaryPath, "newer-auxiliary-data");
            CreateAccountsDatabase(accountsPath, 2);
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(auxiliaryPath, now.AddMinutes(-1));
            File.SetLastWriteTimeUtc(accountsPath, now.AddMinutes(-2));

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(currentRoot));

            Assert.Equal(2, ReadAccountCount(paths.DatabasePath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_CompletesMigrationWhenOnlyLegacyDatabaseCandidateIsPermanentlyInvalid()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            CreateAccountsDatabase(Path.Combine(persistentRoot, "telegram_panel.db"), 0);
            var legacyPath = Path.Combine(legacyRoot, "telegram_panel.db");
            File.WriteAllText(legacyPath, "broken-legacy-database");

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal(0, ReadAccountCount(paths.DatabasePath));
            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                "telegram_panel.db.invalid-*",
                SearchOption.TopDirectoryOnly));
            Assert.Single(Directory.GetFiles(
                persistentRoot,
                ".storage-database-migration-v1-*.complete",
                SearchOption.TopDirectoryOnly));

            File.Delete(legacyPath);
            CreateAccountsDatabase(legacyPath, 2);
            var retried = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal(0, ReadAccountCount(retried.DatabasePath));
            Assert.Single(Directory.GetFiles(
                persistentRoot,
                ".storage-database-migration-v1-*.complete",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_DoesNotFallBackWhenNewestDatabaseCandidateIsTemporarilyLocked()
    {
        var root = CreateTempDirectory();
        try
        {
            var currentRoot = Path.Combine(root, "app-current");
            var newerRoot = Path.Combine(root, "app-previous-newer");
            var olderRoot = Path.Combine(root, "app-previous-older");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(currentRoot);
            Directory.CreateDirectory(newerRoot);
            Directory.CreateDirectory(olderRoot);
            Directory.CreateDirectory(persistentRoot);

            var targetPath = Path.Combine(persistentRoot, "telegram_panel.db");
            var newerPath = Path.Combine(newerRoot, "telegram_panel.db");
            var olderPath = Path.Combine(olderRoot, "telegram_panel.db");
            CreateAccountsDatabase(targetPath, 0);
            CreateAccountsDatabase(newerPath, 1);
            CreateAccountsDatabase(olderPath, 3);
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(newerPath, now.AddMinutes(-1));
            File.SetLastWriteTimeUtc(olderPath, now.AddMinutes(-2));

            using (var lockedConnection = new SqliteConnection(
                       $"Data Source={newerPath};Mode=ReadWrite;Pooling=False;Default Timeout=0"))
            {
                lockedConnection.Open();
                ExecuteNonQuery(lockedConnection, "BEGIN EXCLUSIVE;");

                var exception = Assert.Throws<InvalidOperationException>(() =>
                    PersistentStorageBootstrapper.Initialize(
                        CreateConfiguration(persistentRoot),
                        new TestEnvironment(currentRoot)));

                Assert.Contains("暂时无法读取", exception.Message, StringComparison.Ordinal);
                Assert.Empty(Directory.GetFiles(
                    persistentRoot,
                    ".storage-database-migration-v1-*.complete",
                    SearchOption.TopDirectoryOnly));

                ExecuteNonQuery(lockedConnection, "ROLLBACK;");
            }

            Assert.Equal(0, ReadAccountCount(targetPath));

            var retried = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(currentRoot));

            Assert.Equal(1, ReadAccountCount(retried.DatabasePath));
            Assert.Single(Directory.GetFiles(
                persistentRoot,
                ".storage-database-migration-v1-*.complete",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_DoesNotOverwriteDatabaseChangedBeforeSnapshotPromotion()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            var targetPath = Path.Combine(persistentRoot, "telegram_panel.db");
            CreateAccountsDatabase(targetPath, 0);
            CreateAccountsDatabase(Path.Combine(legacyRoot, "telegram_panel.db"), 2);
            var targetChanged = false;

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PersistentStorageBootstrapper.Initialize(
                    CreateConfiguration(persistentRoot),
                    new TestEnvironment(legacyRoot),
                    message =>
                    {
                        if (targetChanged
                            || !message.StartsWith("准备提升旧数据库快照", StringComparison.Ordinal))
                        {
                            return;
                        }

                        using var connection = new SqliteConnection(
                            $"Data Source={targetPath};Pooling=False");
                        connection.Open();
                        ExecuteNonQuery(connection, "INSERT INTO Accounts (Id) VALUES (99);");
                        targetChanged = true;
                    }));

            Assert.True(targetChanged);
            Assert.Contains("发生变化", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, ReadAccountCount(targetPath));
            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                "telegram_panel.db.invalid-*",
                SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                ".storage-database-migration-v1-*.complete",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_RestoresHyphenatedDatabaseFromWritableRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            var currentRoot = Path.Combine(root, "app-current");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(currentRoot);
            Directory.CreateDirectory(persistentRoot);
            CreateAccountsDatabase(Path.Combine(persistentRoot, "telegram-panel.db"), 4);

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(currentRoot));

            Assert.Equal(4, ReadAccountCount(paths.DatabasePath));
            Assert.Equal(4, ReadAccountCount(Path.Combine(persistentRoot, "telegram-panel.db")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_ReplacesZeroLengthCredentialsAndPreservesInvalidTarget()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "persistent");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            File.WriteAllText(
                Path.Combine(legacyRoot, "admin_auth.json"),
                CreateCredentialJson("restored-admin"));
            var targetPath = Path.Combine(persistentRoot, "admin_auth.json");
            File.WriteAllBytes(targetPath, Array.Empty<byte>());

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal("restored-admin", ReadCredentialUsername(paths.CredentialsPath));
            var preservedTargets = Directory.GetFiles(
                persistentRoot,
                "admin_auth.json.invalid-*",
                SearchOption.TopDirectoryOnly);
            var preservedTarget = Assert.Single(preservedTargets);
            Assert.Equal(0, new FileInfo(preservedTarget).Length);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(true, 32, "legacy-user")]
    [InlineData(false, 16, "legacy-user")]
    [InlineData(false, 32, " legacy-user ")]
    public void Initialize_RejectsCredentialCandidatesThatRuntimeCannotAuthenticate(
        bool lowercaseUsername,
        int hashLength,
        string username)
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            var candidate = CreateCredentialJson(
                username,
                "legacy-password",
                mustChangePassword: false,
                hashLength: hashLength);
            if (lowercaseUsername)
            {
                candidate = candidate.Replace(
                    "\"Username\":",
                    "\"username\":",
                    StringComparison.Ordinal);
            }
            File.WriteAllText(Path.Combine(legacyRoot, "admin_auth.json"), candidate);
            var targetPath = Path.Combine(persistentRoot, "admin_auth.json");
            File.WriteAllBytes(targetPath, Array.Empty<byte>());

            Assert.Throws<InvalidDataException>(() =>
                PersistentStorageBootstrapper.Initialize(
                    CreateConfiguration(persistentRoot),
                    new TestEnvironment(legacyRoot)));
            Assert.Equal(0, new FileInfo(targetPath).Length);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Initialize_ReplacesGeneratedDefaultCredentialsAndPreservesSource(string? initialPassword)
    {
        var root = CreateTempDirectory();
        try
        {
            var currentRoot = Path.Combine(root, "app-current");
            var previousRoot = Path.Combine(root, "app-previous");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(currentRoot);
            Directory.CreateDirectory(previousRoot);
            Directory.CreateDirectory(persistentRoot);

            var targetPath = Path.Combine(persistentRoot, "admin_auth.json");
            var sourcePath = Path.Combine(previousRoot, "admin_auth.json");
            File.WriteAllText(
                targetPath,
                CreateCredentialJson("tgpanel", "tgpanel123", mustChangePassword: true));
            File.WriteAllText(
                sourcePath,
                CreateCredentialJson("restored-user", "restored-password", mustChangePassword: false));

            var configuration = CreateConfiguration(persistentRoot);
            configuration["AdminAuth:InitialPassword"] = initialPassword;
            var paths = PersistentStorageBootstrapper.Initialize(configuration, new TestEnvironment(currentRoot));

            Assert.Equal("restored-user", ReadCredentialUsername(paths.CredentialsPath));
            Assert.Equal("restored-user", ReadCredentialUsername(sourcePath));
            var backups = Directory.GetFiles(
                persistentRoot,
                "admin_auth.json.invalid-*",
                SearchOption.TopDirectoryOnly);
            Assert.Single(backups);
            Assert.Equal("tgpanel", ReadCredentialUsername(backups[0]));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initialize_DoesNotOverwriteUserModifiedCredentials(bool mustChangePassword)
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            File.WriteAllText(
                Path.Combine(legacyRoot, "admin_auth.json"),
                CreateCredentialJson("legacy-user", "legacy-password", mustChangePassword: false));
            var targetPath = Path.Combine(persistentRoot, "admin_auth.json");
            File.WriteAllText(
                targetPath,
                CreateCredentialJson("current-user", "current-password", mustChangePassword));

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal("current-user", ReadCredentialUsername(paths.CredentialsPath));
            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                "admin_auth.json.invalid-*",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_CompletesMigrationWhenOnlyLegacyCredentialCandidateIsPermanentlyInvalid()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            var targetPath = Path.Combine(persistentRoot, "admin_auth.json");
            File.WriteAllText(
                targetPath,
                CreateCredentialJson("tgpanel", "tgpanel123", mustChangePassword: true));
            var legacyPath = Path.Combine(legacyRoot, "admin_auth.json");
            File.WriteAllText(legacyPath, "{ invalid-json");

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal("tgpanel", ReadCredentialUsername(paths.CredentialsPath));
            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                "admin_auth.json.invalid-*",
                SearchOption.TopDirectoryOnly));
            Assert.True(File.Exists(Path.Combine(
                persistentRoot,
                ".storage-credential-migration-v1.complete")));

            File.WriteAllText(
                legacyPath,
                CreateCredentialJson("recovered-user", "recovered-password", mustChangePassword: false));
            var retried = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal("tgpanel", ReadCredentialUsername(retried.CredentialsPath));
            Assert.True(File.Exists(Path.Combine(
                persistentRoot,
                ".storage-credential-migration-v1.complete")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_RetriesCredentialMigrationAfterLockedCandidateIsReleased()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            var legacyPath = Path.Combine(legacyRoot, "admin_auth.json");
            File.WriteAllText(
                legacyPath,
                CreateCredentialJson("legacy-user", "legacy-password", mustChangePassword: false));
            var targetPath = Path.Combine(persistentRoot, "admin_auth.json");
            File.WriteAllText(
                targetPath,
                CreateCredentialJson("tgpanel", "tgpanel123", mustChangePassword: true));
            var markerPath = Path.Combine(
                persistentRoot,
                ".storage-credential-migration-v1.complete");

            using (var heldFile = new FileStream(
                       legacyPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    PersistentStorageBootstrapper.Initialize(
                        CreateConfiguration(persistentRoot),
                        new TestEnvironment(legacyRoot)));

                Assert.Contains("暂时无法读取", exception.Message, StringComparison.Ordinal);
                Assert.False(File.Exists(markerPath));
                Assert.Equal("tgpanel", ReadCredentialUsername(targetPath));
            }

            var retried = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal("legacy-user", ReadCredentialUsername(retried.CredentialsPath));
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_DoesNotOverwriteCredentialsChangedBeforeSnapshotPromotion()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            File.WriteAllText(
                Path.Combine(legacyRoot, "admin_auth.json"),
                CreateCredentialJson("legacy-user", "legacy-password", mustChangePassword: false));
            var targetPath = Path.Combine(persistentRoot, "admin_auth.json");
            File.WriteAllText(
                targetPath,
                CreateCredentialJson("tgpanel", "tgpanel123", mustChangePassword: true));
            var targetChanged = false;

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PersistentStorageBootstrapper.Initialize(
                    CreateConfiguration(persistentRoot),
                    new TestEnvironment(legacyRoot),
                    message =>
                    {
                        if (targetChanged
                            || !message.StartsWith("准备提升旧后台凭据快照", StringComparison.Ordinal))
                        {
                            return;
                        }

                        File.WriteAllText(
                            targetPath,
                            CreateCredentialJson(
                                "current-user",
                                "current-password",
                                mustChangePassword: false));
                        targetChanged = true;
                    }));

            Assert.True(targetChanged);
            Assert.Contains("发生变化", exception.Message, StringComparison.Ordinal);
            Assert.Equal("current-user", ReadCredentialUsername(targetPath));
            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                "admin_auth.json.invalid-*",
                SearchOption.TopDirectoryOnly));
            Assert.False(File.Exists(Path.Combine(
                persistentRoot,
                ".storage-credential-migration-v1.complete")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_DoesNotPromoteDefaultCandidateFromDifferentInitialConfiguration()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);

            var targetPath = Path.Combine(persistentRoot, "admin_auth.json");
            File.WriteAllText(
                targetPath,
                CreateCredentialJson("tgpanel", "tgpanel123", mustChangePassword: true));
            File.WriteAllText(
                Path.Combine(legacyRoot, "admin_auth.json"),
                CreateCredentialJson("old-tgpanel", "old-password", mustChangePassword: true));

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal("tgpanel", ReadCredentialUsername(paths.CredentialsPath));
            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                "admin_auth.json.invalid-*",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("CreatedAtUtc")]
    [InlineData("UpdatedAtUtc")]
    public void Initialize_DoesNotPromoteCredentialCandidateWithInvalidRuntimeField(
        string field)
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            var targetPath = Path.Combine(persistentRoot, "admin_auth.json");
            File.WriteAllText(
                targetPath,
                CreateCredentialJson("tgpanel", "tgpanel123", mustChangePassword: true));

            var candidate = JsonNode.Parse(CreateCredentialJson(
                "legacy-user",
                "legacy-password",
                mustChangePassword: false))!.AsObject();
            candidate[field] = "invalid";
            File.WriteAllText(
                Path.Combine(legacyRoot, "admin_auth.json"),
                candidate.ToJsonString());

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal("tgpanel", ReadCredentialUsername(paths.CredentialsPath));
            Assert.Empty(Directory.GetFiles(
                persistentRoot,
                "admin_auth.json.invalid-*",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_ThrowsWhenExistingDatabaseIsInvalidAndNoRecoverySourceExists()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "persistent");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            var targetPath = Path.Combine(persistentRoot, "telegram_panel.db");
            File.WriteAllText(targetPath, "not-a-sqlite-database");

            var exception = Assert.Throws<InvalidDataException>(() =>
                PersistentStorageBootstrapper.Initialize(
                    CreateConfiguration(persistentRoot),
                    new TestEnvironment(legacyRoot)));

            Assert.Contains("未找到可恢复的旧数据库", exception.Message, StringComparison.Ordinal);
            Assert.Equal("not-a-sqlite-database", File.ReadAllText(targetPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_ThrowsWhenLegacyDatabaseValidationFailsWithoutCreatingTarget()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "persistent");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            File.WriteAllText(Path.Combine(legacyRoot, "telegram_panel.db"), "broken-legacy-db");
            var targetPath = Path.Combine(persistentRoot, "telegram_panel.db");

            var exception = Assert.Throws<InvalidDataException>(() =>
                PersistentStorageBootstrapper.Initialize(
                    CreateConfiguration(persistentRoot),
                    new TestEnvironment(legacyRoot)));

            Assert.Contains("全部未通过完整性校验", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(targetPath));
            Assert.Equal(
                "broken-legacy-db",
                File.ReadAllText(Path.Combine(legacyRoot, "telegram_panel.db")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_RecoversCommittedWalDataAsConsistentSnapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "persistent");
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(persistentRoot);
            var sourcePath = Path.Combine(legacyRoot, "telegram_panel.db");

            using var source = new SqliteConnection($"Data Source={sourcePath}");
            source.Open();
            ExecuteNonQuery(source, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(source, "PRAGMA wal_autocheckpoint=0;");
            ExecuteNonQuery(source, "CREATE TABLE RecoveryMarker (Value TEXT NOT NULL);");
            ExecuteNonQuery(source, "INSERT INTO RecoveryMarker (Value) VALUES ('wal-db');");
            Assert.True(File.Exists(sourcePath + "-wal"));

            var paths = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal("wal-db", ReadDatabaseMarker(paths.DatabasePath));
            Assert.False(File.Exists(paths.DatabasePath + "-wal"));
            Assert.False(File.Exists(paths.DatabasePath + "-shm"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_RetriesSessionMigrationAfterLockedSourceIsReleased()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacyRoot = Path.Combine(root, "old-app");
            var persistentRoot = Path.Combine(root, "data");
            var legacySessions = Path.Combine(legacyRoot, "sessions");
            Directory.CreateDirectory(legacySessions);
            Directory.CreateDirectory(persistentRoot);
            var legacySession = Path.Combine(legacySessions, "100.session");
            File.WriteAllText(legacySession, "legacy-session");
            var markerPath = Path.Combine(
                persistentRoot,
                ".storage-session-migration-v1.complete");

            using (var heldFile = new FileStream(
                       legacySession,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                Assert.Throws<IOException>(() =>
                    PersistentStorageBootstrapper.Initialize(
                        CreateConfiguration(persistentRoot),
                        new TestEnvironment(legacyRoot)));

                Assert.False(File.Exists(markerPath));
            }

            var retried = PersistentStorageBootstrapper.Initialize(
                CreateConfiguration(persistentRoot),
                new TestEnvironment(legacyRoot));

            Assert.Equal(
                "legacy-session",
                File.ReadAllText(Path.Combine(retried.SessionsPath, "100.session")));
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Initialize_MergesMissingSessionsFromAllLegacyDirectories()
    {
        var root = CreateTempDirectory();
        try
        {
            var currentRoot = Path.Combine(root, "app-current");
            var previousRoot = Path.Combine(root, "app-previous");
            var sessionsRoot = Path.Combine(root, "sessions");
            Directory.CreateDirectory(Path.Combine(currentRoot, "sessions"));
            Directory.CreateDirectory(Path.Combine(previousRoot, "sessions"));
            Directory.CreateDirectory(sessionsRoot);
            File.WriteAllText(Path.Combine(currentRoot, "sessions", "current.session"), "current");
            File.WriteAllText(Path.Combine(previousRoot, "sessions", "previous.session"), "previous");
            File.WriteAllText(Path.Combine(sessionsRoot, "existing.session"), "existing");

            var configuration = CreateConfiguration(root);
            var paths = PersistentStorageBootstrapper.Initialize(
                configuration,
                new TestEnvironment(currentRoot));

            Assert.Equal("existing", File.ReadAllText(Path.Combine(paths.SessionsPath, "existing.session")));
            Assert.Equal("current", File.ReadAllText(Path.Combine(paths.SessionsPath, "current.session")));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(paths.SessionsPath, "previous.session")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static ConfigurationManager CreateConfiguration(string persistentRoot)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:RootPath"] = persistentRoot,
            ["ConnectionStrings:DefaultConnection"] = "Data Source=telegram_panel.db",
            ["AdminAuth:CredentialsPath"] = "admin_auth.json",
            ["Telegram:SessionsPath"] = "sessions"
        });
        return configuration;
    }

    private static void CreateSqliteDatabase(string path, string marker)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        ExecuteNonQuery(connection, "CREATE TABLE RecoveryMarker (Value TEXT NOT NULL);");

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO RecoveryMarker (Value) VALUES ($value);";
        insert.Parameters.AddWithValue("$value", marker);
        insert.ExecuteNonQuery();
    }

    private static void CreateAccountsDatabase(string path, int accountCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        ExecuteNonQuery(connection, "CREATE TABLE Accounts (Id INTEGER NOT NULL PRIMARY KEY);");
        for (var index = 1; index <= accountCount; index++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Accounts (Id) VALUES ($id);";
            insert.Parameters.AddWithValue("$id", index);
            insert.ExecuteNonQuery();
        }
    }

    private static long ReadAccountCount(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Accounts;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ReadDatabaseMarker(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM RecoveryMarker LIMIT 1;";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string CreateCredentialJson(string username)
    {
        return CreateCredentialJson(username, "unused-password", mustChangePassword: false);
    }

    private static string CreateCredentialJson(
        string username,
        string password,
        bool mustChangePassword,
        int hashLength = 32)
    {
        var salt = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        const int iterations = 1_000;
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            hashLength);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1,
            Username = username,
            SaltBase64 = Convert.ToBase64String(salt),
            HashBase64 = Convert.ToBase64String(hash),
            Iterations = iterations,
            MustChangePassword = mustChangePassword,
            CreatedAtUtc = DateTime.UnixEpoch,
            UpdatedAtUtc = DateTime.UnixEpoch
        });
    }

    private static string ReadCredentialUsername(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("Username").GetString() ?? string.Empty;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "telegram-panel-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖断言结果。
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public TestEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string ApplicationName { get; set; } = "TelegramPanel.Web.Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
