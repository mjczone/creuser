using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Persistence.AppSettings;
using Creuser.Web.Branding;
using Creuser.Web.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Creuser.Web.Endpoints;

public sealed record BrandingAssetResult(string Url, string ContentType, long Size);

public static class BrandingEndpoints
{
    /// <summary>The single key under which BrandingConfig is stored in cr.app_settings.</summary>
    public const string SettingKey = "branding";

    public static IEndpointRouteBuilder MapBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/branding").WithTags("Branding");

        // Anonymous so the login screen and unauthenticated pages can theme
        // themselves before sign-in. The branding doc carries no secrets.
        group.MapGet("/", (Delegate)Get).WithName("GetBranding").AllowAnonymous();

        group
            .MapPut("/", (Delegate)Put)
            .WithName("UpdateBranding")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group
            .MapPost("/assets/logo", (Delegate)UploadLogo)
            .WithName("UploadLogo")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .DisableAntiforgery();

        return app;
    }

    private static async Task<Ok<ApiResult<BrandingConfig>>> Get(IAppSettingsStore store)
    {
        var current = await store.GetAsync<BrandingConfig>(SettingKey);
        return TypedResults.Ok(new ApiResult<BrandingConfig>(current ?? BrandingConfig.Default));
    }

    private static async Task<Results<Ok<ApiResult<BrandingConfig>>, ProblemHttpResult>> Put(
        BrandingConfig request,
        IAppSettingsStore store,
        HttpContext http
    )
    {
        if (string.IsNullOrWhiteSpace(request.ProductName))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]> { ["productName"] = ["Product name is required."] }
            );

        var updatedBy = CookieAuthHelpers.GetUserId(http);
        await store.SetAsync(SettingKey, request, updatedBy);
        return TypedResults.Ok(new ApiResult<BrandingConfig>(request));
    }

    private static async Task<
        Results<Ok<ApiResult<BrandingAssetResult>>, ProblemHttpResult>
    > UploadLogo([FromForm] IFormFile file, BrandingAssetsService assets, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Problems.ValidationFailed(
                new Dictionary<string, string[]> { ["file"] = ["A file is required."] }
            );

        if (!assets.IsWithinSizeLimit(file.Length))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["file"] = [$"File exceeds {assets.MaxBytesAllowed / 1024} KB limit."],
                }
            );

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !assets.IsAllowedExtension(ext))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["file"] = ["Allowed types: png, jpg, jpeg, webp, svg, ico."],
                }
            );

        if (!assets.IsAllowedContentType(file.ContentType))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["file"] = [$"Disallowed content-type: {file.ContentType}."],
                }
            );

        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var url = await assets.SaveAsync("logo", bytes, ext, ct);
        return TypedResults.Ok(
            new ApiResult<BrandingAssetResult>(
                new BrandingAssetResult(url, file.ContentType, bytes.Length)
            )
        );
    }
}
