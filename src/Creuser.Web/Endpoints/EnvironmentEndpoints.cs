using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Persistence.AppSettings;
using Creuser.Web.Contracts;
using Creuser.Web.Environment;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Creuser.Web.Endpoints;

public static class EnvironmentEndpoints
{
    public const string SettingKey = "environment";

    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        // All environment endpoints are admin-only — they expose deployment
        // configuration that non-admin users have no business seeing or
        // changing (SMTP host, AI provider keys' presence, base URL).
        var group = app.MapGroup("/api/environment")
            .WithTags("Environment")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", (Delegate)Get).WithName("GetEnvironment");
        group.MapPut("/", (Delegate)Put).WithName("UpdateEnvironment");

        var secretsGroup = group.MapGroup("/secrets");
        secretsGroup.MapGet("/", (Delegate)ListSecrets).WithName("ListEnvironmentSecrets");
        secretsGroup.MapPut("/{name}", (Delegate)SetSecret).WithName("SetEnvironmentSecret");
        secretsGroup
            .MapDelete("/{name}", (Delegate)DeleteSecret)
            .WithName("DeleteEnvironmentSecret");

        return app;
    }

    private static async Task<Ok<ApiResult<EnvironmentConfigView>>> Get(
        IAppSettingsStore store,
        SecretsService secrets
    )
    {
        var current =
            await store.GetAsync<EnvironmentConfig>(SettingKey) ?? EnvironmentConfig.Default;
        var view = BuildView(current, secrets);
        return TypedResults.Ok(new ApiResult<EnvironmentConfigView>(view));
    }

    private static async Task<Results<Ok<ApiResult<EnvironmentConfigView>>, ProblemHttpResult>> Put(
        EnvironmentConfig request,
        IAppSettingsStore store,
        SecretsService secrets,
        HttpContext http
    )
    {
        // No structured validation rules right now — every field is optional
        // and admin-meaningful as null. SMTP host requires no specific format
        // (could be hostname, IP, IDN); base URL doesn't have to be reachable
        // from the server. We let the consumers (SmtpClient, AI providers)
        // surface their own errors when they actually try to use the values.
        var updatedBy = CookieAuthHelpers.GetUserId(http);
        await store.SetAsync(SettingKey, request, updatedBy);
        return TypedResults.Ok(new ApiResult<EnvironmentConfigView>(BuildView(request, secrets)));
    }

    private static Ok<ApiResult<IReadOnlyList<string>>> ListSecrets(SecretsService secrets)
    {
        return TypedResults.Ok(new ApiResult<IReadOnlyList<string>>(secrets.List()));
    }

    private static async Task<Results<Ok<ApiResult<bool>>, ProblemHttpResult>> SetSecret(
        string name,
        SetSecretRequest body,
        SecretsService secrets,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(body.Value))
            return Problems.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["value"] = ["Value cannot be empty. Use DELETE to clear a secret."],
                }
            );

        try
        {
            await secrets.SetAsync(name, body.Value, ct);
        }
        catch (ArgumentException ex)
        {
            return Problems.ValidationFailed(
                new Dictionary<string, string[]> { ["name"] = [ex.Message] }
            );
        }
        return TypedResults.Ok(new ApiResult<bool>(true));
    }

    private static Results<Ok<ApiResult<bool>>, ProblemHttpResult> DeleteSecret(
        string name,
        SecretsService secrets
    )
    {
        try
        {
            return TypedResults.Ok(new ApiResult<bool>(secrets.Delete(name)));
        }
        catch (ArgumentException ex)
        {
            return Problems.ValidationFailed(
                new Dictionary<string, string[]> { ["name"] = [ex.Message] }
            );
        }
    }

    private static EnvironmentConfigView BuildView(EnvironmentConfig config, SecretsService secrets)
    {
        // Tell the UI which referenced secrets currently have on-disk values.
        // Keys are the same string the config records carry as `*.Secret`.
        var refs = new[]
        {
            config.Smtp.PasswordSecret,
            config.AiProviders.Anthropic?.ApiKeySecret,
            config.AiProviders.OpenAI?.ApiKeySecret,
            config.AiProviders.Local?.ApiKeySecret,
        }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct()
            .ToDictionary(name => name, secrets.Exists);

        return new EnvironmentConfigView(config, refs);
    }

    public sealed record SetSecretRequest(string Value);
}
