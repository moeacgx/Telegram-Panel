using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace TelegramPanel.Web.Services;

public static class AdminAuthSecurity
{
    public const string StampClaim = "admin_security_stamp";
    public const string CsrfHeader = "X-TelegramPanel-CSRF";

    public static Task SignInAsync(HttpContext http, AdminCredentialStore store) =>
        SignInAsync(http, new AdminCredentialStore.AuthenticationSnapshot(store.Username, store.SecurityStamp));

    public static Task SignInAsync(HttpContext http, AdminCredentialStore.AuthenticationSnapshot snapshot) => http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, snapshot.Username), new Claim(ClaimTypes.Role, "Admin"),
            new Claim(StampClaim, snapshot.SecurityStamp)
        }, CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var store = context.HttpContext.RequestServices.GetRequiredService<AdminCredentialStore>();
        await store.EnsureInitializedAsync(context.HttpContext.RequestAborted);
        if (context.Principal?.FindFirst(StampClaim)?.Value != store.SecurityStamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    public static IResult GetCsrfToken(HttpContext http, IAntiforgery antiforgery)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { token = antiforgery.GetAndStoreTokens(http).RequestToken });
    }

    public static async Task ProtectAsync(HttpContext http, RequestDelegate next)
    {
        var path = http.Request.Path.Value?.TrimEnd('/') ?? "";
        var login = path.Equals("/login", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/panel/auth/login", StringComparison.OrdinalIgnoreCase);
        var settings = path.Equals("/api/panel/settings/password", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/panel/settings/username", StringComparison.OrdinalIgnoreCase);
        if (HttpMethods.IsPost(http.Request.Method) && (login || settings))
        {
            if (login && !http.RequestServices.GetRequiredService<AdminLoginLimiter>()
                .TryAcquire(http.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown", out var retryAfter))
            {
                http.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await RejectAsync(http, 429, "LOGIN_RATE_LIMITED", "登录尝试过于频繁，请稍后重试");
                return;
            }
            try
            {
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                await RejectAsync(http, 400, "INVALID_CSRF_TOKEN", "安全校验失败，请刷新页面后重试");
                return;
            }
        }

        var store = http.RequestServices.GetRequiredService<AdminCredentialStore>();
        // 限制后台 Cookie、后台 API 和旧交互通道；独立 API Key 与 Telegram Webhook 保留各自合同。
        var panelRequest = http.User.Identity?.IsAuthenticated == true
            || path.StartsWith("/api/panel/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
            || http.GetEndpoint()?.Metadata.GetMetadata<IAuthorizeData>() != null;
        if (store.Enabled && store.MustChangePassword && panelRequest && !IsSetupPath(path, http.Request.Method))
        {
            if (HttpMethods.IsGet(http.Request.Method) && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase))
                http.Response.Redirect("/ui/admin/password");
            else
                await RejectAsync(http, 403, "PASSWORD_CHANGE_REQUIRED", "请先修改初始密码");
            return;
        }
        await next(http);
    }

    internal static bool IsSetupPath(string path, string method)
    {
        if (HttpMethods.IsGet(method))
            return path.Equals("/login", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/logout", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/panel/auth/me", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/panel/auth/csrf", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/ui", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/ui/", StringComparison.OrdinalIgnoreCase);
        return HttpMethods.IsPost(method) && new[] { "/login", "/api/panel/auth/login", "/api/panel/auth/logout",
            "/api/panel/settings/password", "/api/panel/settings/username" }.Contains(path, StringComparer.OrdinalIgnoreCase);
    }

    private static Task RejectAsync(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        http.Response.Headers.CacheControl = "no-store";
        return http.Response.WriteAsJsonAsync(new { code, message }, http.RequestAborted);
    }
}
