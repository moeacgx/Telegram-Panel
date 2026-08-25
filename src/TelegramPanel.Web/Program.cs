using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Claims;
using Serilog;
using TelegramPanel.Core;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;
using TelegramPanel.Modules;
using TelegramPanel.Web.Modules;
using TelegramPanel.Web.Api;
using TelegramPanel.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.FileProviders;
using System.Diagnostics;
using System.Data.Common;

TelegramLayerCompatibility.EnsureRegistered();

// 诊断：对某个目录下的 *.json/*.session 做一次“可转换/可校验”检查（不写数据库）
// 用法：先通过 Telegram__Proxy__Server/Port 配置代理；若确需直连，额外传入 --allow-direct。
// dotnet run --project src/TelegramPanel.Web -- --diag-session-dir "D:/path/to/dir"
if (args.Length >= 2 && string.Equals(args[0], "--diag-session-dir", StringComparison.OrdinalIgnoreCase))
{
    var dir = args[1];
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"目录不存在：{dir}");
        return;
    }

    using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
    {
        b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
    });
    var logger = loggerFactory.CreateLogger("SessionDiag");
    var diagConfiguration = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();
    var allowDirect = args.Skip(2)
        .Any(x => string.Equals(x, "--allow-direct", StringComparison.OrdinalIgnoreCase));
    ProxyConnectionOptions? diagProxy;
    if (GlobalTelegramProxyConfiguration.GetSourceMode(diagConfiguration)
        == GlobalTelegramProxyConfiguration.ExistingSourceMode)
    {
        var selectedProxyId = GlobalTelegramProxyConfiguration.GetSelectedProxyId(
            diagConfiguration,
            requireEnabled: false);
        Console.Error.WriteLine(
            $"诊断配置引用已有代理 #{selectedProxyId?.ToString() ?? "未选择"}，"
            + "但独立诊断模式不会在应用启动前读取代理数据库。"
            + "请改用 Telegram__Proxy__SourceMode=manual 并提供 Server/Port，"
            + "或明确追加 --allow-direct 承担公网 IP 暴露风险。");
        if (!allowDirect)
            return;
        diagProxy = null;
    }
    else
    {
        try
        {
            diagProxy = GlobalTelegramProxyConfiguration.Build(diagConfiguration);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"诊断代理配置无效：{ex.Message}");
            return;
        }
    }

    if (diagProxy == null && !allowDirect)
    {
        Console.Error.WriteLine(
            "诊断可能连接 Telegram，已阻止默认直连。请配置 Telegram__Proxy__Server/Port，"
            + "或明确追加 --allow-direct 承担公网 IP 暴露风险。");
        return;
    }

    var jsonFiles = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (jsonFiles.Count == 0)
    {
        Console.Error.WriteLine("目录内未找到任何 .json 文件");
        return;
    }

    var tempOutDir = Path.Combine(Path.GetTempPath(), "telegram-panel-diag-sessions");
    Directory.CreateDirectory(tempOutDir);

    foreach (var jsonPath in jsonFiles)
    {
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGetInt(root, out var apiId, "api_id", "app_id"))
            {
                Console.WriteLine($"[FAIL] {Path.GetFileName(jsonPath)}: 缺少 api_id/app_id");
                continue;
            }

            if (!TryGetString(root, out var apiHash, "api_hash", "app_hash") || string.IsNullOrWhiteSpace(apiHash))
            {
                Console.WriteLine($"[FAIL] {Path.GetFileName(jsonPath)}: 缺少 api_hash/app_hash");
                continue;
            }

            if (!TryGetString(root, out var phone, "phone") || string.IsNullOrWhiteSpace(phone))
            {
                if (!TryGetString(root, out phone, "session_file", "sessionFile") || string.IsNullOrWhiteSpace(phone))
                    phone = Path.GetFileNameWithoutExtension(jsonPath);

                if (string.IsNullOrWhiteSpace(phone))
                {
                    Console.WriteLine($"[FAIL] {Path.GetFileName(jsonPath)}: 缺少 phone");
                    continue;
                }
            }

            _ = TryGetLong(root, out var userId, "user_id", "uid");
            _ = TryGetString(root, out var sessionString, "session_string", "sessionString");

            phone = phone.Trim();
            apiHash = apiHash.Trim();
            sessionString = string.IsNullOrWhiteSpace(sessionString) ? null : sessionString.Trim();

            var baseName = Path.GetFileNameWithoutExtension(jsonPath);
            var sessionPath = Path.Combine(dir, $"{baseName}.session");
            if (!File.Exists(sessionPath))
                sessionPath = Directory.EnumerateFiles(dir, "*.session", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
            {
                Console.WriteLine($"[FAIL] {phone}: 未找到 .session 文件（json={Path.GetFileName(jsonPath)}）");
                continue;
            }

            var targetSessionPath = Path.Combine(tempOutDir, $"{phone}.session");

            TelegramPanel.Core.Services.Telegram.SessionDataConverter.SessionConvertResult converted;
            if (TelegramPanel.Core.Services.Telegram.SessionDataConverter.LooksLikeSqliteSession(sessionPath))
            {
                if (!string.IsNullOrWhiteSpace(sessionString))
                {
                    converted = await TelegramPanel.Core.Services.Telegram.SessionDataConverter.TryCreateWTelegramSessionFromSessionStringAsync(
                        sessionString: sessionString,
                        apiId: apiId,
                        apiHash: apiHash,
                        targetSessionPath: targetSessionPath,
                        phone: phone,
                        userId: userId,
                        logger: logger,
                        proxy: diagProxy);
                }
                else
                {
                    converted = await TelegramPanel.Core.Services.Telegram.SessionDataConverter.TryCreateWTelegramSessionFromTelethonSqliteFileAsync(
                        sqliteSessionPath: sessionPath,
                        apiId: apiId,
                        apiHash: apiHash,
                        targetSessionPath: targetSessionPath,
                        phone: phone,
                        userId: userId,
                        logger: logger,
                        proxy: diagProxy);
                }
            }
            else
            {
                File.Copy(sessionPath, targetSessionPath, overwrite: true);
                converted = TelegramPanel.Core.Services.Telegram.SessionDataConverter.SessionConvertResult.Success();
            }

            if (converted.Ok)
                Console.WriteLine($"[OK] {phone}: 可用（输出={targetSessionPath}）");
            else
                Console.WriteLine($"[FAIL] {phone}: {converted.Reason}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {Path.GetFileName(jsonPath)}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    return;

    static bool TryGetInt(System.Text.Json.JsonElement root, out int value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == System.Text.Json.JsonValueKind.Number && prop.TryGetInt32(out var i))
                {
                    value = i;
                    return true;
                }

                if (prop.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(prop.GetString(), out var isv))
                {
                    value = isv;
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }

    static bool TryGetLong(System.Text.Json.JsonElement root, out long? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == System.Text.Json.JsonValueKind.Number && prop.TryGetInt64(out var l))
                {
                    value = l;
                    return true;
                }

                if (prop.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(prop.GetString(), out var ls))
                {
                    value = ls;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    static bool TryGetString(System.Text.Json.JsonElement root, out string? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                value = prop.GetString();
                return true;
            }
        }

        value = null;
        return false;
    }
}

// 旧容器的一键更新只会替换 /data/app-current，不会自动更新镜像内的 /entrypoint.sh。
// 必须在创建主机前先安装新版入口并记录启动尝试，后续初始化失败时才能在下一次启动自动回滚。
SelfUpdateStartupCoordinator.PrepareCurrentProcess(
    AppContext.BaseDirectory,
    message => Console.WriteLine($"[SelfUpdate] {message}"));

var builder = WebApplication.CreateBuilder(args);

// 可选的本地覆盖配置（不要提交到仓库）
// Docker 部署时该文件通常是指向 /data/appsettings.local.json 的符号链接；首次启动可能是“悬空链接”，
// 直接加载会抛 FileNotFoundException 导致容器不断重启，因此这里先确保文件存在一个空 JSON。
try
{
    var localOverridePath = LocalConfigFile.ResolvePath(builder.Configuration, builder.Environment);
    if (!File.Exists(localOverridePath))
        File.WriteAllText(localOverridePath, "{}", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to ensure appsettings.local.json exists: {ex.Message}");
}

try
{
    // 注意：如果该路径是“悬空符号链接”，配置系统可能会认为文件存在并尝试打开，从而抛 FileNotFoundException。
    // 本地覆盖配置不应该阻塞启动，因此这里兜底吞掉 FileNotFoundException。
    var localOverridePath = LocalConfigFile.ResolvePath(builder.Configuration, builder.Environment);
    builder.Configuration.AddJsonFile(localOverridePath, optional: true, reloadOnChange: true);
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"Ignoring missing appsettings.local.json: {ex.Message}");
}

// 自更新会切换 ContentRootPath。数据库、后台凭据和 Session 必须在此之前统一到持久化目录，
// 并尝试从旧的 /app、app-current、app-previous 目录恢复，避免升级后打开空库或重新生成默认凭据。
PersistentStoragePaths persistentStorage;
try
{
    persistentStorage = PersistentStorageBootstrapper.Initialize(
        builder.Configuration,
        builder.Environment,
        message => Console.WriteLine($"[Storage] {message}"));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to initialize persistent storage paths: {ex.Message}");
    throw;
}

// 配置 Serilog
static int ReadRetainedFileCountLimit(IConfiguration configuration)
{
    var raw = (configuration["Serilog:RetainedFileCountLimit"] ?? "").Trim();
    if (!int.TryParse(raw, out var v))
        return 30;
    if (v < 1) return 1;
    if (v > 3650) return 3650;
    return v;
}

static string ResolveSerilogFilePath(IConfiguration configuration, IWebHostEnvironment environment)
{
    var configured = (configuration["Serilog:FilePath"] ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(configured))
        return StoragePathResolver.ResolveRelativeToBase(configured, environment.ContentRootPath);

    var persistentRoot = StoragePathResolver.ResolvePersistentRoot(configuration);
    if (!string.IsNullOrWhiteSpace(persistentRoot))
        return Path.Combine(persistentRoot, "logs", "telegram-panel-.log");

    return Path.Combine("logs", "telegram-panel-.log");
}

var retainedFileCountLimit = ReadRetainedFileCountLimit(builder.Configuration);
var serilogEnabled = builder.Configuration.GetValue("Serilog:Enabled", false);
var serilogFilePath = ResolveSerilogFilePath(builder.Configuration, builder.Environment);

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext();

if (serilogEnabled)
{
    loggerConfig = loggerConfig
        .ReadFrom.Configuration(builder.Configuration)
        // buffered=true：降低磁盘抖动/IO 阻塞导致的请求卡顿风险（尤其是低配 VPS/挂载卷/杀软扫描场景）
        .WriteTo.File(
            serilogFilePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: retainedFileCountLimit,
            buffered: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1));
}
else
{
    // 关闭“详细日志输出”时，仍保留 Error/Fatal：避免进程异常退出却完全看不到日志。
    loggerConfig = loggerConfig.MinimumLevel.Error();
}

loggerConfig = loggerConfig.WriteTo.Console();
Log.Logger = loggerConfig.CreateLogger();

builder.Host.UseSerilog();

// 兜底：即使日志配置被关闭/过滤，也尽量把致命异常打到 stderr
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        if (e.ExceptionObject is Exception ex)
            Log.Fatal(ex, "Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);
        else
            Log.Fatal("Unhandled exception: {ExceptionObject} (IsTerminating={IsTerminating})", e.ExceptionObject, e.IsTerminating);
    }
    catch
    {
        // ignore
    }
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    try
    {
        Log.Error(e.Exception, "Unobserved task exception");
    }
    catch
    {
        // ignore
    }
    finally
    {
        e.SetObserved();
    }
};

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = true;
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(5);
        options.DisconnectedCircuitMaxRetained = 200;
    });

// MudBlazor
builder.Services.AddMudServices();
builder.Services.AddMemoryCache();

// DataProtection keys 持久化：避免容器重建/重启后出现 antiforgery token 无法解密
try
{
    var configuredKeysPath = (builder.Configuration["DataProtection:KeysPath"] ?? "").Trim();
    var keysPath = configuredKeysPath;
    if (string.IsNullOrWhiteSpace(keysPath))
    {
        keysPath = Path.Combine(
            StoragePathResolver.ResolveWritableRoot(builder.Configuration, builder.Environment),
            Directory.Exists("/data") ? "keys" : "data-protection-keys");
    }
    else
    {
        keysPath = StoragePathResolver.ResolveRelativeToBase(keysPath, builder.Environment.ContentRootPath);
    }

    Directory.CreateDirectory(keysPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to configure DataProtection keys persistence: {ex.Message}");
}

// 反向代理支持（宝塔/Nginx/Caddy 等）
// 让应用正确识别外部访问的 Host/Proto，避免重定向到 http://localhost/...
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                               | ForwardedHeaders.XForwardedProto
                               | ForwardedHeaders.XForwardedHost;

    // 适配宝塔等面板：上游代理 IP 不固定时不做白名单限制（由部署环境保证信任边界）。
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// 数据库上下文
// 持久化初始化失败会在上方终止启动，禁止回退到可能创建空库的临时路径。
var connectionString = persistentStorage.ConnectionString;

// 云端场景（容器/卷/后台任务）更容易出现 SQLite 写锁：这里统一增强连接参数，提升抗锁能力
try
{
    var csb = new SqliteConnectionStringBuilder(connectionString)
    {
        // 等待写锁释放的最大秒数（映射/等价于 busy_timeout 行为）
        DefaultTimeout = 30,
        Pooling = true
    };
    connectionString = csb.ToString();
}
catch (Exception ex)
{
    Log.Warning(ex, "Failed to enhance sqlite connection string, using current value");
}
builder.Services.AddTelegramPanelData(connectionString);

// Telegram Panel 核心服务
builder.Services.AddTelegramPanelCore();
builder.Services.AddSingleton<AccountLoginProxyStateStore>();
builder.Services.AddSingleton<IWarpProxyUsageGuard>(serviceProvider =>
    serviceProvider.GetRequiredService<AccountLoginProxyStateStore>());
builder.Services.AddScoped<AccountLoginProxyCoordinator>();
builder.Services.AddHostedService<AccountLoginProxyCleanupService>();
builder.Services.AddSingleton<WarpMaintenanceState>();
builder.Services.AddHostedService<WarpMaintenanceBackgroundService>();
builder.Services.AddHostedService<ProxyEgressMaintenanceBackgroundService>();
builder.Services.AddScoped<AccountExportService>();
builder.Services.AddScoped<DataSyncService>();
builder.Services.AddScoped<UiPreferencesService>();
builder.Services.AddScoped<BotAdminPresetsService>();
builder.Services.AddScoped<BotChannelAdminDefaultsService>();
builder.Services.AddScoped<ChannelAdminDefaultsService>();
builder.Services.AddScoped<ChannelAdminPresetsService>();
builder.Services.AddScoped<ChannelInvitePresetsService>();
builder.Services.AddSingleton<ImageAssetStorageService>();
builder.Services.AddSingleton<CronExpressionService>();
builder.Services.AddScoped<DataDictionaryService>();
builder.Services.AddScoped<TemplateRenderingService>();
builder.Services.AddScoped<ScheduledTaskService>();
builder.Services.Configure<UpdateCheckOptions>(builder.Configuration.GetSection("UpdateCheck"));
builder.Services.Configure<SelfUpdateOptions>(builder.Configuration.GetSection("SelfUpdate"));
builder.Services.Configure<AiOpenAiOptions>(builder.Configuration.GetSection("AI:OpenAI"));
builder.Services.AddSingleton<UpdateCheckService>();
builder.Services.AddSingleton<UpdateModeStore>();
builder.Services.AddSingleton<AppSelfUpdateService>();
builder.Services.Configure<PanelTimeZoneOptions>(builder.Configuration.GetSection("System"));
builder.Services.AddSingleton<PanelTimeZoneService>();
builder.Services.AddScoped<UserChatActiveAiVerificationService>();
builder.Services.AddSingleton<TelegramPanelAiService>();
builder.Services.AddSingleton<TelegramPanel.Modules.ITelegramPanelAiService>(sp => sp.GetRequiredService<TelegramPanelAiService>());
builder.Services.AddScoped<IModuleTaskHandler, BotChannelSetAdminsByAccountTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, BotSetAdminsTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, BotChannelInviteUsersTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, UserJoinSubscribeTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, UserChatActiveTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, ChannelInviteUsersTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, GroupInviteUsersTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, ChannelGroupPrivateCreateTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, ChannelGroupPublicizeTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, AutoChangeLoginEmailTaskHandler>();
builder.Services.AddScoped<IModuleTaskHandler, AccountAutoSyncTaskHandler>();
builder.Services.AddSingleton<ModuleTaskLifecycleService>();
builder.Services.AddSingleton<BatchTaskExecutionControlService>();
builder.Services.AddSingleton<BatchTaskStartupRecoveryService>();
builder.Services.AddHostedService<BatchTaskBackgroundService>();
builder.Services.AddHostedService<PersistentModuleTaskBackgroundService>();
builder.Services.AddHostedService<ScheduledTaskBackgroundService>();
builder.Services.AddHostedService<AccountDataAutoSyncBackgroundService>();
builder.Services.AddHostedService<AccountStatusAutoRefreshBackgroundService>();
builder.Services.AddHostedService<BotAutoSyncBackgroundService>();
builder.Services.AddHostedService<TelegramPanel.Web.Services.WebhookRegistrationService>();
builder.Services.AddHttpClient<TelegramBotApiClient>();
builder.Services.AddHttpClient<TelegramPanel.Web.Services.CloudMailClient>();
builder.Services.AddHttpClient(nameof(TelegramPanelAiService));
builder.Services.AddScoped<TelegramPanel.Modules.ITelegramEmailCodeService, TelegramPanel.Web.Services.TelegramEmailCodeService>();
builder.Services.AddModuleSystem(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<AppRestartService>();
builder.Services.Configure<BucketBackupOptions>(builder.Configuration.GetSection("BucketBackup"));
builder.Services.AddHttpClient(nameof(BucketBackupService));
builder.Services.AddSingleton<BucketBackupService>();

// 后台账号密码验证（Cookie 登录）
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));
builder.Services.AddSingleton<AdminCredentialStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "TelegramPanel.Auth";
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ReturnUrlParameter = "returnUrl";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);

        // 反向代理（宝塔默认反代）可能会把 Host 透传为 127.0.0.1/localhost，导致框架生成绝对跳转到 http://localhost/login
        // 这里强制使用“相对路径重定向”，不依赖 Host/Proto，就算反代没配 header 也能正常跳转。
        var loginPathValue = options.LoginPath.HasValue ? options.LoginPath.Value : "/login";
        var returnUrlParam = options.ReturnUrlParameter;
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx =>
            {
                var returnUrl = (ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString).ToString();
                var target = $"{loginPathValue}?{returnUrlParam}={Uri.EscapeDataString(returnUrl)}";
                ctx.Response.Redirect(target);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = ctx =>
            {
                var returnUrl = (ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString).ToString();
                var target = $"{loginPathValue}?{returnUrlParam}={Uri.EscapeDataString(returnUrl)}";
                ctx.Response.Redirect(target);
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

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

        ConfigureSqliteConnection(conn);

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
            // 迁移策略：
            // - 新库：直接 Migrate()
            // - 已有迁移历史：直接 Migrate()
            // - 已有表但无迁移历史：写入 baseline 到 __EFMigrationsHistory，再 Migrate()（避免永远无法升级）
            if (!hasAnyUserTables)
            {
                db.Database.Migrate();
            }
            else if (hasHistory)
            {
                db.Database.Migrate();
            }
            else
            {
                var baselinedMigrations = LegacyDatabaseMigrationBaseline.Apply(conn, migrations);
                Log.Warning(
                    "Database has schema tables but no __EFMigrationsHistory; reconstructed history through {MigrationId} ({Count} migrations)",
                    baselinedMigrations[^1],
                    baselinedMigrations.Count);
                db.Database.Migrate();
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

        void ConfigureSqliteConnection(System.Data.Common.DbConnection connection)
        {
            // journal_mode=WAL 会持久化到库；busy_timeout 是连接级参数
            // 这里提前设置，减少在云端并发写入时的 “database is locked” 概率
            try
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA journal_mode=WAL;";
                    _ = cmd.ExecuteScalar();
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA synchronous=NORMAL;";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA busy_timeout=5000;";
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to configure sqlite pragmas");
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

PersistentStorageBootstrapper.CompleteDatabaseMigration(
    persistentStorage,
    message => Log.Information("[Storage] {Message}", message));

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseForwardedHeaders();

// 仅在存在 HTTPS 端口/端点时启用重定向；否则会产生 “Failed to determine the https port for redirect.” 噪声
var httpsPort = app.Configuration["ASPNETCORE_HTTPS_PORT"];
var urls = app.Configuration["ASPNETCORE_URLS"] ?? "";
var hasHttpsEndpoint = !string.IsNullOrWhiteSpace(httpsPort)
                      || urls.Contains("https://", StringComparison.OrdinalIgnoreCase)
                      || !string.IsNullOrWhiteSpace(app.Configuration["Kestrel:Endpoints:Https:Url"]);
if (hasHttpsEndpoint)
    app.UseHttpsRedirection();

// 静态文件（包括 MudBlazor 等 NuGet 包的静态 Web 资源）
app.UseStaticFiles();
var spaRoot = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "panel-spa");
if (Directory.Exists(spaRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(spaRoot),
        RequestPath = "/ui",
        OnPrepareResponse = context =>
        {
            var fileName = context.File.Name;
            var requestPath = context.Context.Request.Path.Value ?? string.Empty;
            var headers = context.Context.Response.Headers;

            if (string.Equals(fileName, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                headers.CacheControl = "no-cache, no-store, must-revalidate";
                headers.Pragma = "no-cache";
                headers.Expires = "0";
                return;
            }

            if (requestPath.StartsWith("/ui/assets/", StringComparison.OrdinalIgnoreCase))
                headers.CacheControl = "public, max-age=31536000, immutable";
        }
    });
}
var persistentUploadsRoot = StoragePathResolver.ResolvePersistentRoot(app.Configuration);
if (!string.IsNullOrWhiteSpace(persistentUploadsRoot))
{
    var uploadsRoot = Path.Combine(persistentUploadsRoot, "uploads");
    Directory.CreateDirectory(uploadsRoot);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(uploadsRoot),
        RequestPath = "/uploads"
    });
}

var legacyUiRedirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["/"] = "/ui/dashboard",
    ["/dashboard"] = "/ui/dashboard",
    ["/admin/password"] = "/ui/admin/password",
    ["/tasks"] = "/ui/tasks",
    ["/accounts"] = "/ui/accounts",
    ["/accounts/import"] = "/ui/accounts/import",
    ["/accounts/login"] = "/ui/accounts/login",
    ["/accounts/categories"] = "/ui/accounts/categories",
    ["/channels"] = "/ui/channels",
    ["/channels/create"] = "/ui/channels/create",
    ["/channels/groups"] = "/ui/channels/groups",
    ["/groups"] = "/ui/groups",
    ["/groups/create"] = "/ui/groups/create",
    ["/groups/categories"] = "/ui/groups/categories",
    ["/bots"] = "/ui/bots",
    ["/bots/channels"] = "/ui/bots/channels",
    ["/data-dictionaries"] = "/ui/data-dictionaries",
    ["/dictionaries"] = "/ui/data-dictionaries",
    ["/modules"] = "/ui/modules",
    ["/apis"] = "/ui/apis",
    ["/settings"] = "/ui/settings"
};
var redirectLegacyToVue = string.Equals(
    app.Configuration["PanelSpa:RedirectLegacy"] ?? "true",
    "true",
    StringComparison.OrdinalIgnoreCase);
var moduleContributions = app.Services.GetRequiredService<ModuleContributionRegistry>();

// 已复刻的后台页面默认交给 Vue；后台模块页面也默认进入 Vue 宿主。
// 只有显式 legacy=1 才打开旧 Razor/Blazor 兼容页，公开模块端点仍由模块自己处理。
app.Use(async (context, next) =>
{
    var requestPath = context.Request.Path.Value ?? string.Empty;
    var normalizedRequestPath = requestPath.Length > 1 ? requestPath.TrimEnd('/') : requestPath;
    var hasLegacyTarget = legacyUiRedirects.TryGetValue(normalizedRequestPath, out var legacyTarget);
    var vueTarget = hasLegacyTarget ? legacyTarget : null;
    if (vueTarget == null
        && TryResolveExtensionModuleVueRoute(normalizedRequestPath, moduleContributions, out var extensionVueTarget))
    {
        vueTarget = extensionVueTarget;
    }

    if (redirectLegacyToVue
        && Directory.Exists(spaRoot)
        && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && !context.Request.Query.ContainsKey("legacy")
        && vueTarget != null
        && !string.Equals(normalizedRequestPath, vueTarget, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect(vueTarget + context.Request.QueryString, permanent: false);
        return;
    }

    await next();
});

static bool TryResolveExtensionModuleVueRoute(
    string normalizedRequestPath,
    ModuleContributionRegistry contributions,
    out string vueTarget)
{
    vueTarget = string.Empty;
    if (!ExtensionModuleRoute.TryParse(normalizedRequestPath, out var moduleId, out var pageKey))
        return false;

    var matched = contributions.Pages.Any(page =>
        string.Equals(page.Module.Id, moduleId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(page.Definition.Key, pageKey, StringComparison.OrdinalIgnoreCase))
        || contributions.NavItems.Any(item =>
            string.Equals(item.Module.Id, moduleId, StringComparison.OrdinalIgnoreCase)
            && ExtensionModuleRoute.Matches(item.Definition.Href, moduleId, pageKey));
    if (!matched)
        return false;

    vueTarget = ExtensionModuleRoute.BuildVuePath(moduleId, pageKey);
    return true;
}

app.UseRouting();

app.UseAntiforgery();

// Serilog 请求日志
if (serilogEnabled)
    app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Json(new
{
    status = "ok",
    at = DateTimeOffset.UtcNow
})).AllowAnonymous();

// Modules (built-in & installed): endpoints mapping
app.MapInstalledModules();

// 初始化后台登录凭据（首次启动会生成 admin_auth.json）
var adminCredentials = app.Services.GetRequiredService<AdminCredentialStore>();
await adminCredentials.EnsureInitializedAsync();

// 登录页（独立 endpoint，避免与 RequireAuthorization 的 Razor Components 冲突）
app.MapGet("/login", async (HttpContext http, IConfiguration configuration, AdminCredentialStore credentialStore) =>
{
    var enabled = credentialStore.Enabled;
    var configured = enabled;

    if (configured && http.User.Identity?.IsAuthenticated == true)
        return Results.Redirect("/");

    var q = http.Request.Query;
    var error = q.TryGetValue("error", out var e) ? e.ToString() : "";
    var returnUrl = q.TryGetValue("returnUrl", out var r) ? r.ToString() : "";
    if (string.IsNullOrWhiteSpace(returnUrl))
        returnUrl = q.TryGetValue("ReturnUrl", out var r2) ? r2.ToString() : "/";
    if (!AdminAuthHelpers.IsLocalReturnUrl(returnUrl))
        returnUrl = "/";

    var title = "Telegram Panel 登录";
    var msg = error == "1" ? "<div class=\"mud-alert mud-alert-filled mud-alert-filled-error\" style=\"margin-bottom:12px;\">账号或密码错误</div>" : "";
    var disabledMsg = configured ? "" : "<div class=\"mud-alert mud-alert-filled mud-alert-filled-warning\" style=\"margin-bottom:12px;\">后台验证未启用</div>";
    var initialUsername = System.Net.WebUtility.HtmlEncode((configuration["AdminAuth:InitialUsername"] ?? "tgpanel").Trim());
    var initialPassword = System.Net.WebUtility.HtmlEncode((configuration["AdminAuth:InitialPassword"] ?? "tgpanel123").Trim());
    var initialHint = configured && credentialStore.MustChangePassword
        ? $"<div class=\"mud-alert mud-alert-filled mud-alert-filled-info\" style=\"margin-bottom:12px;\">初始账号：<b>{initialUsername}</b>，初始密码：<b>{initialPassword}</b>（首次登录后请立即修改）</div>"
        : "";

    var html = $@"
<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
  <meta charset=""utf-8"" />
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
  <title>{title}</title>
  <link href=""_content/MudBlazor/MudBlazor.min.css"" rel=""stylesheet"" />
  <style>
    body {{ background:#121212; color:#fff; font-family:Roboto,Arial; }}
    .wrap {{ max-width:420px; margin:10vh auto; padding:24px; background:#1e1e2d; border-radius:12px; }}
    .field {{ width:100%; padding:12px 14px; border-radius:8px; border:1px solid rgba(255,255,255,0.12); background:rgba(255,255,255,0.06); color:#fff; }}
    .label {{ font-size:12px; opacity:0.8; margin:10px 0 6px; }}
    .btn {{ width:100%; margin-top:14px; padding:10px 14px; border-radius:10px; border:0; background:#1976d2; color:#fff; font-weight:600; cursor:pointer; }}
    .btn:disabled {{ opacity:0.5; cursor:not-allowed; }}
  </style>
</head>
<body>
  <div class=""wrap"">
    <h2 style=""margin:0 0 8px;"">Telegram Panel</h2>
    <div style=""opacity:0.8; margin-bottom:16px;"">后台登录</div>
    {disabledMsg}
    {initialHint}
    {msg}
    <form method=""post"" action=""/login"">
      <input type=""hidden"" name=""returnUrl"" value=""{System.Net.WebUtility.HtmlEncode(returnUrl)}"" />
      <div class=""label"">账号</div>
      <input class=""field"" name=""username"" autocomplete=""username"" />
      <div class=""label"">密码</div>
      <input class=""field"" type=""password"" name=""password"" autocomplete=""current-password"" />
      <button class=""btn"" type=""submit"" {(configured ? "" : "disabled")}>登录</button>
    </form>
  </div>
</body>
</html>";

    return Results.Content(html, "text/html; charset=utf-8");
}).AllowAnonymous();

app.MapPost("/login", async (HttpContext http, AdminCredentialStore credentialStore) =>
{
    if (!credentialStore.Enabled)
        return Results.Redirect("/login");

    var form = await http.Request.ReadFormAsync();
    var u = (form["username"].ToString() ?? "").Trim();
    var p = (form["password"].ToString() ?? "").Trim();
    var returnUrl = (form["returnUrl"].ToString() ?? "/").Trim();
    if (!AdminAuthHelpers.IsLocalReturnUrl(returnUrl))
        returnUrl = "/";

    var ok = await credentialStore.ValidateAsync(u, p);
    if (!ok)
        return Results.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl)}");

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, u),
        new(ClaimTypes.Role, "Admin")
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

    if (credentialStore.MustChangePassword)
        return Results.Redirect($"/admin/password?returnUrl={Uri.EscapeDataString(returnUrl)}");

    return Results.Redirect(returnUrl);
}).DisableAntiforgery().AllowAnonymous();

app.MapGet("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).AllowAnonymous();

var adminAuthEnabled = adminCredentials.Enabled;

app.MapPanelAdminApi(adminAuthEnabled);

if (Directory.Exists(spaRoot))
{
    app.MapMethods("/ui/assets/{**path}", new[] { HttpMethods.Get, HttpMethods.Head }, (HttpContext http) =>
    {
        http.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers.Expires = "0";
        return Results.NotFound();
    }).AllowAnonymous();

    app.MapMethods("/ui/{**path}", new[] { HttpMethods.Get, HttpMethods.Head }, async (HttpContext http) =>
    {
        var indexPath = Path.Combine(spaRoot, "index.html");
        if (!File.Exists(indexPath))
            return Results.NotFound();

        http.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers.Expires = "0";
        var html = await File.ReadAllTextAsync(indexPath, http.RequestAborted);
        return Results.Content(html, "text/html; charset=utf-8");
    }).AllowAnonymous();
}

var razor = app.MapRazorComponents<TelegramPanel.Web.Components.App>()
    .AddInteractiveServerRenderMode();
if (adminAuthEnabled)
    razor.RequireAuthorization();

// 下载：导出账号 Zip（用于备份/迁移）
var accountsZipDownload = app.MapGet("/downloads/accounts.zip", async (
    HttpContext http,
    AccountManagementService accountManagement,
    AccountExportService exporter,
    CancellationToken cancellationToken) =>
{
    var idsRaw = http.Request.Query["ids"].ToString();
    HashSet<int>? ids = null;
    if (!string.IsNullOrWhiteSpace(idsRaw))
    {
        ids = idsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var x) ? x : 0)
            .Where(x => x > 0)
            .ToHashSet();
    }

    var all = (await accountManagement.GetAllAccountsAsync()).ToList();
    var accounts = ids == null ? all : all.Where(a => ids.Contains(a.Id)).ToList();

    var formatRaw = (http.Request.Query["format"].ToString() ?? string.Empty).Trim();
    var format = string.Equals(formatRaw, "tdata", StringComparison.OrdinalIgnoreCase)
        ? AccountExportFormat.Tdata
        : AccountExportFormat.Telethon;

    var zipBytes = await exporter.BuildAccountsZipAsync(accounts, cancellationToken, format);
    var formatName = format == AccountExportFormat.Tdata ? "tdata" : "telethon";
    var fileName = $"telegram-panel-accounts-{formatName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";

    // 下载文件禁止缓存，避免浏览器复用旧的 accounts.zip 导致“重复拿到旧导出包”。
    http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
    http.Response.Headers.Pragma = "no-cache";
    http.Response.Headers.Expires = "0";
    return Results.File(zipBytes, "application/zip", fileName);
}).DisableAntiforgery();
if (adminAuthEnabled)
    accountsZipDownload.RequireAuthorization();

// 下载：导出 Bot 邀请链接（文本）
var botInvitesDownload = app.MapGet("/downloads/bots/{botId:int}/invites.txt", async (
    HttpContext http,
    int botId,
    BotManagementService botManagement,
    BotTelegramService botTelegram,
    CancellationToken cancellationToken) =>
{
    var bot = await botManagement.GetBotAsync(botId)
        ?? throw new InvalidOperationException($"机器人不存在：{botId}");

    var idsRaw = http.Request.Query["ids"].ToString();
    IReadOnlyList<long> telegramIds;
    if (string.IsNullOrWhiteSpace(idsRaw))
    {
        telegramIds = (await botManagement.GetChannelsAsync(botId)).Select(x => x.TelegramId).ToList();
    }
    else
    {
        telegramIds = idsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var x) ? x : 0)
            .Where(x => x > 0)
            .Distinct()
            .ToList();
    }

    var links = await botTelegram.ExportInviteLinksAsync(botId, telegramIds, cancellationToken);

    var lines = new List<string>
    {
        $"# Bot: {bot.Name}",
        $"# ExportedAtUtc: {DateTime.UtcNow:O}",
        "# Format: <TelegramId>\\t<Link>",
        ""
    };

    foreach (var id in telegramIds)
    {
        if (links.TryGetValue(id, out var link))
            lines.Add($"{id}\t{link}");
        else
            lines.Add($"{id}\t(无法生成/不可见/无权限)");
    }

    var text = string.Join(Environment.NewLine, lines);
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    var fileName = $"telegram-panel-bot-invites-{botId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
    return Results.File(bytes, "text/plain; charset=utf-8", fileName);
}).DisableAntiforgery();
if (adminAuthEnabled)
    botInvitesDownload.RequireAuthorization();

// 下载：导出频道邀请链接（文本）
var channelInvitesDownload = app.MapGet("/downloads/channels/invites.txt", async (
    HttpContext http,
    ChannelManagementService channelManagement,
    IChannelService channelService,
    CancellationToken cancellationToken) =>
{
    var idsRaw = http.Request.Query["ids"].ToString();
    IReadOnlyList<long> telegramIds;
    if (string.IsNullOrWhiteSpace(idsRaw))
    {
        telegramIds = (await channelManagement.GetAllChannelsAsync()).Select(x => x.TelegramId).Where(x => x > 0).Distinct().ToList();
    }
    else
    {
        telegramIds = idsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var x) ? x : 0)
            .Where(x => x > 0)
            .Distinct()
            .ToList();
    }

    var preferredAccountIdRaw = http.Request.Query["accountId"].ToString();
    var preferredAccountId = int.TryParse(preferredAccountIdRaw, out var x) ? x : 0;
    if (preferredAccountId <= 0)
        preferredAccountId = 0;

    var lines = new List<string>
    {
        $"# ExportedAtUtc: {DateTime.UtcNow:O}",
        "# Format: <TelegramId>\\t<Title>\\t<Link>",
        ""
    };

    foreach (var telegramId in telegramIds)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ch = await channelManagement.GetChannelByTelegramIdAsync(telegramId);
        if (ch == null)
        {
            lines.Add($"{telegramId}\t(unknown)\t(频道不存在)");
            continue;
        }

        var executeAccountId = await channelManagement.ResolveExecuteAccountIdAsync(ch, preferredAccountId: preferredAccountId);
        if (executeAccountId is not > 0)
        {
            lines.Add($"{telegramId}\t{ch.Title}\t(无可用执行账号)");
            continue;
        }

        try
        {
            var link = await channelService.ExportJoinLinkAsync(executeAccountId.Value, telegramId);
            lines.Add($"{telegramId}\t{ch.Title}\t{link}");
        }
        catch
        {
            lines.Add($"{telegramId}\t{ch.Title}\t(无法生成/不可见/无权限)");
        }
    }

    var text = string.Join(Environment.NewLine, lines);
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    var fileName = $"telegram-panel-channel-invites-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
    return Results.File(bytes, "text/plain; charset=utf-8", fileName);
}).DisableAntiforgery();
if (adminAuthEnabled)
    channelInvitesDownload.RequireAuthorization();

// 下载：导出群组加入链接（文本）
var groupInvitesDownload = app.MapGet("/downloads/groups/invites.txt", async (
    HttpContext http,
    GroupManagementService groupManagement,
    IGroupService groupService,
    CancellationToken cancellationToken) =>
{
    var idsRaw = http.Request.Query["ids"].ToString();
    IReadOnlyList<long> telegramIds;
    if (string.IsNullOrWhiteSpace(idsRaw))
    {
        telegramIds = (await groupManagement.GetAllGroupsAsync()).Select(x => x.TelegramId).Where(x => x > 0).Distinct().ToList();
    }
    else
    {
        telegramIds = idsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var x) ? x : 0)
            .Where(x => x > 0)
            .Distinct()
            .ToList();
    }

    var preferredAccountIdRaw = http.Request.Query["accountId"].ToString();
    var preferredAccountId = int.TryParse(preferredAccountIdRaw, out var x) ? x : 0;
    if (preferredAccountId <= 0)
        preferredAccountId = 0;

    var lines = new List<string>
    {
        $"# ExportedAtUtc: {DateTime.UtcNow:O}",
        "# Format: <TelegramId>\\t<Title>\\t<Link>",
        ""
    };

    foreach (var telegramId in telegramIds)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var group = await groupManagement.GetGroupByTelegramIdAsync(telegramId);
        if (group == null)
        {
            lines.Add($"{telegramId}\t(unknown)\t(群组不存在)");
            continue;
        }

        var executeAccountId = await groupManagement.ResolveExecuteAccountIdAsync(group, preferredAccountId: preferredAccountId);
        if (executeAccountId is not > 0)
        {
            lines.Add($"{telegramId}\t{group.Title}\t(无可用执行账号)");
            continue;
        }

        try
        {
            var link = await groupService.ExportJoinLinkAsync(executeAccountId.Value, telegramId);
            lines.Add($"{telegramId}\t{group.Title}\t{link}");
        }
        catch
        {
            lines.Add($"{telegramId}\t{group.Title}\t(无法生成/不可见/无权限)");
        }
    }

    var text = string.Join(Environment.NewLine, lines);
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    var fileName = $"telegram-panel-group-invites-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
    return Results.File(bytes, "text/plain; charset=utf-8", fileName);
}).DisableAntiforgery();
if (adminAuthEnabled)
    groupInvitesDownload.RequireAuthorization();

// Telegram Bot Webhook 端点
// 接收 Telegram 服务器推送的更新，用于 Webhook 模式
app.MapPost("/api/bot/webhook/{secretToken}", async (
    HttpContext http,
    string secretToken,
    BotUpdateHub updateHub,
    IConfiguration configuration,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    // 验证 secret token
    var configuredSecret = configuration["Telegram:WebhookSecretToken"];
    if (!string.IsNullOrWhiteSpace(configuredSecret))
    {
        // 检查 Telegram 发送的 X-Telegram-Bot-Api-Secret-Token header
        var headerSecret = http.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        if (!string.Equals(headerSecret, configuredSecret, StringComparison.Ordinal))
        {
            logger.LogWarning("Webhook request rejected: invalid secret token");
            return Results.Unauthorized();
        }
    }

    // 读取请求体
    using var reader = new System.IO.StreamReader(http.Request.Body);
    var body = await reader.ReadToEndAsync(cancellationToken);

    if (string.IsNullOrWhiteSpace(body))
    {
        logger.LogWarning("Webhook request rejected: empty body");
        return Results.BadRequest();
    }

    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var update = doc.RootElement;

        var botToken = await updateHub.ResolveBotTokenFromWebhookPathAsync(secretToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(botToken))
        {
            logger.LogWarning("Webhook update rejected: unknown or inactive bot");
            return Results.NotFound();
        }

        var sw = Stopwatch.StartNew();
        var success = await updateHub.InjectWebhookUpdateAsync(botToken, update.Clone(), cancellationToken);
        sw.Stop();

        if (!success)
        {
            logger.LogWarning("Webhook update rejected: unknown or inactive bot");
            return Results.NotFound();
        }

        var updateId = update.TryGetProperty("update_id", out var uid) ? uid.GetInt64() : 0;
        if (sw.Elapsed > TimeSpan.FromSeconds(2))
            logger.LogWarning("Webhook update processed slowly: update_id={UpdateId} elapsed_ms={ElapsedMs}", updateId, (long)sw.Elapsed.TotalMilliseconds);
        else
            logger.LogDebug("Webhook update processed: update_id={UpdateId} elapsed_ms={ElapsedMs}", updateId, (long)sw.Elapsed.TotalMilliseconds);

        return Results.Ok();
    }
    catch (System.Text.Json.JsonException ex)
    {
        logger.LogWarning(ex, "Webhook request rejected: invalid JSON");
        return Results.BadRequest();
    }
}).AllowAnonymous().DisableAntiforgery();

// TODO: Hangfire Dashboard
// app.MapHangfireDashboard("/hangfire");

try
{
    await app.StartAsync();

    // 只有主机与全部托管服务真正启动成功后，才确认自更新版本可用。
    SelfUpdateStartupCoordinator.TryConfirmSuccessfulStartup(
        AppContext.BaseDirectory,
        VersionService.Version,
        message => Log.Information("[SelfUpdate] {Message}", message));

    Log.Information("Telegram Panel started");
    await app.WaitForShutdownAsync();
}
finally
{
    try
    {
        await app.DisposeAsync();
    }
    finally
    {
        Log.CloseAndFlush();
    }
}

internal static class LegacyDatabaseMigrationBaseline
{
    internal static IReadOnlyList<string> Apply(
        DbConnection connection,
        IReadOnlyList<string> migrations)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);

        if (migrations.Count == 0)
            throw new InvalidOperationException("没有可用于识别旧数据库结构的 EF 迁移。");

        var actualSchema = SqliteSchemaSnapshot.Capture(connection);
        var matchedMigrationCount = DetectMatchingMigrationCount(actualSchema, migrations, out var latestSchema);
        if (matchedMigrationCount <= 0)
        {
            throw new InvalidOperationException(
                "现有数据库没有 __EFMigrationsHistory，且结构无法与任何已知迁移版本可靠匹配。"
                + "为避免重复建表或跳过必要迁移，已停止启动；请先备份并人工确认数据库版本。"
                + $"结构差异：{actualSchema.DescribeDifference(latestSchema)}");
        }

        var appliedMigrations = migrations.Take(matchedMigrationCount).ToArray();
        WriteHistory(connection, appliedMigrations);
        return appliedMigrations;
    }

    private static int DetectMatchingMigrationCount(
        SqliteSchemaSnapshot actualSchema,
        IReadOnlyList<string> migrations,
        out SqliteSchemaSnapshot latestSchema)
    {
        using var referenceConnection = new SqliteConnection("Data Source=:memory:");
        referenceConnection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(referenceConnection)
            .Options;
        using var referenceDb = new AppDbContext(options);
        var migrator = referenceDb.GetService<IMigrator>();

        var matchedMigrationCount = 0;
        latestSchema = SqliteSchemaSnapshot.Empty;
        for (var i = 0; i < migrations.Count; i++)
        {
            migrator.Migrate(migrations[i]);
            latestSchema = SqliteSchemaSnapshot.Capture(referenceConnection);
            if (actualSchema.Equals(latestSchema))
                matchedMigrationCount = i + 1;
        }

        return matchedMigrationCount;
    }

    private static void WriteHistory(DbConnection connection, IReadOnlyList<string> migrations)
    {
        var productVersion = typeof(DbContext)
            .Assembly
            .GetName()
            .Version?
            .ToString(3) ?? "8.0.0";

        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE __EFMigrationsHistory (
                    MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY,
                    ProductVersion TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        foreach (var migration in migrations)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ($id, $version);
                """;

            var idParameter = insert.CreateParameter();
            idParameter.ParameterName = "$id";
            idParameter.Value = migration;
            insert.Parameters.Add(idParameter);

            var versionParameter = insert.CreateParameter();
            versionParameter.ParameterName = "$version";
            versionParameter.Value = productVersion;
            insert.Parameters.Add(versionParameter);

            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private sealed class SqliteSchemaSnapshot : IEquatable<SqliteSchemaSnapshot>
    {
        private readonly IReadOnlySet<string> entries;
        private readonly string fingerprint;

        internal static SqliteSchemaSnapshot Empty { get; } = new(Array.Empty<string>());

        private SqliteSchemaSnapshot(IEnumerable<string> entries)
        {
            this.entries = entries.ToHashSet(StringComparer.Ordinal);
            fingerprint = string.Join('\n', this.entries.Order(StringComparer.Ordinal));
        }

        internal static SqliteSchemaSnapshot Capture(DbConnection connection)
        {
            var entries = new List<string>();
            var tables = ReadTableNames(connection);

            foreach (var table in tables)
            {
                entries.Add($"table|{table}");
                CaptureColumns(connection, table, entries);
                CaptureIndexes(connection, table, entries);
                CaptureForeignKeys(connection, table, entries);
            }

            return new SqliteSchemaSnapshot(entries);
        }

        public bool Equals(SqliteSchemaSnapshot? other)
        {
            return other != null
                && string.Equals(fingerprint, other.fingerprint, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => Equals(obj as SqliteSchemaSnapshot);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(fingerprint);

        internal string DescribeDifference(SqliteSchemaSnapshot expected)
        {
            var missing = expected.entries.Except(entries, StringComparer.Ordinal).Take(5).ToArray();
            var extra = entries.Except(expected.entries, StringComparer.Ordinal).Take(5).ToArray();
            return $"缺少 [{string.Join("；", missing)}]；额外 [{string.Join("；", extra)}]";
        }

        private static IReadOnlyList<string> ReadTableNames(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table'
                  AND name <> '__EFMigrationsHistory'
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY name COLLATE BINARY;
                """;

            using var reader = command.ExecuteReader();
            var tables = new List<string>();
            while (reader.Read())
                tables.Add(reader.GetString(0));
            return tables;
        }

        private static void CaptureColumns(DbConnection connection, string table, ICollection<string> entries)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_xinfo({ToSqliteLiteral(table)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim().ToUpperInvariant();
                var notNull = reader.GetInt32(3);
                var primaryKeyOrder = reader.GetInt32(5);
                var hidden = reader.FieldCount > 6 ? reader.GetInt32(6) : 0;
                // EnsureCreated 与 AddColumn 对同一最终列可能生成不同的数据库默认值；
                // 默认值差异不代表该列可以安全地再次执行 AddColumn。
                entries.Add($"column|{table}|{name}|{type}|{notNull}|{primaryKeyOrder}|{hidden}");
            }
        }

        private static void CaptureIndexes(DbConnection connection, string table, ICollection<string> entries)
        {
            var indexes = new List<(string Name, int Unique, string Origin, int Partial)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA index_list({ToSqliteLiteral(table)});";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(1);
                    if (name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal))
                        continue;

                    indexes.Add((
                        name,
                        reader.GetInt32(2),
                        reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        reader.FieldCount > 4 ? reader.GetInt32(4) : 0));
                }
            }

            foreach (var index in indexes.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var columns = new List<string>();
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA index_info({ToSqliteLiteral(index.Name)});";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    columns.Add(reader.IsDBNull(2) ? $"#{reader.GetInt32(1)}" : reader.GetString(2));

                entries.Add(
                    $"index|{table}|{index.Name}|{index.Unique}|{index.Origin}|{index.Partial}|{string.Join(',', columns)}");
            }
        }

        private static void CaptureForeignKeys(DbConnection connection, string table, ICollection<string> entries)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_key_list({ToSqliteLiteral(table)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(
                    $"foreign-key|{table}|{reader.GetString(2)}|{reader.GetString(3)}|{reader.GetString(4)}|"
                    + $"{reader.GetString(5)}|{reader.GetString(6)}|{reader.GetString(7)}");
            }
        }

        private static string ToSqliteLiteral(string value) => $"'{value.Replace("'", "''")}'";
    }
}
