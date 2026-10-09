using TelegramPanel.Web.Services;

namespace TelegramPanel.Web.Api;

public static class WgcfWarpApiEndpoints
{
    public static RouteGroupBuilder MapWgcfWarpApi(this RouteGroupBuilder group)
    {
        group.MapGet("/proxies/wgcf", (WgcfWarpService service, CancellationToken ct) => service.ListAsync(ct));
        group.MapPost("/proxies/wgcf", async (WgcfCreateRequest request, WgcfWarpService service, CancellationToken ct) =>
            await HandleAsync(() => service.CreateAsync(request.RequestId, request.Name, request.AcceptTerms, ct), accepted: true));
        group.MapPost("/proxies/wgcf/{profile}/resume", async (string profile, WgcfWarpService service, CancellationToken ct) =>
            await HandleAsync(() => service.ResumeAsync(profile, ct), accepted: true));
        group.MapPost("/proxies/wgcf/{profile}/start", async (string profile, WgcfWarpService service, CancellationToken ct) =>
            await HandleAsync(() => service.SetEnabledAsync(profile, true, ct)));
        group.MapPost("/proxies/wgcf/{profile}/stop", async (string profile, WgcfWarpService service, CancellationToken ct) =>
            await HandleAsync(() => service.SetEnabledAsync(profile, false, ct)));
        group.MapPost("/proxies/wgcf/{profile}/test", async (string profile, WgcfWarpService service, CancellationToken ct) =>
            await HandleAsync(() => service.TestAsync(profile, ct)));
        return group;
    }

    private static async Task<IResult> HandleAsync(Func<Task<WgcfProfileDto>> operation, bool accepted = false)
    {
        try
        {
            var result = await operation();
            return accepted ? Results.Accepted(value: result) : Results.Ok(result);
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(new OperationResultDto(false, ex.Message)); }
        catch (ArgumentException ex) { return Results.BadRequest(new OperationResultDto(false, ex.Message)); }
        catch (InvalidOperationException ex) { return Results.Conflict(new OperationResultDto(false, ex.Message)); }
    }
}

public sealed record WgcfCreateRequest(string RequestId, string Name, bool AcceptTerms);
