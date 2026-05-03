using Creuser.Agents;
using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Auth.Providers.Local;
using Creuser.Persistence;
using Creuser.Web.Agents;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Branding;
using Creuser.Web.Endpoints;
using Creuser.Web.Environment;
using Creuser.Web.Hubs;
using Creuser.Web.Workspaces;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Scalar.AspNetCore;

DapperSetup.Initialize();

var builder = WebApplication.CreateBuilder(args);

// Per-developer overrides — populated by `npm run services:up` with the
// random host ports Docker assigned to the dev Postgres and Redis
// containers. Gitignored. In production this file does not exist and the
// connection strings come from environment variables (ConnectionStrings__*).
builder.Configuration.AddJsonFile(
    $"appsettings.{builder.Environment.EnvironmentName}.local.json",
    optional: true,
    reloadOnChange: true
);

// In production the container sets CREUSER_DATA_DIR=/data. In dev we want a
// repo-relative path that survives `dotnet clean`, is easy to inspect from
// the IDE, and is gitignored — falling back to a bin/-relative directory
// would scatter data across Debug/Release builds and lose it on rebuild.
var dataDir = builder.Configuration["CREUSER_DATA_DIR"];
if (string.IsNullOrEmpty(dataDir))
{
    dataDir = builder.Environment.IsDevelopment()
        ? Path.Combine(FindRepoRoot(AppContext.BaseDirectory), ".data")
        : Path.Combine(AppContext.BaseDirectory, ".creuser-data");
}
Directory.CreateDirectory(dataDir);

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return start;
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;

    // Creuser is deployed behind trusted reverse proxies (Railway, an org's
    // ingress, or the Quasar dev proxy). The default loopback-only allowlist
    // would silently drop forwarded headers in those environments.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new BrandingAssetsService(dataDir));
builder.Services.AddSingleton(new SecretsService(dataDir));
builder.Services.AddSingleton(new WorkspaceFilesystemService(dataDir));
builder.Services.AddSingleton<AgentClientFactory>();
builder.Services.AddScoped<AgentClientResolver>();

// Capability registry. Add additional ICapabilityProvider registrations
// here as new modules / plugins land; CapabilityRegistry composes whatever
// providers it finds in DI.
builder.Services.AddSingleton<ICapabilityProvider, CoreCapabilityProvider>();
builder.Services.AddScoped<CapabilityRegistry>();
builder.Services.AddScoped<AgentTools>();
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

// Caching. In-process IMemoryCache is always available. The distributed
// cache (IDistributedCache) is backed by Redis when ConnectionStrings:Redis
// is set, otherwise it falls back to in-memory so dev / single-instance
// deployments don't require Redis to be running.
builder.Services.AddMemoryCache();
var redisConn = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConn))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConn;
        options.InstanceName = "creuser:";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

// Persistence + auth wiring.
builder.Services.AddDatabase();
builder.Services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddLocalAuth();

// Cookie session, data protection keys persisted under <DataDir>/keys/.
var keysDir = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keysDir);
builder
    .Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("Creuser");

builder
    .Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opts =>
    {
        opts.Cookie.Name = "creuser-session";
        opts.Cookie.HttpOnly = true;
        opts.Cookie.SameSite = SameSiteMode.Lax;
        opts.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        opts.SlidingExpiration = true;
        opts.ExpireTimeSpan = TimeSpan.FromDays(14);
        opts.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        opts.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseStatusCodePages();
app.UseExceptionHandler();

// Serve the SPA from wwwroot. Quasar's build emits index.html + assets there.
// UseDefaultFiles rewrites "/" to "/index.html"; UseStaticFiles serves the
// emitted assets. Endpoints (/api, /hub, /scalar, /openapi) are matched first
// by routing; MapFallbackToFile only fires for unmatched routes — that's how
// client-side router deep links resolve to index.html.
app.UseDefaultFiles();
app.UseStaticFiles();

// Branded assets (logo, favicon, login background) live under <dataDir>/branding/.
// Public so the login screen can fetch the logo before sign-in. Cached by
// the browser per filename — uploads are content-addressed so each new
// upload has a unique URL and old caches don't get stale.
var brandingAssets = app.Services.GetRequiredService<BrandingAssetsService>();
app.UseStaticFiles(
    new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(brandingAssets.DirectoryPath),
        RequestPath = BrandingAssetsService.UrlPrefix,
        OnPrepareResponse = ctx =>
            ctx.Context.Response.Headers.CacheControl = "public, max-age=86400, immutable",
    }
);

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();

if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference(options => options.WithTitle("Creuser API"));
}

app.MapAuthEndpoints();
app.MapAdminUsersEndpoints();
app.MapBrandingEndpoints();
app.MapEnvironmentEndpoints();
app.MapAgentsEndpoints();
app.MapWorkspacesEndpoints();
app.MapPingEndpoints();
app.MapEchoEndpoints();
app.MapHub<NotificationsHub>("/hub/notifications");

app.MapFallbackToFile("index.html");

app.Run();

// Marker for WebApplicationFactory in tests, and AddValidatorsFromAssembly above.
public partial class Program;
