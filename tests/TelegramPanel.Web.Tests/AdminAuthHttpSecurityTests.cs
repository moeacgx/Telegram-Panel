using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class AdminAuthHttpSecurityTests
{
    private const string InitialPassword = "Initial-password-2026!";
    private const string NewPassword = "Changed-password-2026!";

    private const string SpaIndex = "<!doctype html><html><body><div id=app></div></body></html>";
    private const string SpaScript = "window.panelReady = true;";
    private const string SpaStyle = "body { color: black; }";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Spa_ExistingAssets_AreServedBeforeFallback(bool authenticated, bool head)
    {
        await using var host = await SecurityHost.StartAsync();
        using var client = host.CreateClient();
        if (authenticated)
        {
            using var login = await LoginAsync(client, form: false, await GetTokenAsync(client));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.True(host.Store.MustChangePassword);
        }

        foreach (var (path, expectedBody, mediaType) in new[]
        {
            ("/ui/assets/index-test.js", SpaScript, "text/javascript"),
            ("/ui/assets/index-test.css", SpaStyle, "text/css")
        })
        {
            using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(head ? "" : expectedBody, await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl?.Public);
            Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
            Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Spa_MissingAssets_NeverReturnEntryHtml(bool authenticated, bool head)
    {
        await using var host = await SecurityHost.StartAsync();
        using var client = host.CreateClient();
        if (authenticated)
        {
            using var login = await LoginAsync(client, form: false, await GetTokenAsync(client));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, "/ui/assets/missing.js");
        using var response = await client.SendAsync(request);
        // 初始密码会话的 HEAD 保持安全中间件原有拒绝合同。
        Assert.Equal(authenticated && head ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<html>", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spa_EntryHtml_IsAvailableAndNeverCached(bool authenticated)
    {
        await using var host = await SecurityHost.StartAsync();
        using var client = host.CreateClient();
        if (authenticated)
        {
            using var login = await LoginAsync(client, form: false, await GetTokenAsync(client));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        foreach (var path in new[] { "/ui/dashboard", "/ui/admin/password", "/ui/index.html" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(SpaIndex, await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl?.NoCache);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.True(response.Headers.CacheControl?.MustRevalidate);
            Assert.Contains(response.Headers.Pragma, value => value.Name == "no-cache");
        }
    }

    [Fact]
    public async Task InitialSession_UploadsStillPassThroughPasswordGuard()
    {
        await using var host = await SecurityHost.StartAsync();
        using var client = host.CreateClient();
        using var anonymous = await client.GetAsync("/uploads/test.txt");
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        using var login = await LoginAsync(client, form: false, await GetTokenAsync(client));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var blocked = await client.GetAsync("/uploads/test.txt");
        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        Assert.Equal("/ui/admin/password", blocked.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_RequiresCookieBoundToken_AndWorksOnDesktopHttp(bool form)
    {
        await using var host = await SecurityHost.StartAsync();
        using var client = host.CreateClient();
        using var missing = await LoginAsync(client, form, token: null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("INVALID_CSRF_TOKEN", await missing.Content.ReadAsStringAsync());

        using var otherClient = host.CreateClient();
        var foreignToken = await GetTokenAsync(otherClient);
        using var foreign = await LoginAsync(client, form, foreignToken);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        var token = await GetTokenAsync(client);
        using var accepted = await LoginAsync(client, form, token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var me = await client.GetAsync("/api/panel/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.True(body.RootElement.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal("http", client.BaseAddress!.Scheme);
    }

    [Fact]
    public async Task Login_AnonymousTokenCannotChangePassword_AfterSigningIn()
    {
        await using var host = await SecurityHost.StartAsync();
        using var client = host.CreateClient();
        var anonymousToken = await GetTokenAsync(client);
        using var login = await LoginAsync(client, form: false, anonymousToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var rejected = await ChangePasswordAsync(client, anonymousToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.True(host.Store.MustChangePassword);

        var authenticatedToken = await GetTokenAsync(client);
        using var accepted = await ChangePasswordAsync(client, authenticatedToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.False(host.Store.MustChangePassword);
        using var business = await client.GetAsync("/api/panel/summary");
        Assert.Equal(HttpStatusCode.OK, business.StatusCode);
    }

    [Fact]
    public async Task Login_FormAndJsonShareOneRateLimit()
    {
        await using var host = await SecurityHost.StartAsync();
        using var client = host.CreateClient();
        var token = await GetTokenAsync(client);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await LoginAsync(client, attempt % 2 == 0, token, "incorrect-password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var blocked = await LoginAsync(client, form: false, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Contains("LOGIN_RATE_LIMITED", await blocked.Content.ReadAsStringAsync());
        Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.True(blocked.Headers.RetryAfter?.Delta <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task InitialSession_CannotReadBusinessApi_OpenBlazor_OrDownload()
    {
        await using var host = await SecurityHost.StartAsync();
        using var client = host.CreateClient();
        using var login = await LoginAsync(client, form: false, await GetTokenAsync(client));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        foreach (var path in new[] { "/api/panel/summary", "/API/PANEL/SUMMARY/", "/_blazor" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("PASSWORD_CHANGE_REQUIRED", await response.Content.ReadAsStringAsync());
        }

        using var download = await client.GetAsync("/downloads/accounts.zip");
        Assert.Equal(HttpStatusCode.Redirect, download.StatusCode);
        Assert.Equal("/ui/admin/password", download.Headers.Location?.OriginalString);
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task PasswordChange_RevokesOtherSession_AndRefreshesCurrentSession()
    {
        await using var host = await SecurityHost.StartAsync();
        using var current = host.CreateClient();
        using var other = host.CreateClient();
        using var firstLogin = await LoginAsync(current, form: false, await GetTokenAsync(current));
        using var secondLogin = await LoginAsync(other, form: true, await GetTokenAsync(other));
        Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);

        using var changed = await ChangePasswordAsync(current, await GetTokenAsync(current));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var currentResponse = await current.GetAsync("/api/panel/summary");
        Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        using var revoked = await other.GetAsync("/api/panel/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var otherMe = await other.GetAsync("/api/panel/auth/me");
        using var body = JsonDocument.Parse(await otherMe.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task LoginWithOldPassword_CannotAcquireStampRotatedByConcurrentPasswordChange()
    {
        await using var host = await SecurityHost.StartAsync();
        using var lateLogin = host.CreateClient();
        using var current = host.CreateClient();
        using var loginRequest = lateLogin.PostAsync("/test/late-login", null);
        await host.SnapshotCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var currentLogin = await LoginAsync(current, form: false, await GetTokenAsync(current));
        Assert.Equal(HttpStatusCode.OK, currentLogin.StatusCode);
        using var changed = await ChangePasswordAsync(current, await GetTokenAsync(current));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        host.ReleaseLateLogin.TrySetResult();
        using var staleLogin = await loginRequest;
        Assert.Equal(HttpStatusCode.OK, staleLogin.StatusCode);
        using var rejected = await lateLogin.GetAsync("/api/panel/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    private static async Task<string> GetTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/panel/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client, bool form, string? token, string password = InitialPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, form ? "/login" : "/api/panel/auth/login");
        if (form)
        {
            var fields = new Dictionary<string, string> { ["username"] = "tgpanel", ["password"] = password };
            if (token != null) fields["__RequestVerificationToken"] = token;
            request.Content = new FormUrlEncodedContent(fields);
        }
        else
        {
            request.Content = JsonContent.Create(new { username = "tgpanel", password });
            if (token != null) request.Headers.Add(AdminAuthSecurity.CsrfHeader, token);
        }
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ChangePasswordAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/panel/settings/password")
        {
            Content = JsonContent.Create(new { currentPassword = InitialPassword, newPassword = NewPassword })
        };
        request.Headers.Add(AdminAuthSecurity.CsrfHeader, token);
        return await client.SendAsync(request);
    }

    // 使用真实 Kestrel、Cookie 和防伪服务，验证浏览器协议边界，不替换安全服务。
    private sealed class SecurityHost(WebApplication app, string directory, Uri address,
        TaskCompletionSource snapshotCaptured, TaskCompletionSource releaseLateLogin) : IAsyncDisposable
    {
        public TaskCompletionSource SnapshotCaptured { get; } = snapshotCaptured;
        public TaskCompletionSource ReleaseLateLogin { get; } = releaseLateLogin;
        public AdminCredentialStore Store => app.Services.GetRequiredService<AdminCredentialStore>();

        public static async Task<SecurityHost> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "telegram-panel-auth-http-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var spaDirectory = Path.Combine(directory, "wwwroot", "panel-spa");
            Directory.CreateDirectory(Path.Combine(spaDirectory, "assets"));
            await File.WriteAllTextAsync(Path.Combine(spaDirectory, "index.html"), SpaIndex);
            await File.WriteAllTextAsync(Path.Combine(spaDirectory, "assets", "index-test.js"), SpaScript);
            await File.WriteAllTextAsync(Path.Combine(spaDirectory, "assets", "index-test.css"), SpaStyle);
            var uploadsDirectory = Path.Combine(directory, "uploads");
            Directory.CreateDirectory(uploadsDirectory);
            await File.WriteAllTextAsync(Path.Combine(uploadsDirectory, "test.txt"), "existing upload");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing", ContentRootPath = directory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = directory,
                ["AdminAuth:Enabled"] = "true",
                ["AdminAuth:InitialUsername"] = "tgpanel",
                ["AdminAuth:InitialPassword"] = InitialPassword,
                ["AdminAuth:CredentialsPath"] = Path.Combine(directory, "admin_auth.json")
            });
            builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));
            builder.Services.AddSingleton<AdminCredentialStore>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<AdminLoginLimiter>();
            builder.Services.AddAntiforgery(options => options.HeaderName = AdminAuthSecurity.CsrfHeader);
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
            {
                options.Cookie.Name = "TelegramPanel.HttpSecurityTests";
                options.Events.OnValidatePrincipal = AdminAuthSecurity.ValidatePrincipalAsync;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            });
            builder.Services.AddAuthorization();
            var app = builder.Build();
            var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLateLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var spaRoot = app.UsePanelSpaAndSecurity();
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(uploadsDirectory),
                RequestPath = "/uploads"
            });
            app.UseAuthorization();
            app.UseAntiforgery();
            app.MapPanelSpa(spaRoot);
            app.MapGet("/api/panel/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
                AdminAuthSecurity.GetCsrfToken(context, antiforgery)).AllowAnonymous();
            app.MapGet("/api/panel/auth/me", (HttpContext context, AdminCredentialStore store) => Results.Ok(new
            {
                authenticated = context.User.Identity?.IsAuthenticated == true,
                mustChangePassword = store.MustChangePassword
            }));
            app.MapPost("/test/late-login", async (HttpContext context, AdminCredentialStore store) =>
            {
                var snapshot = await store.ValidateForAuthenticationAsync("tgpanel", InitialPassword);
                Assert.NotNull(snapshot);
                snapshotCaptured.TrySetResult();
                await releaseLateLogin.Task;
                await AdminAuthSecurity.SignInAsync(context, snapshot);
                return Results.Ok();
            }).AllowAnonymous();
            app.MapPost("/api/panel/auth/login", async (HttpContext context, AdminCredentialStore store) =>
            {
                var request = await context.Request.ReadFromJsonAsync<LoginBody>();
                return await AuthenticateAsync(context, store, request?.Username, request?.Password);
            }).AllowAnonymous();
            app.MapPost("/login", async (HttpContext context, AdminCredentialStore store) =>
            {
                var form = await context.Request.ReadFormAsync();
                return await AuthenticateAsync(context, store, form["username"], form["password"]);
            }).AllowAnonymous();
            app.MapPost("/api/panel/settings/password", async (HttpContext context, AdminCredentialStore store) =>
            {
                var request = (await context.Request.ReadFromJsonAsync<PasswordBody>())!;
                await store.ChangePasswordAsync(request.CurrentPassword, request.NewPassword, context.RequestAborted);
                await AdminAuthSecurity.SignInAsync(context, store);
                return Results.Ok();
            }).RequireAuthorization();
            app.MapGet("/api/panel/summary", () => Results.Ok(new { protectedData = true })).RequireAuthorization();
            app.MapGet("/_blazor", () => Results.Ok()).RequireAuthorization();
            app.MapGet("/downloads/accounts.zip", () => Results.Ok("private archive")).RequireAuthorization();
            app.MapGet("/healthz", () => Results.Ok()).AllowAnonymous();
            await app.Services.GetRequiredService<AdminCredentialStore>().EnsureInitializedAsync();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new SecurityHost(app, directory, new Uri(address), snapshotCaptured, releaseLateLogin);
        }

        private static async Task<IResult> AuthenticateAsync(
            HttpContext context, AdminCredentialStore store, string? username, string? password)
        {
            var snapshot = await store.ValidateForAuthenticationAsync(username, password, context.RequestAborted);
            if (snapshot is null) return Results.Unauthorized();
            await AdminAuthSecurity.SignInAsync(context, snapshot);
            return Results.Ok();
        }

        public HttpClient CreateClient() => new(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer()
        }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };

        public async ValueTask DisposeAsync()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cancellation.Token);
            await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record LoginBody(string Username, string Password);
    private sealed record PasswordBody(string CurrentPassword, string NewPassword);
}
