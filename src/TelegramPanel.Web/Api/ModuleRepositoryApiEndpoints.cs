using System.Text.Json;
using TelegramPanel.Web.Modules;
using TelegramPanel.Web.Services;

namespace TelegramPanel.Web.Api;

public static class ModuleRepositoryApiEndpoints
{
    public static void MapModuleRepositoryApi(this RouteGroupBuilder secured)
    {
        var repositories = secured.MapGroup("/module-repositories");
        repositories.MapGet("", (ModuleRepositoryStore store, CancellationToken ct) =>
            RespondAsync(async () => Results.Ok(await store.ListAsync(ct))));
        repositories.MapPost("", (ModuleRepositoryInput input, ModuleRepositoryStore store, CancellationToken ct) =>
            RespondAsync(async () => Results.Ok(await store.SaveAsync(null, input, ct))));
        repositories.MapPut("/{id}", (string id, ModuleRepositoryInput input, ModuleRepositoryStore store, CancellationToken ct) =>
            RespondAsync(async () => Results.Ok(await store.SaveAsync(id, input, ct))));
        repositories.MapDelete("/{id}", (string id, ModuleRepositoryStore store, CancellationToken ct) =>
            RespondAsync(async () => { await store.DeleteAsync(id, ct); return Results.Ok(new OperationResultDto(true, "已移除仓库")); }));
        repositories.MapGet("/{id}/index", (string id, ModuleRepositoryService service, CancellationToken ct) =>
            RespondAsync(async () => Results.Ok(await service.GetIndexAsync(id, ct))));
        repositories.MapPost("/{id}/install", (string id, RepositoryInstallRequest request, ModuleRepositoryService service, AppRestartService restart, CancellationToken ct) =>
            RespondAsync(async () =>
            {
                var result = await service.InstallAsync(id, request, ct);
                if (!result.Success) return Results.BadRequest(new OperationResultDto(false, result.Message));
                if (request.AutoRestart) restart.RequestRestart(TimeSpan.FromSeconds(2), "repository module install");
                return Results.Ok(result);
            }));

        secured.MapGet("/modules/prune-preview", async (ModuleInstallerService installer) =>
            Results.Ok(await installer.GetPrunePreviewAsync()));
        secured.MapPost("/modules/prune-all", async (ModuleActionRequestDto request, ModuleInstallerService installer, AppRestartService restart) =>
        {
            var result = await installer.PruneAllOldVersionsAsync();
            if (request.AutoRestart && result.RemovedVersions > 0)
                restart.RequestRestart(TimeSpan.FromSeconds(2), "module prune all");
            return Results.Ok(result);
        });
    }

    private static async Task<IResult> RespondAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new OperationResultDto(false, ex.Message)); }
        catch (JsonException) { return Results.BadRequest(new OperationResultDto(false, "仓库 JSON 格式无效")); }
        catch (OperationCanceledException) { return Results.BadRequest(new OperationResultDto(false, "仓库请求超时或已取消")); }
        catch (HttpRequestException) { return Results.BadRequest(new OperationResultDto(false, "仓库连接失败，请检查网络、域名与 HTTPS 证书")); }
        catch (IOException) { return Results.BadRequest(new OperationResultDto(false, "仓库配置或模块文件读写失败，请检查磁盘空间与目录权限")); }
        catch (UnauthorizedAccessException) { return Results.BadRequest(new OperationResultDto(false, "仓库配置或模块目录无写入权限")); }
    }
}
