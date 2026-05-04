# Plugin Development Guide

> How to write, build, and deploy a Creuser plugin. Covers the plugin
> contract, the available extension points, the build + deploy flow,
> Docker patterns, and a walkthrough of the canonical example
> (`Creuser.Plugins.Examples.Hello`). Read this end-to-end before writing
> your first plugin.

## What a plugin is

A Creuser plugin is a .NET assembly (`.dll`) that contributes services
to the host's DI container at startup. Plugins extend the platform along
well-defined seams without modifying core: a plugin can ship a new step
runner, a new agent tool registry, a new capability provider for the
in-app assistant, or any combination. Plugins are discovered by the host
at boot from the on-disk plugin directory; they don't compile against
or modify the host source tree.

**Single-tenant deployment shape**: there is no upload mechanism in v1
and won't be unless multi-tenant deployments arrive. Adding a plugin is
a filesystem operation (admin SSH or Docker volume mount). The
operational story is "drop the folder under `<dataDir>/plugins/`,
restart Creuser." Hot-reload is post-v1.

## Extension points

A plugin can contribute any of:

| Extension point | Contract | Example |
| --- | --- | --- |
| **Step runners** | Implementations of `Creuser.Core.Execution.IStepRunner` registered via `AddPluginStepRunner<T>(stepType, context)` | `Examples.Hello` contributes `hello-world`; `Examples.Slack` contributes `slack-post` |
| **Tool registries (agentic)** | Implementations of `Creuser.Scripting.ToolLoop.IToolLoopToolRegistry` registered via `AddPluginToolRegistry<T>(context)` | `Examples.GitHubTools` contributes `read_pr`, `list_issues`, `comment_on_issue` to the `llm-tool-loop` agent |
| **Capability providers** | Implementations of `Creuser.Web.Agents.Capabilities.ICapabilityProvider` so the in-app assistant learns about plugin-supplied features | A plugin shipping settings pages registers them so users can ask "where do I configure X?" |
| **Convention types (post-v1)** | Custom convention loaders for the projection layer | Parse atlas SQL, GitHub workflow files, Linear issue templates into entity-graph rows |

**Always use the `AddPluginStepRunner` / `AddPluginToolRegistry` helpers
from `Creuser.Plugins.Abstractions`** rather than calling the underlying
`AddKeyedScoped` / `AddScoped` directly. The helpers do two things in
one call: (1) register the contribution into the host's DI, and (2)
record `(step type → plugin id)` or `(registry type → plugin id)` in
`IPluginContributions`. The host uses (2) to gate dispatch on
per-workspace enablement — a step type whose contributing plugin isn't
enabled for the workspace fails fast with a clear error before the
runner is invoked. Bypassing the helpers makes your contribution look
like a built-in platform service and silently bypasses the gate.

## The contract

Every plugin implements one interface from
`Creuser.Plugins.Abstractions`:

```csharp
public interface IPluginRegistration
{
    PluginManifest Manifest { get; }
    void Configure(IServiceCollection services, IPluginContext context);
}
```

The manifest is the plugin's identity:

```csharp
public sealed record PluginManifest(
    string Id,                          // e.g. "creuser.examples.hello"
    string Name,                        // human-readable
    string Version,                     // semver
    string? Author = null,
    string? Description = null,
    string? MinimumHostVersion = null,  // host rejects on mismatch
    IReadOnlyList<string>? RequiredTools = null,  // e.g. ["python>=3.12"]
    IReadOnlyList<string>? Provides = null,        // e.g. ["StepRunner:hello-world"]
    string? DocumentationUrl = null
);
```

Conventions:

- **One `IPluginRegistration` implementation per plugin assembly.** The
  loader raises a clear error if your assembly contains zero or
  multiple. By convention the class is named `<Vendor>Plugin` (e.g.
  `HelloPlugin`, `SlackPlugin`).
- **Parameterless constructor.** The loader instantiates via
  `Activator.CreateInstance`. Don't put DI dependencies in the
  constructor — those go on the classes you register inside `Configure`.
- **Plugin id matches folder name.** The loader walks
  `<dataDir>/plugins/<plugin-id>/`; convention is to match `Manifest.Id`
  to the folder. e.g. `creuser.examples.hello/Creuser.Plugins.Examples.Hello.dll`.
- **Stable id format.** Use `vendor.feature` lowercase. The id is the
  natural key in `cr.plugins` and in `cr.workspace_plugins` — changing
  it breaks per-workspace enablement.

The `IPluginContext` carries a logger pre-scoped to your plugin id and
the absolute filesystem path you were loaded from (read auxiliary files
relative to it).

## Project setup

Your plugin's csproj is a standard library project that references
`Creuser.Plugins.Abstractions`. The conventional shape:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <!-- Mark host references as PrivateAssets so we don't bundle the
         host's framework DLLs into our plugin folder — the loader uses
         the host's already-loaded copies. -->
    <PackageReference Include="Creuser.Plugins.Abstractions" Version="0.1.*">
      <PrivateAssets>all</PrivateAssets>
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
    <PackageReference Include="Creuser.Core" Version="0.1.*">
      <PrivateAssets>all</PrivateAssets>
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

`<ExcludeAssets>runtime</ExcludeAssets>` is critical: it tells MSBuild
not to copy the host's DLLs into your publish output. The loader
expects only your plugin's own assembly + non-host third-party deps in
the plugin folder; host types must come from the host's already-loaded
copy so type identity stays consistent across the boundary.

If your plugin has third-party deps that the host doesn't ship (e.g. a
Slack SDK), reference those WITHOUT `<ExcludeAssets>runtime</ExcludeAssets>`
— they'll be copied into your publish folder and the loader's
`AssemblyDependencyResolver` will pick them up via `deps.json`.

## Walk-through: the Hello plugin

The smallest possible plugin — one step runner that echoes input. Read
`src/plugins/Creuser.Plugins.Examples.Hello/` source as the canonical
"how to write a plugin" reference.

`HelloPlugin.cs` — the registration class:

```csharp
public sealed class HelloPlugin : IPluginRegistration
{
    public PluginManifest Manifest { get; } = new(
        Id: "creuser.examples.hello",
        Name: "Hello World Example",
        Version: "0.1.0",
        Author: "MJCZone",
        Description: "Smallest possible Creuser plugin...",
        MinimumHostVersion: "0.1.0",
        Provides: new[] { "StepRunner:hello-world" });

    public void Configure(IServiceCollection services, IPluginContext context)
    {
        // AddPluginStepRunner does both DI registration AND records the
        // contribution so the per-workspace enablement gate fires for
        // plugin-contributed step types.
        services.AddPluginStepRunner<HelloWorldStepRunner>("hello-world", context);
        context.Logger.LogInformation(
            "Hello plugin registered hello-world step runner from {Dir}",
            context.PluginDirectory);
    }
}
```

`HelloWorldStepRunner.cs` — the contributed step runner:

```csharp
public sealed class HelloWorldStepRunner : IStepRunner
{
    public string StepType => "hello-world";

    public Task<StepResult> ExecuteAsync(
        StepContext ctx,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken ct)
    {
        var name = inputs.TryGetValue("name", out var v) ? v?.ToString() : null;
        if (string.IsNullOrWhiteSpace(name)) name = "world";
        return Task.FromResult(StepResult.Success(
            new Dictionary<string, object?>
            {
                ["greeting"] = $"Hello, {name}!",
                ["name"] = name,
            },
            durationMs: 0));
    }
}
```

That's the whole plugin. Two files. Build it (`dotnet publish`), copy
the `.dll` to `<dataDir>/plugins/creuser.examples.hello/`, restart, and
admins can enable it per-workspace; workspace authors can then write:

```yaml
type: hello-world
inputs:
  name: Creuser
```

…in any job script and the `hello-world` step runner runs against the
saga-driven executor like any built-in runner.

## Walk-through: the Slack plugin

`Creuser.Plugins.Examples.Slack` ships the canonical pattern for a step
runner that talks to an external service. It demonstrates four things
the Hello plugin doesn't:

1. **Per-workspace plugin settings** — what the operator configures once.
2. **Secret resolution** — values that must never live in the database.
3. **`IHttpClientFactory` for outbound HTTP** — testable, named clients.
4. **Multi-input step runner** — required + optional inputs with defaults.

`SlackPlugin.cs` registers a named `slack-plugin` HttpClient and the
`slack-post` step runner via `AddPluginStepRunner`:

```csharp
public void Configure(IServiceCollection services, IPluginContext context)
{
    services.AddHttpClient("slack-plugin", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(15);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Creuser-Slack-Plugin/0.1");
    });
    services.AddPluginStepRunner<SlackPostStepRunner>("slack-post", context);
}
```

`SlackSettings` is a per-workspace record stored as JSON in
`cr.workspace_plugin_settings`. The plugin author defines its own shape;
the host stores the JSON verbatim:

```csharp
public sealed record SlackSettings(
    string? WebhookSecretName = null,  // filename in /data/secrets/
    string? DefaultChannel = null,
    string? DefaultUsername = null
);
```

Note what's NOT in the settings: the webhook URL itself. Settings hold
the *filename* of a secret; the value lives in `/data/secrets/<name>`
and is read via `ISecretsReader` at runtime. This keeps the URL out of
the queryable database — settings are JSON in Postgres, but secret
values stay on the local disk.

`SlackPostStepRunner` resolves credentials at execute time:

```csharp
public async Task<StepResult> ExecuteAsync(
    StepContext ctx,
    IReadOnlyDictionary<string, object?> inputs,
    CancellationToken ct)
{
    var settingsJson = await _settings.GetAsync(ctx.WorkspaceId, SlackPlugin.PluginId, ct);
    var settings = JsonSerializer.Deserialize<SlackSettings>(
        settingsJson ?? "{}",
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
    ) ?? new SlackSettings();

    var secretName = GetString(inputs, "webhook_url_secret") ?? settings.WebhookSecretName;
    var webhookUrl = await _secrets.ReadAsync(secretName, ct);  // reads /data/secrets/<name>

    var client = _http.CreateClient("slack-plugin");
    var response = await client.PostAsJsonAsync(webhookUrl, payload, ct);
    // ...returns StepResult.Success/Failure based on the HTTP outcome
}
```

**Use `JsonSerializerDefaults.Web` when deserializing settings.** The
host's settings endpoints write JSON in camelCase (web defaults). If
you deserialize with the .NET default serializer settings (PascalCase),
your settings record's properties will be `null` and your runner will
fail with a confusing "no webhook URL configured" error.

The Setup workflow once the plugin is staged:

1. Drop the plugin under `<dataDir>/plugins/creuser.examples.slack/` and restart.
2. Admin enables the plugin for the workspace via the Plugins page.
3. Operator stores the webhook URL as a secret: `creuser secrets set slack-prod.url 'https://hooks.slack.com/...'` (or via the Environment page).
4. Admin saves the plugin settings: `PUT /api/workspaces/{slug}/plugins/creuser.examples.slack/settings` with body `{ "settings": { "webhookSecretName": "slack-prod.url", "defaultChannel": "#alerts" } }`.
5. Job authors include `type: slack-post` steps.

```yaml
type: slack-post
inputs:
  text: "Build succeeded for {{ workspace.slug }}"
  channel: "#deploys"   # optional — overrides settings default
```

## Walk-through: the GitHub Tools plugin

`Creuser.Plugins.Examples.GitHubTools` ships the canonical pattern for
contributing tools to the **agentic** `llm-tool-loop` runner. The
agent doesn't see credentials directly — the registry resolves them
once per `BuildTools` call from workspace settings + secrets, then
bakes them into closures that get exposed as `AIFunction`s.

`GitHubToolsPlugin.cs` registers a named HttpClient and the registry
via `AddPluginToolRegistry`. Because the registry's CLR type is what
the host's enablement gate matches against, it must also be registered
as `IToolLoopToolRegistry` so the runner enumerates it:

```csharp
public void Configure(IServiceCollection services, IPluginContext context)
{
    services.AddHttpClient("github-plugin", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Creuser-GitHub-Plugin/0.1");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.BaseAddress = new Uri("https://api.github.com/");
    });
    services.AddPluginToolRegistry<GitHubToolRegistry>(context);
    // The host's tool-loop runner resolves IEnumerable<IToolLoopToolRegistry>
    // from DI; the AddPluginToolRegistry helper records the contribution but
    // doesn't bind it to the interface — that's the plugin's job.
    services.AddScoped<IToolLoopToolRegistry, GitHubToolRegistry>();
}
```

Inside `GitHubToolRegistry.BuildTools`, credentials are resolved once
per loop step *before* the agent sees the tool list:

```csharp
public IReadOnlyList<AIFunction> BuildTools(
    IReadOnlyList<string> names,
    StepContext ctx,
    ToolLogSink sink)
{
    var settingsJson = _settings.GetAsync(ctx.WorkspaceId, GitHubToolsPlugin.PluginId).GetAwaiter().GetResult();
    var settings = JsonSerializer.Deserialize<GitHubSettings>(settingsJson ?? "{}", SettingsJsonOptions);
    var pat = _secrets.ReadAsync(settings.PatSecretName).GetAwaiter().GetResult();
    var defaultRepo = settings.DefaultRepo;

    return names.Select(name => name switch
    {
        "read_pr" => BuildReadPr(pat, baseUrl, defaultRepo, sink),  // closes over pat
        // ...
    }).ToList();
}
```

The agent's tool-call args carry only task-specific values (`number`,
`body`); credentials are ambient. **Optional parameters in the lambda
must have explicit `= null` defaults** — `AIFunctionFactory` infers
"required" from the absence of a default value, even when the type is
nullable:

```csharp
AIFunctionFactory.Create(
    async (
        [Description("Repo as owner/name (overrides workspace default).")]
            string? repo = null,                // explicit default
        [Description("Pull request number.")] int number = 0,
        CancellationToken ct = default
    ) => { /* ... */ },
    name: "read_pr",
    description: "...");
```

Without the `= null`, the agent gets back an `ArgumentException` saying
the parameter is required and the tool call fails before your closure
runs.

## Build + deploy

The build is standard `dotnet publish`:

```bash
dotnet publish src/MyPlugin/MyPlugin.csproj \
    -c Release \
    -o ./out/myplugin
```

The output folder contains your plugin's `.dll` plus any non-host
transitive deps. Copy that folder verbatim to
`<dataDir>/plugins/<plugin-id>/`:

```bash
# On the deployment host
cp -r ./out/myplugin/ /var/lib/creuser/data/plugins/myplugin/
systemctl restart creuser
```

The plugin folder name is the loader's discovery key. The plugin's
main assembly resolution rules (in order):

1. `<plugin-id>.dll` — folder name matches DLL name (preferred convention).
2. `*.Plugin.dll` — any single file ending in `.Plugin.dll`.
3. The single `.dll` in the folder if there's exactly one.

The loader will report a clear error in `cr.plugins` if your folder
contents don't match one of these.

After restart, visit `Settings → Plugins` in the SPA. Your plugin
should appear with status **loaded** (or **failed** with a status
message describing what went wrong). Enable it per-workspace; the
contributions take effect immediately within that workspace.

### Workspace enablement model

Plugins are loaded **process-wide** at host startup. Per-workspace
enablement is a UI + persistence layer over that — `cr.workspace_plugins`
holds `(workspace_id, plugin_id, enabled)` rows. Workspace owners
enable the plugin from the Plugins page; operators can also do this via
`PUT /api/workspaces/{slug}/plugins/{pluginId}` with body
`{ "enabled": true }`.

The runtime enablement gate is enforced at two seams:

1. **Step dispatch** — `StepDispatchHandler` looks up the contributing
   plugin for a step type via `IPluginContributions.TryGetStepRunnerPlugin`.
   If the plugin isn't enabled for the workspace, the step fails before
   the runner is invoked with a message naming the plugin id and a link
   to the workspace's plugins page.
2. **Tool-loop registry composition** — `LlmToolLoopStepRunner` filters
   plugin-contributed tool registries (`IToolLoopToolRegistry`
   implementations recorded via `AddPluginToolRegistry`) out of the
   union when the contributing plugin isn't enabled. The agent never
   sees the disabled plugin's tools and the runner reports any
   requested-but-unavailable tools by listing the plugins responsible.

Built-in step types and registries (`shell`, `python`,
`WorkspaceToolLoopRegistry`, etc.) aren't recorded in the contributions
map and pass through unconditionally — the gate only fires for
plugin-contributed extension points.

## Docker patterns

### Operator extending the Creuser image

The standard production deployment shape — your operator builds a
custom image with your plugins baked in:

```dockerfile
# Dockerfile.creuser-with-plugins
FROM ghcr.io/mjczone/creuser:0.1.0

# Plugins are loaded from /data/plugins at startup. Either copy
# already-built plugin folders in directly...
COPY ./plugins/ /data/plugins/

# ...or build them in a multi-stage build first:
# (Stage shown for reference; uncomment if you have plugin sources.)
#
# FROM mcr.microsoft.com/dotnet/sdk:10.0 AS plugin-build
# WORKDIR /src
# COPY ./plugins-src/ ./
# RUN dotnet publish MyPlugin/MyPlugin.csproj -c Release -o /out
#
# FROM ghcr.io/mjczone/creuser:0.1.0
# COPY --from=plugin-build /out /data/plugins/myplugin
```

Then `docker compose` against your image instead of the upstream
`creuser` image. `/data/plugins/` is owned by the image; if you also
mount a volume at `/data`, the volume's `plugins/` subdirectory takes
precedence (operators can swap plugins live by editing the volume).

### Dev compose with the example plugin

For local development, build the example plugin into the repo's
`.data/plugins/` directory (gitignored):

```bash
npm run build:plugins:examples
```

The dev host (`dotnet watch --project src/Creuser.Web`) reads from
`.data/` so the plugin appears immediately on next startup.

## Testing your plugin

Follow the same pattern as Creuser's own integration tests:

- **Unit tests** for your step runners / tool registries / capability
  providers — these don't need the plugin loader at all. Mock
  `StepContext` / `IPluginContext` and call your code directly. See
  `tests/Creuser.Scripting.Tests/` for the style.
- **Integration tests** that exercise the loader — stage your built
  plugin into a temp data dir, point `CREUSER_DATA_DIR` at it, boot a
  test host (or use `WebApplicationFactory<Program>` if your plugin
  ships within the Creuser repo), and assert the plugin appears in
  `cr.plugins` and its contributions execute. See
  `tests/Creuser.Integration.Tests/PluginLoaderIntegrationTests.cs`
  for the canonical pattern.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Plugin not appearing in the SPA | Folder name mismatch — `<dataDir>/plugins/<plugin-id>/<plugin-id>.dll` is the canonical layout. Check the host's startup logs for a "Plugin loader" line. |
| Status `failed` with "no public class implements IPluginRegistration" | The build output didn't include your registration class. Check `IsPackable`/`PrivateAssets` in your csproj — sometimes overly aggressive private-asset settings strip your own class. |
| Status `failed` with "Could not load file or assembly" | Your plugin has a transitive dep the loader can't resolve. Make sure you're publishing (not just building) so the `deps.json` is generated alongside your DLL. |
| Step type is "unknown" at runtime | Your registration ran but the step type key in `AddKeyedScoped<IStepRunner>` didn't match the YAML's `type:` value. Both are case-sensitive. |
| Plugin works locally but fails on the deployment | Check host version compatibility — `MinimumHostVersion` rejects plugins compiled against a newer abstractions package than the deployment ships. |

Inspect the plugin status page (`/w/:slug/settings/plugins`) for the
`StatusMessage` field — it's the loader's error report verbatim.

## Versioning + compatibility

The `Creuser.Plugins.Abstractions` package follows semver. Plugins
target a specific version range; the host's loader rejects plugins
declaring `MinimumHostVersion` higher than the deployment's actual
version.

Within a major version, the abstractions surface is append-only — new
manifest fields, new `IPluginContext` properties, new helpers — so
plugins written today continue to work against future hosts. Major
version bumps are rare and well-flagged in CHANGELOG.

## Per-workspace plugin settings

A plugin that needs configuration (webhook URLs, default repos, tuning
knobs) stores it in `cr.workspace_plugin_settings` keyed on
`(workspace_id, plugin_id)`. The shape is JSON; the plugin author owns
the schema.

**Read settings** in your runner via `IPluginSettingsStore`:

```csharp
public sealed class MyStepRunner : IStepRunner
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly IPluginSettingsStore _settings;

    public async Task<StepResult> ExecuteAsync(StepContext ctx, IReadOnlyDictionary<string, object?> inputs, CancellationToken ct)
    {
        var json = await _settings.GetAsync(ctx.WorkspaceId, MyPlugin.PluginId, ct);
        var s = JsonSerializer.Deserialize<MySettings>(json ?? "{}", Web) ?? new MySettings();
        // use s.MyOption, etc.
    }
}
```

**Write settings** via the workspaces API. Admin-only:

```http
PUT  /api/workspaces/{slug}/plugins/{pluginId}/settings
GET  /api/workspaces/{slug}/plugins/{pluginId}/settings
DELETE /api/workspaces/{slug}/plugins/{pluginId}/settings   # reset to defaults
```

`PUT` body shape: `{ "settings": <object> }`. The host validates the
payload parses as JSON; the plugin's settings record validates the
shape on read.

**Secrets do not belong in settings JSON.** Settings store the
*filename* of a secret (e.g. `"webhookSecretName": "slack-prod.url"`);
the value lives in `<dataDir>/secrets/<name>` and is read via
`ISecretsReader.ReadAsync(name, ct)`. This keeps secret values out of
the queryable database entirely. The convention scales: a single plugin
can reference multiple secrets by adding more `*SecretName` properties
to its settings record.

## Examples

The repo ships canonical examples under `src/plugins/`:

- **`Creuser.Plugins.Examples.Hello`** — smallest possible plugin. One
  step runner, no external dependencies, no settings. Read this first
  to learn the registration shape.
- **`Creuser.Plugins.Examples.Slack`** — step runner that talks to an
  external service: contributes `slack-post` using a webhook stored as
  a secret. Demonstrates `AddPluginStepRunner`, per-workspace settings,
  `ISecretsReader`, and `IHttpClientFactory` — the canonical recipe
  when integrating with anything outside the host process.
- **`Creuser.Plugins.Examples.GitHubTools`** — registry that contributes
  three tools to the agentic `llm-tool-loop`: `read_pr`, `list_issues`,
  `comment_on_issue`. Demonstrates `AddPluginToolRegistry`, the
  ambient-credential pattern (the LLM never sees the PAT), and
  `AIFunctionFactory` parameter discipline (explicit `= null` defaults
  on optional args).

The integration tests under `tests/Creuser.Integration.Tests/` exercise
each example end-to-end with stub HTTP handlers — a useful starting
point when writing tests for your own plugins.
