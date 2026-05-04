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
| **Step runners** | Implementations of `Creuser.Core.Execution.IStepRunner` registered as keyed services on their step type | `Examples.Hello` contributes `hello-world`; future `Examples.Slack` will contribute `slack-post` |
| **Capability providers** | Implementations of `Creuser.Web.Agents.Capabilities.ICapabilityProvider` so the in-app assistant learns about plugin-supplied features | A plugin shipping settings pages registers them so users can ask "where do I configure X?" |
| **Tool registries (agentic)** | Implementations of `Creuser.Scripting.ToolLoop.IToolLoopToolRegistry` so `llm-tool-loop` agents get new tools | Future `Examples.GitHubTools` will contribute `read_pr`, `list_issues`, `comment_on_issue` |
| **Convention types (post-v1)** | Custom convention loaders for the projection layer | Parse atlas SQL, GitHub workflow files, Linear issue templates into entity-graph rows |

Plugins use the standard `Microsoft.Extensions.DependencyInjection`
patterns to register their contributions. Anything the host's DI can
construct is fair game.

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
        services.AddKeyedScoped<IStepRunner, HelloWorldStepRunner>("hello-world");
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

> **v1 note**: enablement is a UI signal — runtime enforcement (gating
> step-runner resolution and capability listings on the workspace
> enablement flag) is a v0.2 follow-up. v1 plugins-page state determines
> what shows up in pickers but doesn't yet block direct invocation
> (e.g. typing `type: hello-world` in a job script works regardless of
> the toggle). Workspaces aren't a security boundary in single-tenant
> on-prem; the toggle is a curation aid.

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

## Examples

The repo ships canonical examples under `src/plugins/`:

- **`Creuser.Plugins.Examples.Hello`** — smallest possible plugin. One
  step runner, no external dependencies. Read this first.

Future examples (separate slices, Q3 2026):

- **`Creuser.Plugins.Examples.Slack`** — step runner with external
  service integration: contributes `slack-post` step using a
  per-workspace webhook. Demonstrates secret handling.
- **`Creuser.Plugins.Examples.GitHubTools`** — `IToolLoopToolRegistry`
  contribution: adds `read_pr` / `list_issues` / `comment_on_issue`
  tools agents can use in `llm-tool-loop` steps.

When those land, this guide will gain walk-throughs covering the
additional patterns (settings, secrets, external HTTP calls, agent
tooling).
