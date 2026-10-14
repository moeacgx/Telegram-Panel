using Microsoft.Extensions.FileProviders;

namespace TelegramPanel.Web.Services;

/// <summary>集中注册 SPA 文件与安全管线，供宿主和 HTTP 回归测试共用。</summary>
public static class PanelSpaHosting
{
    public static string UsePanelSpaAndSecurity(this WebApplication app)
    {
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

        // 路由会选中资源 404 兜底端点，因此必须先让真实 SPA 文件完成响应。
        // 仅公开 SPA 文件；默认 wwwroot 与 uploads 仍由宿主在安全管线之后注册。
        app.UseRouting();
        app.UseAuthentication();
        app.Use(AdminAuthSecurity.ProtectAsync);

        return spaRoot;
    }

    public static void MapPanelSpa(this WebApplication app, string spaRoot)
    {
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
    }
}
