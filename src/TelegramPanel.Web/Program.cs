using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using TelegramPanel.Core;
using TelegramPanel.Data;

var builder = WebApplication.CreateBuilder(args);

// 可选的本地覆盖配置（不要提交到仓库）
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// 配置 Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/telegram-panel-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// MudBlazor
builder.Services.AddMudServices();

// 数据库上下文
var configuredConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=telegram-panel.db";

// 统一 SQLite 数据库路径为 ContentRoot 下的文件，避免因工作目录不同导致连到错误的 db（从而出现 no such table）
var connectionString = configuredConnectionString;
try
{
    var dataSourcePrefix = "Data Source=";
    var trimmed = configuredConnectionString.Trim();
    if (trimmed.StartsWith(dataSourcePrefix, StringComparison.OrdinalIgnoreCase))
    {
        var dataSource = trimmed.Substring(dataSourcePrefix.Length).Trim().Trim('"');
        if (!Path.IsPathRooted(dataSource))
        {
            var absolute = Path.Combine(builder.Environment.ContentRootPath, dataSource);
            connectionString = $"{dataSourcePrefix}{absolute}";
        }
    }
}
catch (Exception ex)
{
    Log.Warning(ex, "Failed to normalize sqlite connection string, using configured value");
    connectionString = configuredConnectionString;
}
builder.Services.AddTelegramPanelData(connectionString);

// Telegram Panel 核心服务
builder.Services.AddTelegramPanelCore();

// TODO: 添加 Hangfire
// builder.Services.AddHangfire(config => config.UseInMemoryStorage());
// builder.Services.AddHangfireServer();

var app = builder.Build();

// 确保数据库已创建并应用最新迁移
// 注意：SQLite 在首次连接时只会创建空文件，不会自动建表，因此需要显式执行迁移。
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Log.Information("Using sqlite connection string: {ConnectionString}", connectionString);
        var migrations = db.Database.GetMigrations().ToList();
        Log.Information("EF migrations discovered: {Count}", migrations.Count);

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        List<string> tables;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            tables = new List<string>();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        var hasHistory = tables.Contains("__EFMigrationsHistory", StringComparer.Ordinal);
        var hasAnyUserTables = tables.Any(t =>
            !string.Equals(t, "__EFMigrationsHistory", StringComparison.Ordinal) &&
            !t.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase));

        if (migrations.Count > 0)
        {
            // 只在「新库」或「已有迁移历史」时执行 Migrate；避免对已有表但无历史的库误执行迁移导致冲突
            if (!hasAnyUserTables || hasHistory)
            {
                db.Database.Migrate();
            }
            else
            {
                Log.Warning("Database has schema tables but no __EFMigrationsHistory; skipping Migrate() to avoid conflicts");
            }
        }
        else
        {
            db.Database.EnsureCreated();
        }

        // 刷新表清单（上面可能已创建/迁移）
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            tables = new List<string>();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        // 兜底：开发阶段允许轻量演进 schema（避免已有库无迁移历史时无法自动更新）
        // 仅做“新增列”这种非破坏性变更。
        if (tables.Contains("Accounts", StringComparer.Ordinal))
        {
            EnsureSqliteColumn(conn, tableName: "Accounts", columnName: "Nickname", columnType: "TEXT");
        }

        // 兜底：若 Accounts 仍不存在（常见于库被创建为空文件/仅有历史表），给出可恢复的自愈
        if (!tables.Contains("Accounts", StringComparer.Ordinal))
        {
            hasHistory = tables.Contains("__EFMigrationsHistory", StringComparer.Ordinal);
            if (tables.Count == 0)
            {
                Log.Warning("Database has no tables; calling EnsureCreated()");
                db.Database.EnsureCreated();
            }
            else if (tables.Count == 1 && hasHistory)
            {
                Log.Warning("Database only contains __EFMigrationsHistory but no schema tables; recreating schema via EnsureCreated()");
                db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS __EFMigrationsHistory;");
                db.Database.EnsureCreated();
            }
            else
            {
                Log.Error("Accounts table missing. Existing tables: {Tables}", string.Join(", ", tables));
            }
        }

        void EnsureSqliteColumn(System.Data.Common.DbConnection connection, string tableName, string columnName, string columnType)
        {
            try
            {
                var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = $"PRAGMA table_info('{tableName}');";
                    using var r = pragma.ExecuteReader();
                    while (r.Read())
                    {
                        var name = r.GetString(1);
                        existingColumns.Add(name);
                    }
                }

                if (existingColumns.Contains(columnName))
                    return;

                using (var alter = connection.CreateCommand())
                {
                    alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType};";
                    alter.ExecuteNonQuery();
                }

                Log.Information("Applied schema patch: {Table}.{Column} added", tableName, columnName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to ensure sqlite column {Table}.{Column}", tableName, columnName);
            }
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Database migration failed");
        throw;
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

// 静态文件（包括 MudBlazor 等 NuGet 包的静态 Web 资源）
app.UseStaticFiles();

app.UseAntiforgery();

// Serilog 请求日志
app.UseSerilogRequestLogging();

app.MapRazorComponents<TelegramPanel.Web.Components.App>()
    .AddInteractiveServerRenderMode();

// TODO: Hangfire Dashboard
// app.MapHangfireDashboard("/hangfire");

Log.Information("Telegram Panel started");

app.Run();
