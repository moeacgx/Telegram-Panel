using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Claims;
using Serilog;
using TelegramPanel.Core;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data;
using TelegramPanel.Web.Modules;
using TelegramPanel.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;

// 诊断：对某个目录下的 *.json/*.session 做一次“可转换/可校验”检查（不写数据库）
// 用法：dotnet run --project src/TelegramPanel.Web -- --diag-session-dir "D:/path/to/dir"
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
                Console.WriteLine($"[FAIL] {Path.GetFileName(jsonPath)}: 缺少 phone");
                continue;
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
                        logger: logger);
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
                        logger: logger);
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
builder.Services.AddMemoryCache();

// DataProtection keys 持久化：避免容器重建/重启后出现 antiforgery token 无法解密
try
{
    var configuredKeysPath = (builder.Configuration["DataProtection:KeysPath"] ?? "").Trim();
    var keysPath = configuredKeysPath;
    if (string.IsNullOrWhiteSpace(keysPath))
    {
        keysPath = Directory.Exists("/data")
            ? "/data/keys"
            : Path.Combine(builder.Environment.ContentRootPath, "data-protection-keys");
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
builder.Services.AddScoped<AccountExportService>();
builder.Services.AddScoped<DataSyncService>();
builder.Services.AddScoped<UiPreferencesService>();
builder.Services.AddScoped<BotAdminPresetsService>();
builder.Services.Configure<UpdateCheckOptions>(builder.Configuration.GetSection("UpdateCheck"));
builder.Services.AddSingleton<UpdateCheckService>();
builder.Services.Configure<PanelTimeZoneOptions>(builder.Configuration.GetSection("System"));
builder.Services.AddSingleton<PanelTimeZoneService>();
builder.Services.AddHostedService<BatchTaskBackgroundService>();
builder.Services.AddHostedService<AccountDataAutoSyncBackgroundService>();
builder.Services.AddHostedService<BotAutoSyncBackgroundService>();
builder.Services.AddHttpClient<TelegramBotApiClient>();
builder.Services.AddModuleSystem(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<AppRestartService>();

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
                var baseline = migrations.First(); // EF 返回的顺序为从旧到新
                EnsureMigrationsHistoryBaseline(conn, baseline);
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

        void EnsureMigrationsHistoryBaseline(System.Data.Common.DbConnection connection, string baselineMigrationId)
        {
            try
            {
                // 创建历史表（若不存在）
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
    MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY,
    ProductVersion TEXT NOT NULL
);";
                    cmd.ExecuteNonQuery();
                }

                // 若 baseline 已存在则跳过
                using (var check = connection.CreateCommand())
                {
                    check.CommandText = "SELECT COUNT(1) FROM __EFMigrationsHistory WHERE MigrationId = $id;";
                    var p = check.CreateParameter();
                    p.ParameterName = "$id";
                    p.Value = baselineMigrationId;
                    check.Parameters.Add(p);
                    var exists = Convert.ToInt32(check.ExecuteScalar()) > 0;
                    if (exists)
                        return;
                }

                var productVersion = typeof(Microsoft.EntityFrameworkCore.DbContext)
                    .Assembly
                    .GetName()
                    .Version?
                    .ToString(3) ?? "8.0.0";

                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($id, $v);";
                    var p1 = insert.CreateParameter();
                    p1.ParameterName = "$id";
                    p1.Value = baselineMigrationId;
                    insert.Parameters.Add(p1);

                    var p2 = insert.CreateParameter();
                    p2.ParameterName = "$v";
                    p2.Value = productVersion;
                    insert.Parameters.Add(p2);

                    insert.ExecuteNonQuery();
                }

                Log.Warning("Database has schema tables but no __EFMigrationsHistory; baselined migrations history with {MigrationId}", baselineMigrationId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to baseline __EFMigrationsHistory; database might not auto-upgrade");
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

app.UseAntiforgery();

// Serilog 请求日志
app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

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
    var initialUsername = System.Net.WebUtility.HtmlEncode((configuration["AdminAuth:InitialUsername"] ?? "admin").Trim());
    var initialPassword = System.Net.WebUtility.HtmlEncode((configuration["AdminAuth:InitialPassword"] ?? "admin123").Trim());
    var initialHint = configured
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

    var zipBytes = await exporter.BuildAccountsZipAsync(accounts, cancellationToken);
    var fileName = $"telegram-panel-accounts-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
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

// TODO: Hangfire Dashboard
// app.MapHangfireDashboard("/hangfire");

Log.Information("Telegram Panel started");

app.Run();
