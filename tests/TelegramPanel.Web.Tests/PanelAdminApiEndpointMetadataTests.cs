using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Web.Api;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class PanelAdminApiEndpointMetadataTests
{
    [Fact]
    public async Task ZipImport_TransportLimitsAllowTheBusinessFileLimit()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        await using var app = builder.Build();
        PanelAdminApiEndpoints.ConfigureAccountImportZipLimits(
            app.MapPost("/zip-import", () => "ok"));

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => string.Equals(
                item.RoutePattern.RawText,
                "/zip-import",
                StringComparison.Ordinal));

        var requestLimit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        var formLimits = endpoint.Metadata.GetMetadata<IFormOptionsMetadata>();

        Assert.NotNull(requestLimit);
        Assert.Equal(
            PanelAdminApiEndpoints.AccountImportZipMaxRequestSize,
            requestLimit.MaxRequestBodySize);
        Assert.NotNull(formLimits);
        Assert.Equal(
            PanelAdminApiEndpoints.AccountImportZipMaxRequestSize,
            formLimits.MultipartBodyLengthLimit);
    }

    [Fact]
    public void PrepareZipImportRequest_ConfiguresManualFormReadLimits()
    {
        var context = new DefaultHttpContext();
        var requestSizeFeature = new MutableRequestSizeFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(
            requestSizeFeature);

        var formOptions =
            PanelAdminApiEndpoints.PrepareAccountImportZipRequest(
                context.Request);

        Assert.Equal(
            PanelAdminApiEndpoints.AccountImportZipMaxRequestSize,
            requestSizeFeature.MaxRequestBodySize);
        Assert.Equal(
            PanelAdminApiEndpoints.AccountImportZipMaxRequestSize,
            formOptions.MultipartBodyLengthLimit);
    }

    [Fact]
    public async Task GroupAdminKickEndpoint_UsesProtectedPostRoute()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Services.AddSingleton<GroupManagementService>(_ => null!);
        builder.Services.AddSingleton<IGroupService>(_ => null!);
        await using var app = builder.Build();
        var secured = app.MapGroup("/api/panel").RequireAuthorization();
        PanelAdminApiEndpoints.MapGroupAdminKickEndpoint(secured);

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => string.Equals(
                item.RoutePattern.RawText,
                "/api/panel/groups/{id:int}/admins/{userId:long}/kick",
                StringComparison.Ordinal));

        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
        Assert.NotNull(methods);
        Assert.Contains("POST", methods.HttpMethods);
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>());
    }

    private sealed class MutableRequestSizeFeature
        : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

}
