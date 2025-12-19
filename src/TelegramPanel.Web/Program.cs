using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using Serilog;
using TelegramPanel.Core;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data;
using TelegramPanel.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// 可选的本地覆盖配置（不要提交到仓库）
// Docker 部署时该文件通常是指向 /data/appsettings.local.json 的符号链接；首次启动可能是“悬空链接”，
// 直接加载会抛 FileNotFoundException 导致容器不断重启，因此这里先确保文件存在一个空 JSON。
try
{
    var localOverridePath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.local.json");
    if (!File.Exists(localOverridePath))
        File.WriteAllText(localOverridePath, "{}", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to ensure appsettings.local.json exists: {ex.Message}");
}
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
builder.Services.AddScoped<AccountExportService>();
builder.Services.AddScoped<DataSyncService>();
builder.Services.AddHostedService<BatchTaskBackgroundService>();
builder.Services.AddHostedService<AccountDataAutoSyncBackgroundService>();
builder.Services.AddHostedService<BotAutoSyncBackgroundService>();
builder.Services.AddHttpClient<TelegramBotApiClient>();

// 后台账号密码验证（Cookie 登录）
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));
builder.Services.AddSingleton<AdminCredentialStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "TelegramPanel.Auth";
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
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

app.UseHttpsRedirection();

// 静态文件（包括 MudBlazor 等 NuGet 包的静态 Web 资源）
app.UseStaticFiles();

app.UseAntiforgery();

// Serilog 请求日志
app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

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
    var returnUrl = q.TryGetValue("returnUrl", out var r) ? r.ToString() : "/";
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
