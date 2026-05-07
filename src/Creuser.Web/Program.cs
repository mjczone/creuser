using Creuser.Agents;
using Creuser.Auth.Abstractions;
using Creuser.Auth.Core;
using Creuser.Auth.Providers.Local;
using Creuser.Core.Execution;
using Creuser.Core.Projections;
using Creuser.Core.Repositories;
using Creuser.Core.Secrets;
using Creuser.Persistence;
using Creuser.Persistence.Repositories;
using Creuser.Plugins.Loader;
using Creuser.Projections.Conventions;
using Creuser.Projections.Scanner;
using Creuser.Projections.Sync;
using Creuser.Sagas;
using Creuser.Sagas.Handlers;
using Creuser.Scripting;
using Creuser.Scripting.ToolLoop;
using Creuser.Web.Agents;
using Creuser.Web.Agents.Capabilities;
using Creuser.Web.Branding;
using Creuser.Web.Endpoints;
using Creuser.Web.Environment;
using Creuser.Web.Hubs;
using Creuser.Web.Schedules;
using Creuser.Web.Workspaces;
using FluentValidation;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.Marten;

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
var secretsService = new SecretsService(dataDir);
builder.Services.AddSingleton(secretsService);
builder.Services.AddSingleton<ISecretsReader>(secretsService);
builder.Services.AddSingleton(new WorkspaceFilesystemService(dataDir));
builder.Services.AddSingleton<AgentClientFactory>();
builder.Services.AddScoped<AgentClientResolver>();

// Cross-layer abstraction so Creuser.Scripting (and plugins) can resolve
// chat clients without taking a host dependency.
builder.Services.AddScoped<IChatClientResolver>(sp => sp.GetRequiredService<AgentClientResolver>());

// Execution model — scripts, runs, the runner registry, the executor.
// Step runners are registered as keyed services on their step type so the
// executor can resolve "what runs `llm-chat`?" without iterating.
builder.Services.AddScoped<IJobScriptStore, jobScriptsRepository>();
builder.Services.AddScoped<IJobRunStore, jobRunsRepository>();
builder.Services.AddScoped<ILlmCacheStore, llmCacheRepository>();
builder.Services.AddScoped<IJobPlanStore, jobPlansRepository>();
builder.Services.AddKeyedScoped<IStepRunner, LlmChatStepRunner>("llm-chat");
builder.Services.AddKeyedScoped<IStepRunner, ShellStepRunner>("shell");
builder.Services.AddKeyedScoped<IStepRunner, CSharpStepRunner>("csharp");
builder.Services.AddKeyedScoped<IStepRunner, FileMutateStepRunner>("file-mutate");
builder.Services.AddKeyedScoped<IStepRunner, PythonStepRunner>("python");
builder.Services.AddKeyedScoped<IStepRunner, NodeStepRunner>("node");
builder.Services.AddKeyedScoped<IStepRunner, FileFrontmatterStepRunner>("file-frontmatter");
builder.Services.AddKeyedScoped<IStepRunner, HttpStepRunner>("http");
builder.Services.AddKeyedScoped<IStepRunner, LlmToolLoopStepRunner>("llm-tool-loop");
builder.Services.AddKeyedScoped<IStepRunner, ProjectionSyncStepRunner>("projection-sync");
builder.Services.AddKeyedScoped<IStepRunner, LlmPlannerStepRunner>("llm-planner");

// Tool registries contributed to the agentic llm-tool-loop runner.
// Multi-binding — every IToolLoopToolRegistry the runner finds in DI gets
// composed; the runner validates the per-step tool allow-list against the
// union and dispatches by tool name. Plugins (when the loader lands)
// register additional registries here, e.g. for projection / domain tools.
builder.Services.AddScoped<IToolLoopToolRegistry, WorkspaceToolLoopRegistry>();

// Projections — conventions / scanner / entity store / sync service.
// The projection sync runs automatically as a fire-and-forget continuation
// of WorkspacesEndpoints.Sync after a successful pull (mirrors the
// schedules sync-hook integration). It's also exposed as a `projection-sync`
// step type for explicit DAG composition.
builder.Services.AddScoped<IEntityStore, entitiesRepository>();
builder.Services.AddScoped<IEntityRefStore, entityRefsRepository>();
builder.Services.AddScoped<IConventionLoader, ConventionLoader>();
builder.Services.AddScoped<ProjectionScanner>();
builder.Services.AddScoped<IProjectionSyncService, ProjectionSyncService>();
builder.Services.AddScoped<Creuser.Projections.Authoring.ConventionEditor>();
builder.Services.AddScoped<
    IToolLoopToolRegistry,
    Creuser.Projections.ToolLoop.ProjectionToolLoopRegistry
>();

// HTTP step runner uses IHttpClientFactory for socket + DNS lifecycle.
// Two named clients differ on redirect behavior; the runner picks at
// request time based on the `follow_redirects` input.
builder.Services.AddHttpClient(
    "creuser-http",
    c =>
    {
        // 60s is the outer wall — the runner's per-request `timeout_seconds`
        // (default 30) and per-step `budgets.max_duration_seconds` cap
        // requests below this.
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Creuser/0.1 (+http step runner)");
    }
);
builder
    .Services.AddHttpClient(
        "creuser-http-noredirect",
        c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Creuser/0.1 (+http step runner)");
        }
    )
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddScoped<IWorkspaceWorkingTree, WorkspaceWorkingTree>();

// Per-provider workspace verbs — git/local today, s3 forward-looking. Each
// provider declares its capabilities (write/commit/push/sync) so the
// endpoint layer dispatches by type without switching, and the SPA's
// header surface hides Commit/Push buttons for providers that don't
// support them.
builder.Services.AddKeyedScoped<IWorkspaceProvider, GitWorkspaceProvider>(WorkspaceType.Git);
builder.Services.AddKeyedScoped<IWorkspaceProvider, LocalWorkspaceProvider>(WorkspaceType.Local);
builder.Services.AddScoped<IWorkspaceProviderRegistry, WorkspaceProviderRegistry>();
builder.Services.AddSingleton<IWorkspaceStatusBroadcaster, WorkspaceStatusBroadcaster>();
builder.Services.AddSingleton<IToolCatalog, BaselineToolCatalog>();

// Marten event store + Wolverine durable saga executor.
// `mt` schema holds the run event stream + saga state document.
// `cr.job_runs` and `cr.job_run_steps` stay populated via the existing
// IJobRunStore (imperative writes alongside event appends) — see
// docs/wip/wolverine-marten-design.md "Persistence and projections".
//
// Connection: shares the same Postgres database the rest of the app uses;
// Marten manages its own connection pool internally. Auto-create
// schema/tables on first startup is gated behind the same build-time
// OpenAPI guard the DbInitializer uses — the build-time tool spins up the
// host without Postgres available, and Marten's startup tries to connect.
var isBuildTimeOpenApi = IsBuildTimeOpenApiGeneration();
if (!isBuildTimeOpenApi)
{
    // Marten + Wolverine wiring. Connection string is bound late via
    // MartenConnectionConfigurer (IConfigureMarten) so test fixtures
    // (WebApplicationFactory + AddInMemoryCollection) can override it
    // before Marten initializes. Reading builder.Configuration directly
    // here would miss the WAF override (which is added during host build,
    // after the eager string-typed StoreOptions.Connection registers).
    builder
        .Services.AddMarten(opts =>
        {
            opts.DatabaseSchemaName = "mt";
            opts.Events.DatabaseSchemaName = "mt";
            opts.UseNewtonsoftForSerialization();
        })
        .IntegrateWithWolverine()
        .ApplyAllDatabaseChangesOnStartup();
    builder.Services.ConfigureMarten(
        (sp, opts) =>
        {
            var conn =
                sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
                    .GetConnectionString("Postgres")
                ?? string.Empty;
            opts.Connection(conn);
        }
    );

    var isTestEnvironment = string.Equals(
        builder.Environment.EnvironmentName,
        "Test",
        StringComparison.OrdinalIgnoreCase
    );
    builder.Host.UseWolverine(opts =>
    {
        opts.Discovery.IncludeAssembly(typeof(JobRunSagaHandler).Assembly);
        // Single-tenant on-prem deployment shape — no multi-node ownership
        // coordination needed. Solo mode skips the cross-node "release
        // ownership on stop" cleanup and matches our deployment principle
        // of one Creuser instance per organization.
        opts.Durability.Mode = DurabilityMode.Solo;
        if (!isTestEnvironment)
            opts.Policies.UseDurableLocalQueues();
    });
}

// Skip mirrors the DbInitializer guard so build-time OpenAPI generation
// doesn't try to connect to Postgres.
static bool IsBuildTimeOpenApiGeneration()
{
    var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
    if (
        entryName is not null
        && entryName.Contains("getdocument", StringComparison.OrdinalIgnoreCase)
    )
        return true;
    return Environment.CommandLine.Contains(
        "dotnet-getdocument",
        StringComparison.OrdinalIgnoreCase
    );
}

// Synchronous-endpoint waiter — singleton task-completion-source registry
// keyed on RunId. Single-instance only; multi-instance deployments need a
// Redis pub/sub backplane to relay JobRunFinished cross-host. See the
// design doc for the v0.2 path.
builder.Services.AddSingleton<RunCompletionWaiter>();

// Persistence first — `cr.*` tables are created by the DbInitializer
// hosted service. Hosted services run in registration order, so
// AddDatabase() must precede AddHostedService<PluginInitializer> below;
// otherwise the plugin registry tries to INSERT into cr.plugins before
// the table exists. The auth + admin/user wiring lives further below;
// only the data-source + DbInitializer pieces need to run early.
builder.Services.AddDatabase();

// Plugin loader. Discovery happens BEFORE host build so plugin
// contributions land in the same DI container as the host's services.
// `<dataDir>/plugins/*/<plugin>.dll` is the discovery surface; each
// subdirectory is one plugin. The PluginInitializer hosted service
// then persists the registry to cr.plugins after DbInitializer has
// created the table.
var pluginsRoot = Path.Combine(dataDir, "plugins");
Directory.CreateDirectory(pluginsRoot);
var pluginLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var pluginDiscoveryLogger = pluginLoggerFactory.CreateLogger<PluginDiscovery>();
var discovered = new PluginDiscovery(pluginDiscoveryLogger).Discover(pluginsRoot);

// Contributions registry must be in DI BEFORE plugin activation so the
// AddPluginStepRunner / AddPluginToolRegistry helpers can find + populate
// it during each plugin's Configure() call. The same instance is later
// resolved by the dispatch path to gate per-workspace enablement.
var pluginContributions = new PluginContributions();
builder.Services.AddSingleton<IPluginContributions>(pluginContributions);

new PluginActivator().ActivateAll(discovered, builder.Services, pluginLoggerFactory);
var pluginRegistry = new PluginRegistry();
builder.Services.AddSingleton(pluginRegistry);
builder.Services.AddSingleton<IPluginRegistry>(pluginRegistry);
builder.Services.AddSingleton<IReadOnlyList<DiscoveredPlugin>>(discovered);
builder.Services.AddScoped<IPluginRecordStore, pluginsRepository>();
builder.Services.AddScoped<IWorkspacePluginStore, workspacePluginsRepository>();
builder.Services.AddScoped<IPluginSettingsStore, workspacePluginSettingsRepository>();
builder.Services.AddHostedService<PluginInitializer>();

// Schedules. The dispatcher fires a job in a fresh DI scope so neither
// the cron tick nor the sync hook pin the executor's lifetime. The
// SchedulerService is the cron tick — checks `cr.schedules` every
// `CREUSER_SCHEDULER_INTERVAL_MS` (default 30000ms) and dispatches due
// rows. Sync-triggered schedules fire inline from WorkspacesEndpoints.Sync.
builder.Services.AddScoped<IScheduleStore, schedulesRepository>();
builder.Services.AddScoped<IJobScheduleDispatcher, JobScheduleDispatcher>();
builder.Services.AddHostedService<SchedulerService>();

// Dashboards. The store backs CRUD; the seeder runs as a fire-and-forget
// continuation of WorkspacesEndpoints.Create to populate the default
// Home + Operations group on workspace creation. Idempotent — re-runs
// don't clobber user-edited rows.
builder.Services.AddScoped<IDashboardStore, dashboardsRepository>();
builder.Services.AddScoped<IDashboardSeeder, DashboardSeeder>();

// Backfill defaults for any workspace that doesn't have them yet — pre-existing
// workspaces created before the composer slice shipped, plus any workspace
// where the create-time seeder failed silently. Idempotent; matches on
// (workspace_id, slug) so re-runs leave user-edited rows untouched.
builder.Services.AddHostedService<DashboardBackfillService>();

// Workspace memberships. The store is the source of truth for non-admin
// access — admins bypass it entirely (admin-ness implies Editor on every
// workspace per the architecture's auth model). Endpoints are admin-gated
// in v1; v0.2 may relax read access to workspace-viewers.
builder.Services.AddScoped<IWorkspaceMemberStore, workspaceMembersRepository>();

// Capability registry. Add additional ICapabilityProvider registrations
// here as new modules / plugins land; CapabilityRegistry composes whatever
// providers it finds in DI.
//
//  - CoreCapabilityProvider: hand-curated catalog (stage 1). Shrinks as
//    [AiCapability]-decorated endpoints absorb its entries.
//  - EndpointAttributeProvider: scans the host assembly for [AiCapability]
//    attributes (stage 2). Singleton — the reflection scan happens once.
//  - Plugin-contributed providers (stage 3) plug in here once the loader
//    lands.
builder.Services.AddSingleton<ICapabilityProvider, CoreCapabilityProvider>();
builder.Services.AddSingleton<ICapabilityProvider>(_ => new EndpointAttributeProvider());
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

// Auth wiring (database itself was registered earlier so DbInitializer
// runs ahead of the plugin/scheduler hosted services).
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
app.MapJobsEndpoints();
app.MapToolsEndpoints();
app.MapSchedulesEndpoints();
app.MapDashboardsEndpoints();
app.MapMembersEndpoints();
app.MapProjectionsEndpoints();
app.MapConventionsSchemaEndpoints();
app.MapConventionsAuthoringEndpoints();
app.MapPlansEndpoints();
app.MapPingEndpoints();
app.MapEchoEndpoints();
app.MapHub<NotificationsHub>("/hub/notifications");

app.MapFallbackToFile("index.html");

app.Run();

// Marker for WebApplicationFactory in tests, and AddValidatorsFromAssembly above.
public partial class Program;
