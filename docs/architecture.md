# Creuser Architecture

> **Status:** Pre-release. Architecture document for v0.1.0 and the path to v1.0.
> **Last updated:** 2026-05-02
> **Authors:** Matt Cowan (MJCZone Inc.)

## What Creuser is

Creuser is an open-source, on-premise platform for orchestrating workflows, jobs, and AI agents that operate over a git monorepo or S3-backed file system. It provides a deterministic-and-agentic workflow engine, a database-as-projection-of-record over the underlying repository, a dashboard composer for dense operational UIs, and a pluggable surface for organizations to build domain-specific intelligence on top.

The name is French for *"to dig"* — pronounced "KROO-ZAY" in English or "kruh-ZAY" in French. The metaphor is excavation: the platform digs into a codebase to surface structure that's already buried in YAML, markdown, scripts, and source files, making it queryable and operable.

Creuser is the platform. The IP boundary is strict: Creuser exists only to help manage structure of an existing monorepo or file-system.

## Design principles

**Generic first, opinionated second.** Creuser core has no domain-specific tables, UI, or job types. All domain knowledge enters through configuration, plugins, and runtime registration. The test: can an organization use Creuser for their specific use case (data pipelines, infrastructure-as-code, content moderation, business rule and ai context management) without modifying core? If yes, the abstraction is correct.

**Single image, single command.** The deployment story is `docker compose up`. Three services (Creuser, Postgres, Redis), two connection-string environment variables (Postgres and Redis), one persistent volume mounted at `/data`. Everything else — secrets, branding, plugins, scripts — lives on the volume and is configured in-app.

**On-premise, single-tenant.** Each organization runs its own Creuser instance. No multi-tenancy in the core. This simplifies the security model, the data model, and the operational story.

**Database is projection-of-record.** Git (or S3) is the source-of-record for the *content*. Postgres is the source-of-record for *everything else* and the queryable projection of content. Workflows and agents query Postgres for discovery; they touch the working tree only when they need to read or mutate specific files.

**Workspaces own branches.** A Creuser-managed git workspace operates on a dedicated branch (default `creuser/main`, configurable per-workspace). Creuser does not produce many small PRs against the main development branch — it produces sequential commits on its own branch, which a senior developer or analyst merges on a cadence they control.

**White-labelable from day one.** Branding (name, logo, colors, copy) is in-app configuration, not environment variables. Creuser deployments can rebrand to any desired branding and name without rebuilding the image.

## What ships in v0.1 (status snapshot)

The architecture below describes both the v1 destination and the current state. To keep readers oriented:

**Shipped:**

- Authentication (cookie sessions, Argon2id, bootstrap admin, force-password-change), `Creuser.Auth.*` projects with the `IAuthProvider` seam. Local provider; Google provider stub.
- Admin Users page (invite, reset password, promote/demote, deactivate, delete) with last-admin / self-action guards.
- White-labeling: branding doc with palette presets, runtime CSS variable injection, content-addressed asset uploads, theme mode (dark/light/auto), bundled variable fonts.
- Environment configuration page with `SecretsService` (`/data/secrets/<filename>`, chmod 600, value-never-returned). Per-provider "Test connection" hits a sub-cent health probe.
- `Microsoft.Extensions.AI` 10.3.0 wired with Anthropic, OpenAI, and Local providers; `AgentClientFactory` + `AgentClientResolver` with structured `ResolveOutcome`.
- In-app AI assistant: right-side chat panel, `POST /api/agents/chat`, capability registry (`ICapabilityProvider` + `CoreCapabilityProvider` + `EndpointAttributeProvider` from `[AiCapability]`-decorated methods), `navigate` / `describe_capabilities` tools, role-filtered, deep-link rendering with `?expand=` deep-open semantics.
- **Workspaces foundation**: `cr.workspaces` with type discriminator (git/local; s3 reserved), JSONB settings deserialized to typed records, settings CRUD, test-connection (HTTPS smart-HTTP probe + real SSH `git ls-remote`), sync (init / fetch source / try-fetch working / `checkout -B` / `reset --hard` / `clean -fd`), per-slug `SemaphoreSlim` concurrency, dirty-state detection with `?force=true` confirmation flow, sync state columns on the table, last-sync UI in the workspaces list.
- **Per-workspace plugin enablement UI** (first pass): `/w/:slug/settings/plugins` renders the empty-state inventory; `cr.workspace_plugins` join table + persistence land when the plugin loader populates `cr.plugins`.
- **Execution model — full deterministic catalog**: `IStepRunner` contract + `JobExecutor` (in-process, synchronous); 8 registered runners (`llm-chat`, `shell`, `csharp`, `python`, `node`, `file-mutate`, `file-frontmatter`, `http`); multi-step DAGs with `DagValidator` (Kahn topological sort) + `StepBindingResolver` (`$step_id.field` / `$params.name` resolution); `IWorkspaceWorkingTree.ApplyAndCommitAsync` with one-commit-per-step structured commit messages; `JobRun.StartCommitSha` / `EndCommitSha`; sha256 hashes on every `FileChange`; `IToolCatalog` + chip-picker for `shell` allow-lists; `LlmCacheStore` keyed by sha256(provider + model + prompt + system + temperature + format); cancellation propagation from failed upstreams.
- **Schedules + triggers**: `cr.schedules` with cron (NCrontab, UTC, 5- or 6-field) and sync-hook kinds; `SchedulerService` background tick (configurable interval); `IJobScheduleDispatcher` shared by tick / sync hook / manual fire; `JobRun.TriggerKind` records cron/sync/manual; SPA Schedules page under workspace settings.
- **Agentic step type — `llm-tool-loop`**: bounded ReAct runner driven hand-rolled (not via `UseFunctionInvocation()`); per-turn token accounting, explicit `max_steps` / `max_tokens` / `max_duration_seconds` budgets, per-call audit recorded in `tool_log.json`, transcripts in `transcript.json`. `IToolLoopToolRegistry` is the extension seam (DI multi-binding); v1 ships `WorkspaceToolLoopRegistry` with read-only `read_file` / `list_directory` / `grep` / `find_files_by_pattern` / `git_log`. `IChatClientResolver.ResolveRawAsync` returns the no-middleware client the runner drives. Returns `FileChanges: []` — mutations land in downstream `file-mutate` / `file-frontmatter` steps consuming the loop's `final_text` / `final_json`.
- **Workspace projection layer**: `Creuser.Projections` project + `cr.entities` / `cr.entity_refs` tables form a typed knowledge graph over the working tree. **Conventions** declared per-workspace in `.creuser/conventions/*.yaml` describe how directory patterns + frontmatter map to entity kinds, with slug derivation, metadata extraction, relationship resolution, and validation rules. `extends:` merges from a bundled `creuser:standard/*` library (markdown-doc, adr, rfc, skill, migration-sql, business-rule). Full-rebuild semantics on every successful workspace sync (fire-and-forget continuation) and via the explicit `projection-sync` step runner. Conflict resolution: priority desc → glob specificity desc → id asc. Refs that don't resolve persist with `to_entity_id = null` + raw target preserved — that's the gap-finding signal. **`ProjectionToolLoopRegistry`** composes alongside `WorkspaceToolLoopRegistry` to give agents `query_entities` / `get_entity` / `find_orphans` / `find_unresolved_refs` / `find_references` / `list_kinds` — graph queries instead of grep. JSONB metadata + GIN index in v1 keeps the storage forward-compatible with matrix views (see `docs/wip/projections-design.md` for the v0.2 direction). Endpoints: `GET /api/workspaces/{slug}/conventions/`, `GET /api/workspaces/{slug}/entities/`, `GET /api/workspaces/{slug}/entities/{kind}/{slug}`, `POST /api/workspaces/{slug}/projections/sync`.
- SignalR notifications hub (`/hub/notifications`) with `Subscribe` / `Unsubscribe` / `Broadcast`; branding store subscribes to live updates.
- Reusable SPA components: `StatusBanner`, `CollapsibleSection`, `SecretInput`, with mode-aware `--cr-link` tokens and themed scrollbars.

**In flight (next up for v0.1.x):**

- `llm-planner` + plan-then-execute pattern (item 10) — explicit planner emits a structured `JobPlan` against the registered step types; plan persisted; execution is durable saga.
- Matrix views + KPI dashboards on top of the entity projection (item 13's natural sibling, design captured in `docs/wip/projections-design.md` "Forward-looking").

**Deferred (post-v0.1):**

- Marten + Wolverine durable saga executor — replaces the in-process `JobExecutor`. The `IStepRunner` contract was designed so this is wiring-only, not a runner rewrite. The `mt` Postgres schema is reserved.
- Plugin loader (`/data/plugins/*.dll` discovery + manifest parsing) — lights up workspace plugin contributions and unlocks stage 3 of the capability registry.
- Dashboard composer (`dockview-vue`, widget registry, Monaco-based job editor).
- SMTP-driven flows (invite-by-email, full forgot-password); release CI workflows; Dockerfile finalization.
- libgit2sharp for in-process git ops; the shell-out path stays for ops that need the porcelain.

The detail sections below mark each piece accordingly.

## Stack

**Backend (currently in the codebase)**

- .NET 10
- ASP.NET Core minimal APIs
- ASP.NET Core SignalR (`/hub/notifications`; real-time push for dashboards / status banners)
- MJCZone.DapperMatic (DDL abstraction + idempotent migrations for the `cr.*` tables; DML auto-mapping for Dapper)
- Dapper (parameterized SQL for repository implementations)
- Microsoft.Extensions.AI 10.3.0 (LLM abstraction, pinned to match Anthropic.SDK 5.10.0)
- Anthropic.SDK 5.10.0 (Anthropic provider) + Microsoft.Extensions.AI.OpenAI (OpenAI / Azure OpenAI / OpenAI-compatible local providers — Ollama, LM Studio, vLLM)
- FluentValidation (request validation at the endpoint layer)
- Konscious.Security.Cryptography.Argon2 (password hashing)
- Microsoft.AspNetCore.OpenApi (native OpenAPI 3.1 generation)
- Scalar (interactive API docs at `/scalar`)
- Serilog (structured logging)
- CSharpier (formatter; pinned via `dotnet-tools.json`, enforced by pre-commit hook)
- Shell-out to host `git` and `ssh` binaries for workspace clone / fetch / SSH key auth

**Backend (deferred; named here so the eventual landing fits the rest of the stack)**

- Marten — adopted when sagas, run records, and `AgentTrace` documents need event sourcing. The `mt` Postgres schema is reserved.
- Wolverine — durable message dispatch + saga driver, lands with the workflow engine.
- libgit2sharp — in-process git, replacing the shell-out path for the operations that don't need the full porcelain.
- OpenTelemetry — observability.

**Frontend (currently in the codebase)**

- Quasar 2 (Vue 3 Composition API)
- TypeScript strict mode
- Vite (via Quasar CLI in SPA mode)
- Pinia (state management; user, branding, theme-mode, assistant-history stores)
- @hey-api/openapi-ts (TypeScript client generation from OpenAPI)
- @microsoft/signalr (SignalR JS client; subscribed today for branding live updates)
- Fontsource variable fonts (Inter, IBM Plex Sans, Source Sans 3, JetBrains Mono, Fira Code, Source Code Pro), code-split per-font
- Vitest + @vue/test-utils + jsdom (SPA unit tests; project at `tests/Creuser.Web.Spa.Tests/`)
- husky + lint-staged (pre-commit: CSharpier on staged `*.cs`)
- Node 24 LTS, npm only (no pnpm or yarn)

**Frontend (deferred)**

- dockview-vue — dense tiling / docking layout for the dashboard composer.
- Monaco Editor — job script editor.

**Infrastructure**

- Postgres 17 with pgvector extension
- Redis 7 (provisioned in the dev compose file; not yet a runtime dependency in code — reserved for Wolverine / SignalR backplane / cache)
- Docker (single image, multi-stage build)

## Solution layout

```txt
creuser/
├── src/
│   ├── Creuser.Core/                       # Domain primitives, no infra deps
│   │   └── Repositories/                   # Workspace record, IWorkspaceStore,
│   │                                       # GitWorkspaceSettings, LocalWorkspaceSettings
│   ├── Creuser.Persistence/                # DapperMatic table classes + repositories
│   │   ├── DbInitializer.cs                # Idempotent schema bootstrap (cr.* tables, additive ALTERs)
│   │   ├── Tables/                         # Lowercase row classes (users, workspaces, app_settings)
│   │   └── Repositories/                   # Dapper-backed implementations of Core interfaces
│   ├── Creuser.Auth.Abstractions/          # IUserStore, IPasswordHasher, IAuthProvider, User record
│   ├── Creuser.Auth.Core/                  # Argon2id hasher, BootstrapAdminService, cookie helpers
│   ├── Creuser.Auth.Providers.Local/       # Username/email + password (default IAuthProvider)
│   ├── Creuser.Auth.Providers.Google/      # OAuth — stub returning AuthResult.NotSupported
│   ├── Creuser.Agents/                     # AgentClientFactory + provider wiring
│   ├── Creuser.Git/                        # Reserved for libgit2sharp; today empty (workspaces shell out to git)
│   ├── Creuser.Scripting/                  # Reserved for job runners; empty in v0.1 scaffold
│   ├── Creuser.Web/                        # ASP.NET host, serves SPA
│   │   ├── Program.cs                      # DI registrations, middleware, endpoint mapping
│   │   ├── Endpoints/                      # Grouped endpoint extensions (Auth, AdminUsers,
│   │   │                                   #   Workspaces, Branding, Environment, Agents, Ping, Echo)
│   │   ├── Agents/                         # AgentClientResolver, AgentTools (navigate/describe),
│   │   │                                   #   Capabilities/ (ICapabilityProvider + registry)
│   │   ├── Branding/                       # BrandingAssetsService + branding endpoints
│   │   ├── Environment/                    # SecretsService, environment-config endpoints
│   │   ├── Workspaces/                     # WorkspaceFilesystemService (owns <data>/workspaces/<slug>/)
│   │   ├── Hubs/                           # NotificationsHub (SignalR pub/sub)
│   │   ├── Validation/                     # FluentValidation validators
│   │   ├── Contracts/                      # Request / response DTOs (PascalCase, JSON-serialized)
│   │   ├── Problems.cs                     # ProblemDetails factory helpers
│   │   └── wwwroot/                        # SPA build output lands here
│   └── Creuser.Web.Spa/                    # Quasar/Vue/TS app
│       ├── quasar.config.ts
│       ├── package.json
│       ├── tsconfig.json
│       └── src/
│           ├── boot/
│           ├── layouts/
│           ├── pages/                      # Settings shell + login + assistant content
│           ├── components/                 # StatusBanner, CollapsibleSection, SecretInput,
│           │                               #   AssistantPanel, branding/, env/
│           ├── stores/                     # Pinia (auth, branding, themeMode, assistant)
│           ├── composables/                # useT (i18n + branding overrides), useLocalStorage
│           ├── css/                        # theme.scss (--cr-* tokens, palette presets)
│           └── api/                        # Generated TS client (hey-api)
├── tests/
│   ├── Creuser.Core.Tests/
│   ├── Creuser.Integration.Tests/          # WebApplicationFactory + Testcontainers Postgres
│   └── Creuser.Web.Spa.Tests/              # Vitest
├── docker/
│   ├── Dockerfile                          # Multi-stage: SPA → .NET → runtime
│   ├── docker-compose.yml                  # Production-shape compose
│   └── docker-compose.dev.yml              # Local dev (separate SPA dev server)
├── docs/
│   ├── architecture.md                     # This document
│   ├── docker-variants.md                  # `:latest` (fat) vs `:slim` policy
│   ├── job-script-format.md                # Frontmatter spec (forward-looking)
│   ├── widget-development.md               # How to build custom widgets (forward-looking)
│   └── wip/timeline.md                     # Active build plan
├── scripts/
│   └── wire-dev-services.mjs               # Reads dev-compose ephemeral ports → appsettings
├── .github/workflows/                      # ci / edge / release (release wiring TBD)
├── .husky/pre-commit                       # Runs `npx lint-staged` (CSharpier on staged *.cs)
├── README.md                               # Includes LGPL-3.0 license summary
├── LICENSE                                 # LGPL-3.0 full text
├── CONTRIBUTING.md
├── Creuser.slnx                            # XML solution format (.NET 10)
├── dotnet-tools.json                       # Pinned local .NET tools (CSharpier)
├── global.json                             # Pin .NET 10 SDK
├── package.json                            # Root orchestration: build / test / dev / lint / codegen / services
└── package-lock.json
```

`Creuser.Git` and `Creuser.Scripting` are scaffold projects from the original Saturday plan — they exist in the solution but ship no code in v0.1. Workspace git operations currently live in `Creuser.Web/Endpoints/WorkspacesEndpoints.cs` (shell-out to `git` and `ssh`) plus `Creuser.Web/Workspaces/WorkspaceFilesystemService.cs`. They migrate into `Creuser.Git` / `Creuser.Scripting` when the workflow engine lands and the job-runner abstraction needs the seam.

## Data model

### Postgres schemas

Two schemas in one database:

- `cr` — Creuser relational tables, DapperMatic-managed. **Live today.**
- `mt` — Marten-managed document tables (workflows, runs, run steps, agent traces, append-only audit log, anything event-sourced or JSONB-native). **Reserved**; Marten lands with the workflow engine. Singleton platform config (branding, environment) currently lives in `cr.app_settings` and stays there even once Marten lands — it's read-mostly config, not event-sourced state.

The split rule (forward-looking): append-mostly with rich JSONB querying → Marten. Relational with hot reads and explicit indexes → DapperMatic. Don't fight either tool by misusing it for the other's strengths.

### Core tables (DapperMatic-managed, schema `cr`)

See <https://dappermatic.mjczone.com/llms-full.txt>.

**Shipped in v0.1:**

```txt
cr.users                   -- Authentication users (id, email, display_name,
                              password_hash, role, must_change_password,
                              is_active, last_login_at, created_at, updated_at)
cr.workspaces              -- Configured connections to a content source.
                              type discriminator (git/s3/local) + URL-safe slug.
                              settings jsonb (typed per workspace type — see
                              "Workspace abstraction"). Sync state columns
                              (last_sync_at, last_sync_sha, last_sync_status,
                              last_sync_message) drive the UI status chip.
cr.app_settings            -- Singleton platform config (key text PK, value
                              jsonb). Well-known keys: 'branding',
                              'environment'. Secret-backed values reference
                              filenames in /data/secrets/ (never values).
```

**Designed; landing alongside their feature:**

```txt
cr.workspace_members       -- Per-workspace access grants (Editor/Viewer per user)
cr.workspace_plugins       -- Per-workspace plugin enablement (workspace_id, plugin_id, enabled)
cr.entities                -- Generic projection: (id, kind, schema_version, source_ref, data jsonb, projections jsonb)
cr.entity_refs             -- Edges between entities (graph queries for traceability)
cr.job_scripts             -- Frontmatter-parsed scripts (DB is canonical; filesystem is materialized)
cr.workflows               -- Workflow definitions (DB-canonical)
cr.dashboard_groups        -- UI grouping for dashboards in the workspace icon bar
cr.dashboards              -- Saved dockview layouts + widget instances; either standalone (own icon) or grouped
cr.plugins                 -- Registered plugin metadata
cr.user_sessions           -- Active sessions (today: ASP.NET cookie auth + data-protection-keys on disk; revisit if we need server-side revocation)
```

Workspace settings live in the JSONB `cr.workspaces.settings` column rather than a separate `cr.workspace_settings` table — every type discriminator deserializes to a typed C# record (`GitWorkspaceSettings`, `LocalWorkspaceSettings`), keeping the on-disk schema stable while letting type-specific fields evolve without DDL churn. Future workspace types add their own record; the table doesn't change.

### Marten document types (schema `mt`, deferred)

```
WorkflowRun                -- Saga state, event-sourced; canonical run record
RunStep                    -- Individual step execution within a run
AgentTrace                 -- Full LLM conversation + tool-call trace per agentic step
EmailTemplate              -- White-label email content
AuditEvent                 -- Append-only audit log
```

### The `cr.entities` table

This is the load-bearing abstraction. Every parsed YAML rule, every markdown front-matter block, every package.json scripts entry, every SQL DDL parse, every business rule lives here as a row:

```sql
CREATE TABLE cr.entities (
  id            uuid PRIMARY KEY,
  workspace_id  uuid NOT NULL REFERENCES cr.workspaces(id),
  kind          text NOT NULL,            -- e.g. 'yaml_rule', 'sql_table', 'package_json_script'
  schema_version int NOT NULL DEFAULT 1,
  source_ref    jsonb NOT NULL,           -- { path, content_hash, line_start, line_end }
  data          jsonb NOT NULL,           -- Structured representation
  projections   jsonb NOT NULL DEFAULT '{}',  -- Computed derivative fields
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_entities_kind ON cr.entities (workspace_id, kind);
CREATE INDEX idx_entities_data_gin ON cr.entities USING gin (data);
CREATE INDEX idx_entities_source_path ON cr.entities ((source_ref->>'path'));
```

Domain-specific consumers register additional indexes via a configuration mechanism — they never modify this table's structure, just add expression indexes for their query patterns.

`cr.entity_refs` holds the edges:

```sql
CREATE TABLE cr.entity_refs (
  source_id     uuid NOT NULL REFERENCES cr.entities(id) ON DELETE CASCADE,
  target_id     uuid NOT NULL REFERENCES cr.entities(id) ON DELETE CASCADE,
  ref_type      text NOT NULL,            -- e.g. 'references', 'depends_on', 'documents'
  metadata      jsonb NOT NULL DEFAULT '{}',
  PRIMARY KEY (source_id, target_id, ref_type)
);
```

This is what lets domain-specific consumers express cross-entity traceability as a graph query rather than a grep.

## Workspace abstraction

A workspace is a configured connection to a content source. Three types are reserved by the discriminator:

- **`git`** — clone of a remote git repository. **Shipped.**
- **`local`** — pointer to a server-side filesystem path (mounted volume in Docker, any directory in dev/on-host). The simplest backend: read and (optionally) write the directory directly. No clone, no commits, no branches. **Shipped.**
- **`s3`** — reserved for S3-backed workspaces. Disabled in the create UI until the implementation lands.

Each workspace has a **URL-safe slug** (unique, kebab-case, e.g. `acme`, `widgets-platform`) that appears in operator-facing URLs as `/w/:workspaceSlug/...`. Slugs are stable identifiers — the workspace's *display name* can change without breaking bookmarks or in-flight tabs.

Workspaces are a **top-level operator context**, not a feature category. The SPA's primary navigation is workspace-scoped: an operator picks (or lands on) a workspace and the inner navigation (dashboards, runs, scripts, agents, plugins) is rooted under `/w/:slug/`. Multiple browser tabs can hold different workspaces simultaneously — each tab's URL carries its own slug, so there is no in-app "current workspace" global to fight with. Platform-level configuration (`/settings`, `/admin/users`) is unscoped.

The reproducibility invariant: **everything in a workspace's working tree is the output of automation** (jobs, agents, scheduled rules), not hand-authored source. Anything that lives there should be reproducible by re-running the job that produced it. This is why destructive sync (see below) is architecturally safe — it doesn't lose work, it just re-mirrors the canonical state.

### Domain model (current)

```csharp
public sealed record Workspace(
    Guid Id, string Slug, string Name, string? Description,
    string Type,                        // "git" | "local" | "s3"
    string Settings,                    // JSON, deserialized per Type to a typed record
    DateTime CreatedAt, DateTime UpdatedAt, Guid? CreatedBy,
    DateTime? LastSyncAt = null,
    string? LastSyncSha = null,
    string? LastSyncStatus = null,      // "ok" | "failed" | null (never synced)
    string? LastSyncMessage = null
);

public interface IWorkspaceStore
{
    Task<Workspace?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<Workspace?> FindBySlugAsync(string slug, CancellationToken ct = default);
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListAsync(int skip, int take, CancellationToken ct = default);
    Task SaveAsync(Workspace workspace, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task UpdateSyncStatusAsync(Guid id, DateTime syncedAt, string status,
        string? sha, string? message, CancellationToken ct = default);
}
```

The `IRepositoryWorkspace` / `IWritableWorkspace` read/write interfaces are deferred — they land with the job runner, which is the first consumer that actually needs to read or mutate working-tree contents.

### Git workspace settings

```csharp
public sealed record GitWorkspaceSettings(
    string RepositoryUrl,
    string AuthMode = "none",          // "none" | "https-pat" | "ssh-key"
    string? AuthSecret = null,         // filename under /data/secrets/, e.g. workspace-<slug>.pat
    string WorkingBranch = "creuser/main",
    string SourceBranch = "main",
    string Mode = "direct-push",       // "direct-push" | "pull-request"
    string PushFrequency = "every-commit"  // | "batched"
);
```

- **Repository URL** — HTTPS or SSH. The auth mode is selected per workspace, not inferred from URL scheme, so admins can use SSH-form URLs with no key (public mirrors) or HTTPS with PAT (private repos).
- **Auth modes:** `none` (public), `https-pat` (HTTP Basic with username `git` + the PAT — works for GitHub, GitLab, Bitbucket, Azure DevOps, Gitea), `ssh-key` (OpenSSH-format private key). Credentials are written through `SecretsService` to `/data/secrets/workspace-<slug>.{pat,key}` (chmod 600), never stored in the DB. The DB stores only the filename in `AuthSecret`.
- **Working branch** — branch the platform commits to (e.g. `creuser/main`, `acme/development`). Created locally on first sync if it doesn't yet exist on the remote (sync auto-falls-back to source branch).
- **Source branch** — branch the working branch is rebased / pulled from when admins want fresh source content.
- **Mode** — direct push (default) or pull-request (deferred to when the PR producer lands).
- **Push frequency** — every-commit (real-time) or batched. Forward-looking; honoured once the commit/push side ships.

### Local workspace settings

```csharp
public sealed record LocalWorkspaceSettings(
    string Path,        // absolute filesystem path; must exist when saving
    bool Writable = true
);
```

There is no path-allowlist in v1 — single-tenant on-premise + admin-only management means the trust boundary is the admin's own discretion. Multi-tenant deployments (post-v1) would need a path-prefix constraint here.

### Test connection

Before saving (or after rotating credentials), admins can click **Test connection**. The test exercises the same code path the eventual sync runs — what passes here is what'll work in production.

- **HTTPS git** — smart-HTTP `GET <url>/info/refs?service=git-upload-pack` with `Authorization: Basic <base64(git:<pat>)>` when a PAT is supplied. 10-second timeout. Status codes are translated to actionable copy ("PAT may be expired or lack `repo` scope" for 401/403, "Repository not found" for 404).
- **SSH git** — writes the inline private key to a chmod-600 temp file, sets `GIT_SSH_COMMAND` (`-i <key> -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=10 -o LogLevel=ERROR`), runs `git ls-remote --exit-code <url> HEAD`, parses stderr for known patterns (Permission denied, host unreachable, repo not found, key parse errors). The `LogLevel=ERROR` flag suppresses the "Permanently added 'host' to known_hosts" warning that would otherwise leak into the error parser.
- **Local** — confirms the path exists, is a directory, is readable; if `Writable` is set, also verifies write access by creating + deleting a probe file. Latency is reported as 0ms (no network).

### Sync (`POST /api/workspaces/{slug}/sync[?force=true]`)

For git workspaces, the sync handler exercises one unified flow that handles both the first clone and subsequent updates:

1. `git init --quiet` (if working tree doesn't exist yet) and `git remote add origin <url>`, otherwise `git remote set-url origin <url>` to honour edits to the workspace's URL.
2. `git fetch --depth 1 origin <sourceBranch>` (must succeed — if it doesn't, the auth/URL/branch is wrong).
3. `git fetch --depth 1 origin <workingBranch>` (best-effort — failure means the working branch is local-only, still pre-first-push).
4. `git status --porcelain` to count uncommitted changes. If non-zero **and** the request didn't include `force=true`, the server returns `RequiresForce=true` + the dirty count and writes nothing. The SPA shows a "Discard N changes?" confirmation dialog and retries with `?force=true`.
5. `git checkout -B <workingBranch> <target>` where `target` is `origin/<workingBranch>` if the fetch in (3) succeeded, otherwise `origin/<sourceBranch>` (so a fresh workspace is in lockstep with source until the first commit produces the working branch upstream).
6. `git reset --hard <target>` and `git clean -fd`. The combined effect is byte-for-byte mirror of the remote target — destructive by design, since the working tree is reproducible job output. We deliberately omit `-x`, leaving gitignored scratch files alone.
7. Resolve `git rev-parse HEAD` and persist the result in the `last_sync_*` columns.

For local workspaces, sync is a path heartbeat: re-verify the directory exists, refresh `last_sync_at`. There's no remote to pull from — the working tree IS the source content, mediated only by the `Writable` flag.

The full git porcelain isn't in scope for v1: `status`, `fetch`, `init`, `clone`, `remote add/set-url`, `checkout`, `reset`, `clean`, `rev-parse`, `add`, `commit`, `push` cover everything currently planned. No rebase, no merge-with-conflicts, no history rewriting. libgit2sharp will replace the shell-out path for the operations that don't need full porcelain (read-side ops, blame, log) once it's wired.

### Concurrency

Per-slug `SemaphoreSlim` in `WorkspacesEndpoints._syncLocks` so concurrent sync requests for the same workspace serialize, but different slugs run in parallel. **In-memory only — multi-instance deployments need a Postgres advisory lock here.** Single-tenant on-prem v1 is fine. The advisory lock plus libgit2 thread-safety guards land alongside the workflow engine, when multiple sagas may step against the same workspace.

### Working tree layout

```txt
<dataDir>/workspaces/<slug>/
```

Owned by `WorkspaceFilesystemService`. Created on first sync, removed when the workspace is deleted. Operators don't reach in directly — admin actions go through sync / job runs.

### Commit batching (forward-looking)

When the workflow engine lands, a saga step claims a workspace, gets a working tree at HEAD of the working branch, makes N file mutations, and produces exactly one commit per step with a structured message:

```txt
[creuser] <step.name> (run=<run_id> step=<step_id>)

<human_summary>

Updated:
- src/foo.md
- docs/index.md
```

Multiple steps in a workflow each produce their own commit. The sequential commit history on the working branch IS the audit log of what the platform did. `git log creuser/main` is itself a useful debugging tool.

## Execution model

The execution model is the platform's core. It needs to flexibly chain *deterministic computation* (shell scripts, SQL, HTTP, file mutations) with *probabilistic computation* (LLM completions, tool loops, planners) while staying **inspectable, idempotent, replayable, and auditable**. Every claim the platform makes about a workspace must be traceable to a concrete sequence of step executions whose inputs, outputs, and side effects are recorded.

This section is the architectural contract for everything below "what does the platform actually do." Job script CRUD, the runner registry, scheduling, durable orchestration, and the SPA-side run viewer are all surfaces over this model.

### Vocabulary

```
Step       — atomic unit of work. Single shell command, single LLM call,
             single batch of file mutations. Inputs typed, outputs typed,
             side effects bounded.
StepRunner — the .NET implementation of a step type. Registered in DI.
             Plugins contribute additional StepRunners.
Job        — a recipe that composes steps into a DAG (or a single step).
             Stored as a JobScript with YAML frontmatter + a body.
Run        — one execution of a Job with concrete inputs, producing a
             commit (when the Job touches the working tree), step
             artifacts, and an audit trail.
Schedule   — an entry in cr.schedules that triggers a Job on cron, on
             workspace sync, on git push, or on demand.
Plan       — for plan-then-execute Jobs: the structured DAG emitted by an
             llm-planner step that the rest of the Run executes.
```

The hierarchy is intentional: **Jobs compose Steps, Runs execute Jobs, Schedules trigger Runs**. There is no other axis. Workflows in the original architecture sketch were a separate concept; in the actual implementation they collapse into Jobs that happen to have multi-step DAGs.

### Three execution patterns

Every Job follows one of three patterns. The Run record carries which pattern was used so the audit UI can render appropriately.

**1. Deterministic DAG** — every step is fixed at design time. Inputs flow between steps via declared bindings. The DAG is computed before execution starts; the runner walks it topologically. No LLM in the planning path.

```
[fetch_articles] → [parse_metadata] → [generate_index] → [commit]
```

This is the bread-and-butter shape: scheduled maintenance jobs, ingestion pipelines, batch transforms.

**2. Plan-then-execute** — the first step is an `llm-planner` that emits a structured `JobPlan` (a list of step descriptors against the *registered* step types). The plan is persisted, then the rest of the Run executes the plan as if it were a deterministic DAG. The planner can only emit plans against types it knows about — the registry is the constraint.

```
[plan(goal: "audit business rules")] →
  emits {steps: [
    {type: "grep", args: {pattern: "@BusinessRule"}},
    {type: "llm-chat", args: {prompt: "summarize findings", input: $1.matches}},
    {type: "file-mutate", args: {path: "audit.md", op: "create", content: $2.summary}}
  ]} →
[grep] → [llm-chat] → [file-mutate]
```

The plan is immutable once persisted — replay takes the same plan, replays it deterministically (or with cached LLM responses).

**3. Agentic** — an `llm-tool-loop` step is given a goal + a tool registry + budgets (max steps, max tokens). The loop iterates until the agent declares done or hits a budget. Each tool call is a recorded event. The "DAG" is post-hoc: it's whatever the agent did, not what we expected.

Even the agentic pattern is bounded: tools have allow-lists, budgets are enforced, every tool call is logged, and file mutations are batched (see *File mutation discipline*) so the agent can't sneak past the audit boundary.

The user — and the planner — choose which pattern to use *up front*. A Job declares its pattern in frontmatter. We don't try to detect it at runtime; that ambiguity costs inspectability.

### The IStepRunner contract

Lives in `Creuser.Core` so step type plugins don't need to depend on the web host:

```csharp
public interface IStepRunner
{
    /// <summary>Type discriminator — "shell", "llm-chat", "csharp", etc.</summary>
    string StepType { get; }

    /// <summary>JSON Schema for this step's inputs (frontmatter `parameters`).</summary>
    StepInputSchema InputSchema { get; }

    /// <summary>JSON Schema for this step's outputs (referenced by downstream step bindings).</summary>
    StepOutputSchema OutputSchema { get; }

    /// <summary>Execute. Cancellation respected; budgets enforced by the host.</summary>
    Task<StepResult> ExecuteAsync(
        StepContext ctx,
        StepInputs inputs,
        CancellationToken ct);
}

public sealed record StepContext(
    Guid RunId,
    Guid WorkspaceId,
    string WorkspaceSlug,
    string WorkingTreePath,         // <dataDir>/workspaces/<slug>/
    Guid StepId,                    // unique within this run
    string StepName,
    IServiceProvider Services,      // host services: SecretsService, AgentClientResolver, ...
    StepBudgets Budgets,
    ILogger Logger
);

public sealed record StepResult(
    StepStatus Status,              // Succeeded | Failed | Skipped | Paused
    IReadOnlyDictionary<string, object?> Outputs,
    IReadOnlyList<FileChange> FileChanges,    // batched into the Run's commit
    IReadOnlyList<StepArtifact> Artifacts,    // logs, transcripts, generated files
    long DurationMs,
    long? TokensUsed,                          // LLM steps only
    decimal? CostUsd,                          // LLM steps only
    string? ErrorMessage,
    string? ResumeToken                        // for steps that pause + resume
);

public sealed record FileChange(
    string Path,                    // relative to workspace working tree
    FileChangeOp Op,                // Create | Modify | Delete | Rename
    string? RenameTo,
    string? BeforeHash,             // sha256 of prior content; null for Create
    string? AfterHash,               // sha256 of new content; null for Delete
    string? Diff                     // unified diff text for Modify
);

public sealed record StepArtifact(
    string Kind,                    // "stdout" | "stderr" | "transcript" | "generated-file" | ...
    string Path,                    // <dataDir>/runs/<runId>/<stepId>/<kind>/<filename>
    long Bytes,
    string? ContentType
);
```

Three properties of this contract worth calling out:

- **No direct file writes.** A step doesn't touch the working tree directly — it returns `FileChange[]` and the Run's executor applies them transactionally at step end (see *File mutation discipline*). This guarantees that a step that fails halfway leaves no partial mutation.
- **Budgets are enforced by the host, not the step.** Step runners don't need to count tokens or check timeouts — the executor wraps execution with budget enforcement. Steps that respect cancellation tokens get clean shutdowns; ones that don't get killed.
- **`ResumeToken` enables pause + resume.** A step that needs to wait (rate-limit cooldown, human approval, long-running external job) sets `Status = Paused` + a `ResumeToken`. The executor persists the token and schedules a wake-up; resumption calls `ExecuteAsync` again with the prior `ResumeToken` available via `StepContext`.

### Step type catalog

Initial set, with implementation order. Every entry is a registered `IStepRunner`; plugins add more.

| Type | Pattern | Description | Status |
|---|---|---|---|
| `llm-chat` | LLM | Single prompt → structured output (JSON Schema-validated) or free text. Cached by `(prompt-hash, model, temperature, response-format-hash)`. | **Shipped** |
| `shell` | Deterministic | Run a shell command with a per-job allow-list. Stdout/stderr captured as artifacts. Working dir = workspace tree. Allow-list checked before any process spawns; jobs without `allowed_commands` declared reject every shell step. The Jobs editor's allow-list picker is sourced from `GET /api/tools` (`IToolCatalog`), which returns the curated baseline palette + plugin-contributed tools. | **Shipped** |
| `csharp` | Deterministic | Single-file C# script via `dotnet run script.cs`. Honors `#:package` / `#:property` frontmatter directives. Source materialized to `${TMPDIR}/creuser-csharp-<runId>-<stepId>/script.cs`; cleanup is best-effort in finally. The `CREUSER_WORKING_TREE` env var is the reliable path-anchor — file-based apps may compile into intermediate build dirs, so scripts shouldn't rely on `Directory.GetCurrentDirectory()`. Allow-list semantics differ from `shell` because a .NET script can invoke arbitrary APIs; the security boundary is process-level (restricted env, bounded timeout), with real sandboxing reserved for post-v1 multi-tenant deployments. | **Shipped** |
| `node` | Deterministic | Node.js script via `node script.js`. Restricted env (no inherited host secrets), 5-min default timeout, source captured as artifact for replay. v0.1 uses bare `node`; npm packages live in the workspace's `package.json` + `node_modules`; future `--deps` for inline `npx` resolution. The `:latest` fat image includes Node 24 LTS; the `:slim` image does not. | **Shipped** |
| `python` | Deterministic | Python script via `uv run` (PEP 723 inline deps via `# /// script` headers; behaves as plain `python3` without the header). Source captured as artifact. The `:latest` fat image includes uv + Python 3.13; for `:slim`, install uv or shell-out to `python3`. | **Shipped** |
| `http` | Deterministic | HTTP request → response body parsed. Inputs: `url`, `method` (default GET), `headers`, `query`, `body` (string or object), `body_type` (json / form / text), `timeout_seconds` (default 30), `follow_redirects` (default true), `parse` (auto / json / text / none), `expected_status` (override the 2xx default). Outputs: `status`, `headers`, `body` (capped at 256 KB inline; full bytes always in the `response.body` artifact), `body_truncated`, `parsed`, `latency_ms`, `content_type`, `url`. Built on `IHttpClientFactory` for socket / DNS lifecycle; SPA-shaped User-Agent. Caching is the next pass — wire shape is forward-compatible. SSRF posture: trust the operator (single-tenant on-prem); multi-tenant deployments need pre-flight IP allow/deny. | **Shipped** |
| `sql` | Deterministic | Parameterized SQL against a configured connection. Reads only by default; writes opt-in. | Later |
| `git` | Deterministic | Direct git ops on the workspace working tree (`log`, `diff`, `blame`, `show`, …). Read-only — mutations go through `file-mutate`. | Later |
| `file-mutate` | Deterministic | Declarative file ops: `create`, `modify`, `delete`, `rename`. Returns `FileChange[]` without touching disk; the executor's `IWorkspaceWorkingTree.ApplyAndCommitAsync` is the only path that writes — for git workspaces it stages + commits per step (one commit per step, structured commit message); for local workspaces it writes directly. Path-escape protection. Sha256 before/after hashes recorded for audit. The `modify-patch` (unified diff) variant is intentionally deferred — full content replacement is the natural fit for LLM-generated mutations, and surgical refactors land via the post-v1 `code-edit` runner. | **Shipped** |
| `file-frontmatter` | Deterministic | Add / update / remove YAML frontmatter on files of many types via the per-language dialect system (Markdown bare, C-style block-comment, Hash line-comment, HTML comment, SQL dash-comment). Auto-detects from extension; preserves shebangs on `.py` / `.sh`. Ops: `set` (merge), `unset` (remove keys), `replace` (overwrite block). Returns `FileChange[]` like `file-mutate`; the executor commits transactionally. The keystone of the metadata-driven index → code-gen workflow (see "Frontmatter as cross-file metadata"). | **Shipped** |
| `code-edit` | Deterministic | AST-aware edits via tree-sitter or `ast-grep`. Surgical refactors that preserve formatting. | Later |
| `llm-tool-loop` | Agentic | Bounded ReAct loop driven hand-rolled (not via `UseFunctionInvocation()`) so per-turn token accounting, `max_steps` / `max_tokens` / `max_duration_seconds` budgets, and per-call audit recording are all explicit. Tools come from the composed `IToolLoopToolRegistry` registrations in DI; v1 ships `WorkspaceToolLoopRegistry` (read-only file tools) + `ProjectionToolLoopRegistry` (entity-graph queries). The frontmatter declares a per-step `tools:` allow-list validated against the union of registries. Returns `FileChanges: []` — file mutations land in downstream `file-mutate` / `file-frontmatter` steps that consume the loop's `final_text` / `final_json` outputs. Termination reason persisted on the step so operators can distinguish `model_done` from budget breaches. Transcript + tool log saved as sidecar artifacts. | **Shipped** |
| `projection-sync` | Deterministic | Re-scans the working tree, applies the workspace's conventions from `.creuser/conventions/*.yaml`, and rebuilds the `cr.entities` + `cr.entity_refs` projection in a single transaction. Returns `FileChanges: []` (read-only against the tree). Outputs a `ProjectionReport` with entities-by-kind, refs resolved/unresolved, schema failures, convention conflicts, and per-convention content-hashes for downstream cache invalidation. Also fires automatically as a fire-and-forget continuation of `WorkspacesEndpoints.Sync` so every successful pull re-projects without an operator action. | **Shipped** |
| `llm-planner` | LLM | Emits a structured `JobPlan` against the registered step types. Plan is persisted and immutable. | Later |
| `wait` | Deterministic | Pause until time-of-day, until a webhook, or until a human approves. Uses the `Paused` + `ResumeToken` mechanism. | Later |

Each runner declares both an **input schema** (what its `parameters` look like) and an **output schema** (what downstream steps can reference). The job script's frontmatter binds upstream outputs to downstream inputs via `$step_id.output_name` references.

### Idempotency and caching

Re-running the same Job with the same inputs should — for the *deterministic* parts of the run — produce the same outputs without re-doing the work. Re-running the LLM parts should hit a cache when the prompt is bit-identical.

Two idempotency keys:

- **Step idempotency key** = `sha256(stepType || normalized(inputs) || stepConfigHash)`. Computed before the runner is invoked. Two consecutive runs of the same Job with identical inputs hash to the same key per step.
- **LLM cache key** (subset of step idempotency for `llm-*` types) = `sha256(model || prompt || systemPrompt || responseFormat || temperature)`. Tighter and used independently — a deterministic step that *contains* an LLM sub-call still benefits from the LLM cache even when the outer step's inputs differ slightly.

Cache implementation:

- **Step results** cached in `cr.step_results` keyed by step idempotency key + workspace id. TTL: indefinite. Manual invalidation via "force re-run" in the UI.
- **LLM responses** cached in `cr.llm_cache` keyed by LLM cache key. TTL: 30 days (configurable). Includes token usage so cost reporting stays honest on cache hits.

When a Run starts, the executor walks the DAG and skips any step whose idempotency key matches a `cr.step_results` row from a *previous successful Run on the same workspace*. The Run record marks those steps `Skipped` and references the prior result. Audit UI renders this clearly: "Step `parse_metadata` skipped — output identical to Run #42 step #2."

### File mutation discipline

The architectural rule: **the working tree is committed once per Step, not once per file change.** This is what gives sync and replay clean semantics.

```
[step starts]
  step accumulates FileChange[] in memory
  step ends successfully
[executor stages all FileChange ops in the working tree]
[executor commits with structured message linking to step]
[step result records the commit SHA]
```

If a step fails partway through accumulating changes, the changes are discarded — the working tree never sees a partial mutation. This is non-negotiable for the audit invariant.

Multiple steps in one Run produce multiple commits. The Run-level summary record points at the first and last commits; `git log <first>..<last>` is the audit trail of what the platform did during this Run.

```
[creuser] <step.name>  (run=<run_id> step=<step_id>)

<human_summary_from_step_outputs>

Step type: <type>
Files changed:
- src/foo.md  (modified)
- docs/index.md  (created)
- old/legacy.txt  (deleted)
```

### Auditability and replay

Every Run produces a structured audit record. The wire shape:

```csharp
public sealed record JobRun(
    Guid Id, Guid JobId, Guid WorkspaceId,
    DateTime StartedAt, DateTime? CompletedAt,
    JobRunStatus Status,                                     // Pending|Running|Paused|Succeeded|Failed|Cancelled
    IReadOnlyDictionary<string, object?> InputParameters,
    IReadOnlyList<JobRunStep> Steps,
    string? StartCommitSha,                                  // working tree SHA at start
    string? EndCommitSha,                                    // SHA after final commit (or StartCommitSha if no mutations)
    Guid? ResumedFromRunId,                                  // if this run resumed a paused predecessor
    Guid? PlanId,                                            // for plan-then-execute runs
    string? FailureMessage,
    long? TotalTokensUsed,
    decimal? TotalCostUsd
);

public sealed record JobRunStep(
    Guid Id, string StepType, string Name, int Position,
    StepStatus Status,
    DateTime StartedAt, DateTime? CompletedAt,
    string IdempotencyKey,
    Guid? CachedFromStepId,                                  // when status = Skipped
    IReadOnlyDictionary<string, object?> Inputs,             // resolved bindings, before execution
    IReadOnlyDictionary<string, object?>? Outputs,           // null until completion
    IReadOnlyList<FileChange> FileChanges,                   // applied changes
    string? CommitSha,                                       // commit produced by THIS step
    long DurationMs,
    long? TokensUsed,
    decimal? CostUsd,
    string? ErrorMessage,
    string? ResumeToken
);
```

**Replay** comes in three flavours:

- **Cache replay (free)** — re-execute the Run with the same inputs; deterministic steps and LLM cache hits return prior outputs immediately. The Run completes in seconds, no external calls. Used for the "view this run reproducibly" UI affordance.
- **Soft replay (cheap)** — same as above but force-misses the LLM cache. Re-runs LLM steps against the live provider; deterministic steps stay cached. Used to verify "would this run still pass with the latest model?"
- **Hard replay (full)** — invalidate all caches; re-execute everything. Used to verify reproducibility end-to-end or to re-do a run after fixing an upstream input.

LLM transcripts (full conversation including tool calls) are persisted as `StepArtifact`s under `<dataDir>/runs/<runId>/<stepId>/transcript/` and viewable in the SPA's `RunInspector` widget.

### Job script storage and frontmatter

Same dual-storage pattern as before — DB canonical, filesystem materialized — with the new step model expressed in frontmatter:

```yaml
---
id: ingest-arxiv-daily
name: Ingest arXiv articles daily
pattern: deterministic            # deterministic | plan-then-execute | agentic
parameters:
  schema:
    type: object
    properties:
      topic: { type: string, default: "machine learning" }
    required: [topic]
schedule:
  cron: "0 6 * * *"
  trigger_on: ["sync"]            # also run after every workspace sync
allowed_commands: [git, rg, fd]
required_secrets: [anthropic.key]
budgets:
  max_duration_seconds: 600
  max_tokens: 50000
  max_cost_usd: 0.50
steps:
  - id: fetch
    type: http
    inputs:
      url: "https://export.arxiv.org/api/query?search_query=cat:cs.LG&max_results=50"
  - id: parse
    type: llm-chat
    depends_on: [fetch]
    inputs:
      prompt: "Extract title, authors, abstract from each entry."
      input: $fetch.body
      response_format:
        $schema_ref: "schemas/article-list.json"
  - id: write
    type: file-mutate
    depends_on: [parse]
    inputs:
      ops:
        - op: create
          path: "research/$today/{{slug}}.md"
          content: "{{frontmatter}}\n\n{{body}}"
          for_each: $parse.articles
---
# Body (optional, type-dependent — for single-step jobs the body is the prompt
# or script; for multi-step jobs the steps are defined in frontmatter and the
# body is documentation.)

This job pulls fresh arXiv papers in cs.LG every morning and lands them under
research/YYYY-MM-DD/. Idempotent on title-hash; re-running the same day is a
no-op except for genuinely-new entries.
```

For a *single-step* Job (the pragmatic 80% case), the frontmatter shrinks dramatically — `pattern: deterministic` with one inline step, or no `steps:` block at all and the body is the step's content (for `llm-chat` it's the prompt; for `csharp` it's the source file).

Storage layout:

- `cr.job_scripts` — canonical record (frontmatter + body + version + status).
- `<dataDir>/scripts/{type}/{id}.{ext}` — materialized for filesystem-based tooling, the IDE editor, git tracking by the workspace if admins want to commit them.
- `cr.schedules` — cron / trigger entries. Separated because schedules can be edited without versioning the script.
- `cr.job_runs` — Run audit records.
- `cr.job_run_steps` — per-step audit records.
- `cr.step_results` — idempotency cache.
- `cr.llm_cache` — LLM response cache.
- `cr.job_plans` — emitted plans (plan-then-execute pattern).

Conflict policy when a script is edited in both DB and filesystem: last-write-wins with a timestamped version row in `cr.job_script_versions`. Operators see "this script changed in the repo since you last edited it" and can pick a side.

### Scheduling

Three trigger types in v1:

- **Cron** — `cr.schedules.kind = 'cron'` with a NCrontab-parseable `cron_expression`. UTC-evaluated; 5-field (`m h dom mon dow`) and 6-field (with seconds) both supported. The `SchedulerService` is a `BackgroundService` that ticks every `CREUSER_SCHEDULER_INTERVAL_MS` (default 30s), runs a single indexed query against `cr.schedules WHERE enabled AND kind='cron' AND next_due_at <= now`, and dispatches each due row.
- **Workspace sync** — `cr.schedules.kind = 'sync'` (no cron expression — mutually exclusive). Fires inline from the workspace `Sync` endpoint after a successful pull. Sync schedules never acquire a `next_due_at` so the cron tick won't double-fire them.
- **Manual / API** — `POST /api/jobs/{id}/run` with parameters, or `POST /api/workspaces/{slug}/schedules/{id}/fire` to dispatch a configured schedule on demand without waiting for the tick.

Both cron-tick and sync-hook paths route through `IJobScheduleDispatcher`, which creates a fresh DI scope per dispatch (so neither the cron tick nor the sync request pins the executor's lifetime), runs the job, then writes back `last_fired_at` + `last_run_id` and recomputes `next_due_at` for cron schedules. The dispatcher swallows + logs run-time exceptions so a busted job doesn't take down the tick loop. `JobRun.TriggerKind` records which path triggered the run (`cron`, `sync`, `manual`) so the audit timeline can render the cause.

Multi-instance deployments will need a Postgres advisory lock around the tick to prevent the same schedule firing from two hosts at once. Single-tenant on-prem v0.1 is fine without.

Future triggers (post-v1): git push (webhook), file-pattern change (sync diff intersected with a glob), HTTP webhook with body-as-input.

### Durable execution: today vs. with Wolverine

**Today (v0.1):** runs execute in-process via a `JobExecutor` service. Step results persist to Postgres after each step. If the host process dies mid-run, the Run is marked `Failed` on next startup with a "host crashed during execution" reason. Re-run from scratch (or from the last completed step, since prior steps are cached). Acceptable for single-instance on-prem, the v1 deployment shape.

**With Wolverine (v1.x):** each step becomes a Wolverine message; the saga state is a Marten document. The executor becomes a Wolverine handler that durably progresses through the DAG. Host crashes resume cleanly because the saga is stored and step transitions are events. The `IStepRunner` contract doesn't change — only the dispatch mechanism does, so step implementations are agnostic.

The current minimal `JobExecutor` (in-process, synchronous) and the future Wolverine-based one share the same `IStepRunner` registry, the same `StepResult` contract, and the same `cr.job_runs` audit shape. Migration is wiring-only.

### What landed first (the minimum viable slice — shipped)

The pragmatic slice that took the model from paper to end-to-end is in the codebase:

1. `IStepRunner` interface + `StepResult` / `FileChange` / `StepArtifact` records in `Creuser.Core`.
2. `LlmChatStepRunner` in `Creuser.Scripting` — first registered runner. Uses `AgentClientResolver`. Caches in `cr.llm_cache` keyed by `(provider, model, prompt, system, temperature, format)`.
3. YAML frontmatter parser (YamlDotNet) — both single-step (top-level `type:` + body) and multi-step (`steps:` array with `depends_on` + `$step_id.field` bindings).
4. `JobExecutor` — in-process synchronous runner that resolves inputs, invokes the runner, persists `JobRun` + `JobRunStep`, applies `FileChange` ops, commits. Multi-step path: `DagValidator` (Kahn-based topological sort) + `StepBindingResolver` (`$step_id.field` / `$params.name` navigation, raises `StepBindingException` with operator diagnostics on lookup failure). Cancellation propagates from failed upstreams to dependents.
5. `cr.job_scripts`, `cr.job_runs`, `cr.job_run_steps`, `cr.llm_cache`, `cr.schedules` tables (DapperMatic, additive `ALTER TABLE … ADD COLUMN IF NOT EXISTS` migrations).
6. CRUD endpoints for Jobs (admin); `POST /api/workspaces/{slug}/jobs/{jobId}/run` to trigger. Schedules surface (`/api/workspaces/{slug}/schedules` CRUD + `/fire`).
7. SPA: workspace settings → Jobs page (list + edit + run); Schedules page (cron / sync / manual fire); workspace home → recent runs.
8. The seven-runner deterministic catalog (`shell`, `csharp`, `python`, `node`, `file-mutate`, `file-frontmatter`, `http`) plus the cron / sync-hook scheduler.

Subsequent slices add `llm-tool-loop` (the agentic seam — design in `docs/wip/llm-tool-loop-design.md`), `cr.entities` projection, `llm-planner`, then Wolverine for durable execution.

### Sandbox model (forward-looking)

`run_shell` and the script runners (`shell`, `csharp`, `node`, `python`) enforce a per-job command allow-list declared in frontmatter. Anything outside the list returns "command not permitted." For arbitrary code execution, the runner spawns the script in a separate process under a non-root UID with bounded CPU / memory / timeout, in a working directory scoped to that run's tmp space. UID separation + ulimits are sufficient for single-tenant on-premise; real sandboxing (Firecracker, gVisor) is post-v1.

LLM step inputs are also gated: the prompt that goes to the provider is composed *only* from declared inputs + the system prompt body. Secrets, environment values, audit logs, and other workspace state never enter prompts unless the job's frontmatter explicitly references them via `secrets: [name]` or `inputs: { entity_data: $query.results }`.

### Frontmatter as cross-file metadata

The `file-frontmatter` runner is more than a syntactic helper — it's the seam that lets the platform express *intent* about source files in a uniform way across languages. The same YAML grammar (delimited per-language: `---` for Markdown, `/* --- … --- */` for C-style, `# ---` for Hash, `<!-- --- … --- -->` for HTML, `-- ---` for SQL) becomes a metadata layer that downstream steps query, index, and act on.

The pattern that emerges from chaining the registered runners:

1. **Annotate** — `file-frontmatter` decorates source files with `category`, `domain`, `owner`, `description`, `signature`, `references`, etc. Works against any file whose extension matches a known dialect.
2. **Index** — a `node` / `python` step (or, post-v1, a dedicated `index` runner) walks the workspace, parses the frontmatter from every annotated file, and emits a structured catalog (typically a JSON or Markdown index committed via `file-mutate`).
3. **Reason** — `llm-chat` (or `llm-tool-loop`) reads the index and identifies gaps: missing methods, undocumented endpoints, unowned files, untested categories. Structured outputs (JSON Schema) keep the LLM's diagnosis machine-readable.
4. **Generate** — `file-mutate` applies the LLM's proposed code changes as `create` or `modify` ops. The executor's transactional commit captures each step as an audit-trail-bearing commit.
5. **Re-annotate** — the next iteration's `file-frontmatter` step adds metadata to the newly-generated files, which feeds back into step 2.

This is the loop that makes Creuser a "self-improving repository" rather than a workflow runner. Recommended frontmatter keys to standardize on across files in a workspace:

- `id` — stable identifier (often slug-ish).
- `title` — short human label.
- `description` — one-or-two-sentence what-this-is.
- `category` / `domain` — taxonomy axes.
- `owner` — team or person responsible.
- `tags` — list of free-text labels.
- `signature` — for code files: the public surface (function/class signature, exported names).
- `references` — list of other entity ids this file links to (for graph queries).

A workspace's index step can be as simple as:

```yaml
type: node
allowed_commands: []
inputs:
  args: []
---
const fs = require('node:fs');
const path = require('node:path');
const root = process.env.CREUSER_WORKING_TREE;
const out = [];
function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(p);
    else {
      const text = fs.readFileSync(p, 'utf8');
      const fm = parseFrontmatter(text);  // small per-dialect helper
      if (fm) out.push({ path: path.relative(root, p), ...fm });
    }
  }
}
walk(root);
console.log(JSON.stringify(out, null, 2));
```

Pipe that into a `file-mutate` step that writes the index to `.creuser/index.json`, then an `llm-chat` step that takes the index as `input` and emits gap-fill instructions, then another `file-mutate` step that applies them. Five steps; the workspace gets richer on every run.

### Image transformations (deferred)

Image manipulation (resize / format conversion / optimization) is a natural future runner family — `image-transform` with declarative ops (`resize: {width, height}`, `convert: webp`, `optimize: {quality}`). It fits the same `FileChange[]`-returning pattern as `file-mutate`. The Docker fat image already includes the binaries that would back it (`magick` from ImageMagick, `oxipng`, `cwebp`); the runner itself is post-v1 work.

## Agent layer

Built on Microsoft.Extensions.AI as the abstraction. **Shipped today:**

- **Anthropic provider** via `Anthropic.SDK` 5.10.0 (Claude Opus 4.7 default).
- **OpenAI provider** via `Microsoft.Extensions.AI.OpenAI` (GPT-5 family + Azure OpenAI flavours).
- **Local provider** — same OpenAI SDK pointed at Ollama / LM Studio / vLLM via a custom base URL. Smart preset URLs in the SPA select `localhost` vs `host.docker.internal` based on `window.location.hostname`. The compose file maps `host.docker.internal` to the host gateway so Linux Docker can reach the host.
- **`AgentClientFactory`** (provider-agnostic, in `Creuser.Agents`) and **`AgentClientResolver`** (config + secrets → `IChatClient`, in `Creuser.Web/Agents/`). Resolver returns a `ResolveOutcome { Client, Reason }` so health probes and the chat endpoint surface specific "what's missing" messages instead of generic "not configured".
- **Function invocation** — `UseFunctionInvocation()` on both Anthropic and OpenAI factory paths, which is what makes the in-app assistant's `navigate` / `describe_capabilities` tools execute end-to-end. Earlier OpenAI factory was missing this; explicitly required for Gemma/Llama via local OpenAI-compatible endpoints to actually run tool calls.
- **`/api/agents/health?provider=...`** — sub-cent ping that the Environment page's "Test connection" button hits per provider.

API keys live in `/data/secrets/` files (chmod 600), referenced from `cr.app_settings.environment` by filename only — never stored in the database, never returned over the wire.

### Tool namespaces (forward-looking — wires up with the job runner)

Two distinct tool registries planned for autonomous agents:

**Projection toolset** — hits Postgres directly. Cheap, fast, structured.

- `query_entities(kind, filters, projection)`
- `get_entity_by_id(id)`
- `find_references(entity_id, ref_type?)`
- `get_workflow_definition(id)`
- `list_runs(workflow_id?, status?, limit?)`

**Workspace toolset** — operates on the on-disk working tree.

- `read_file(path)`
- `list_directory(path, recursive?)`
- `grep(pattern, path?, file_glob?)`
- `ast_grep(pattern, language)`
- `find_files_by_pattern(glob)`
- `git_log(path?, limit?)`
- `git_blame(path, line?)`
- `git_diff(ref_a, ref_b, path?)`
- `run_shell(command, args)` — sandboxed, allow-list per job

A well-designed agent uses projection tools for discovery and only drops to workspace tools for specific files. This is dramatically cheaper in tokens and faster than the naive "let the agent grep everything" approach.

These tools are distinct from the **assistant** tool registry below — the assistant is for human-facing UI navigation (read-only navigation + capability description), agents will be for autonomous workflow execution (read + write + shell). The two surfaces share `AgentClientResolver` but compose different tool sets.

### ToolLoopRunner — `llm-tool-loop` step type (Shipped)

Bounded ReAct implementation, registered as the `llm-tool-loop` step runner in `Creuser.Scripting`. Frontmatter declares the goal, the tool allow-list (subset of the workspace toolset above), the loop budgets (`max_steps`, `max_tokens`, `max_duration_seconds`), and the optional model / system prompt / `response_format_json`. The loop is **hand-driven** rather than relying on `UseFunctionInvocation()` — that middleware short-circuits the per-turn budget enforcement, per-call audit, and unrecoverable-tool short-circuiting that a step runner needs. `IChatClientResolver.ResolveRawAsync` returns a no-middleware client; the runner constructs the messages list, calls `GetResponseAsync`, walks `ChatResponse.Messages` for `FunctionCallContent` items, dispatches each call against the registry-built `AIFunction` set, appends `FunctionResultContent` to the conversation, and loops until the model emits a tool-call-free response (`termination_reason: "model_done"`) or hits a budget.

Tools come from the composed `IToolLoopToolRegistry` registrations in DI (multi-binding). v1 ships `WorkspaceToolLoopRegistry` with read-only `read_file` / `list_directory` / `grep` / `find_files_by_pattern` / `git_log`; the runner validates the per-step `tools:` list against the union of all registered registries before the loop starts. Path-escape attempts return `{ ok: false, fatal: true }` and the runner aborts the loop with `termination_reason: "tool_error_unrecoverable"` rather than letting the model retry.

Step output: `{ final_text, final_json, turns, tool_calls, tokens_used, cost_usd, model, provider, termination_reason }`. Returns `FileChanges: []` — mutations land in downstream `file-mutate` / `file-frontmatter` steps that consume the loop's `final_text` / `final_json`. Sidecar artifacts: `transcript.json` (full turn-by-turn record) and `tool_log.json` (flattened per-call audit). Token counts roll up into `JobRun.TotalTokensUsed`.

Detailed contract, frontmatter shape, tool registry surface, and test plan: `docs/wip/llm-tool-loop-design.md`. The runner is what realises the agentic execution pattern catalogued in **Three execution patterns**.

See **Execution model → Sandbox model** above for the unified treatment that covers both `run_shell` and the script runners (`shell` / `csharp` / `node` / `python`).

## In-app AI assistant

A second AI surface, distinct from the autonomous agent layer above: a **right-side chat panel** (toggled from the header) that helps the operator find features, navigate the UI, and understand the platform. **Shipped in v0.1.** Powered by the same `AgentClientResolver` + `IChatClient` infrastructure as the agent runner — the in-app assistant is just another consumer of the configured provider. Conversation persistence is per-browser via `useLocalStorage('creuser.assistant.history')` today; promotes to a server-side `cr.user_preferences` shape later.

### Capability discovery

The assistant doesn't know about endpoints — it knows about **capabilities**. A `Capability` is a record describing one discoverable thing the platform can do (a settings surface, an admin action, an operator workflow), with fields:

```csharp
public sealed record Capability(
    string Id,                       // e.g. "users.manage"
    string Topic,                    // e.g. "users", "branding", "environment"
    string Title,                    // human label
    string Description,              // when-to-use-this, drives AI tool selection
    IReadOnlyList<string> Intents,   // free-text phrases an operator might type
    string? Route,                   // SPA route to send the user to
    string? ExpandSection,           // section key the SPA auto-expands via ?expand=
    string RequiresRole,             // "User" or "Admin"
    bool Mutates                     // future: tools-that-write require UI confirmation
);
```

Capabilities are produced by `ICapabilityProvider` implementations registered in DI. `CapabilityRegistry` composes them and filters by the calling user's role before exposing the result to the AI tool registry.

### Three-stage evolution

The capability source-of-truth is meant to grow without disrupting consumers (chat endpoint, AI tools, frontend deep-links):

1. **Code-resident list (current).** `CoreCapabilityProvider` returns a hand-curated `Capability[]` literal in C#. Edited alongside endpoint changes — a PR that adds a feature touches the same file. Type-checked, refactor-safe, easy to keep honest in code review.
2. **`[AiCapability]` attributes (shipped).** Endpoint methods opt into discovery by carrying one or more `[AiCapability]` attributes — `[AiCapability("workspaces.list", "workspaces", "Configured workspaces", "...", "list workspaces", "show workspaces", Route = "/settings/workspaces", RequiresRole = Roles.Admin)]`. `EndpointAttributeProvider` reflects over the host assembly on construction and emits one `Capability` per attribute. Adding a feature with the attribute is automatically discoverable; one method may carry multiple attributes when several capabilities anchor on the same endpoint.
3. **Plugin-contributed providers (when plugins land).** The architecture's plugin model already discovers DLLs from `/data/plugins/`; plugins implement `ICapabilityProvider` to declare their own capabilities. A consumer-application plugin contributing an `acme.process_map` entity kind ships a provider that emits `Capability` entries for editing process maps. **Plugins describe themselves to the AI** through the same registration mechanism they use for widgets and job runners — single contract, multiple sources.

The AI tool registry, the chat endpoint, and the SPA's link-rendering all see capabilities through the same `CapabilityRegistry` interface regardless of which stage produced them.

### Tool registry

The assistant has a **deliberately small** tool registry — three tools by design, hand-written, all read-only in v1:

- **`navigate(intent)`** — keyword-scored match into the registry. Returns the best `Capability` plus a hint to render a clickable markdown link in the reply (`[Anthropic settings](/settings/environment?expand=aiAnthropic)`). The destination page reads the `?expand=` query param to deep-open the relevant section.
- **`describe_capabilities(topic?)`** — list capabilities visible to the calling user, optionally filtered by topic. For "what can I do?" / browsing intents.
- **`call_api(method, path, body?)`** (deferred to v2) — execute a *whitelisted* API call. The whitelist is generated from `[AiTool]`-tagged endpoints; mutating actions emit a "proposed action" payload first that the SPA renders as a confirm dialog. The AI never silently writes anything.

Tool selection quality drops past ~20 tools, so the registry stays small even as the platform grows. Capabilities (the catalog) grow without bound; tools (the AI's verbs) stay tight.

### Per-screen context

The SPA sends the current route in each chat request body (whitelist principle: only what we explicitly attach). The chat endpoint composes a system prompt with the user's role + current screen + tool-usage guidance. **Nothing introspected, nothing from `/data/secrets/`, no auto-attached config.** The assistant only sees what was deliberately put in front of it.

### Security boundary

Three rules, baked in:

1. **Whitelist what reaches the LLM.** The user's message + an explicit per-screen context payload + tool descriptions go to the provider. Secrets, environment values, audit logs, and other user data never enter prompts.
2. **Capabilities are role-filtered before tool invocation.** A `User`-role caller never sees `Admin`-only entries — protects against the assistant suggesting actions the user can't perform AND avoids leaking the existence of admin features. Filter lives in `CapabilityRegistry`, not the UI.
3. **Tools are hand-written, not auto-derived from OpenAPI.** Auto-derivation creates an unmanageable surface; manual curation gives us per-tool authorization, budget caps, and confirmation-before-mutation.

### Local-only deployments

Operators who don't want any prompts leaving their machine configure the **Local** AI provider (Ollama, LM Studio, vLLM) as the default. The chat path routes through the same provider abstraction with a custom OpenAI-compatible endpoint; no prompts reach Anthropic or OpenAI. This is the architectural answer to "we don't trust the cloud with our queries".

## Workspace navigation

Inside a workspace (`/w/:slug/...`), the SPA shell is three tiers from left to right:

```
[icon bar]   [optional sub-sidebar]   [content area = dockview]
```

The **icon bar** carries:

1. 🏠 **Home** — a non-deletable, admin-editable standalone dashboard. The workspace's overview screen.
2. **Standalone dashboards** — each is a single user-built dashboard with its own icon + label. Click renders directly in the content area.
3. **Dashboard groups** — each is a collection of dashboards rendered as one icon. Click opens the **sub-sidebar** listing the group's children; clicking a child renders it in content.
4. ⚙ **Workspace Settings** (workspace admin) — workspace config, members, plugin enablement, sync schedule, parsers/projection rules. Lives at the bottom of the bar.
5. ⚙ **Platform Settings** (platform admin) — branding, users, environment, the workspaces registry. Always present for platform admins regardless of workspace context.
6. 🚪 **Logout**.

The sub-sidebar only renders when a group icon is active; standalone dashboards, Home, and Settings collapse it entirely (no orphan rail).

The right-side **AI assistant panel** is global (toggled from the header) and lives outside this tier model — it's a modeless companion accessible from anywhere, not a top-level nav destination.

### Empty-state behavior

When the user has zero accessible workspaces:

```
[no workspace selected]
─────────────────────────
0 workspaces available — contact an admin to be added
─────────────────────────
⚙ Platform Settings   ← only when user.role === Admin
🚪 Logout
```

Platform admins land on the workspaces registry (`/settings/workspaces`) when they sign in with no recent workspace; non-admin users with zero accessible workspaces see the no-access landing.

## Dashboard composer

A **dashboard** is a saved dockview layout plus a set of widget instances, scoped to a workspace, accessed at `/w/:slug/d/:dashboardSlug`. **dockview-vue** is the layout primitive — splittable, dockable, resizable panels — which is what gives Creuser its "stock-trading-system feel" without fighting Quasar's grid. The earlier Section → Row → Column → Widget grid model was retired in favor of dockview during planning; it didn't survive contact with the use case.

A **dashboard group** is a UI collection of dashboards, surfaced as a single icon in the workspace nav. Groups are admin-curated; the dashboards they contain are otherwise ordinary. Group membership is a `dashboards.group_id` foreign key — the dashboard is the unit of identity, the group is just a sidebar arrangement.

```typescript
interface Dashboard {
  id: string;
  workspaceId: string;
  groupId: string | null;   // null = standalone (own icon in the bar)
  slug: string;
  name: string;
  icon: string | null;      // material icon name; required for standalone
  layout: DockviewLayoutState;
  widgets: WidgetInstance[];
  position: number;         // ordering within group, or among standalones in the bar
  isDefault: boolean;       // shipped by Creuser; admins can edit, hard-delete is gated
}

interface DashboardGroup {
  id: string;
  workspaceId: string;
  slug: string;
  name: string;
  icon: string;
  position: number;
  isDefault: boolean;
}

interface WidgetInstance {
  id: string;
  widgetType: string;       // looked up in WidgetRegistry
  props: Record<string, unknown>;  // conforms to widget's prop schema
}
```

Widgets are Vue components registered in a `WidgetRegistry` by name. Each widget declares a JSON Schema for its props. The "widget designer" is form-driven: pick a widget from the registry, the form auto-renders from its schema, drop it into a dockview pane.

### Defaults shipped with a new workspace

Two icons appear out of the box; admins build out from there:

- 🏠 **Home** (standalone dashboard) — recent runs, last-sync status, member count, quick links. Admin-editable, not user-deletable.
- ⚡ **Operations** (group) — pre-built dashboards: *Runs*, *Scripts*, *Workflows*. Each is a thin dashboard wrapping the corresponding widget at full width. Admins can edit, reorder, dissolve the group, or convert children to standalone.

The "Add new dashboard" flow defaults to **standalone**. Adding to a group is a deliberate choice. Group creation is a separate action ("New group") — a small friction tax to discourage premature 12-group / 2-dashboard-each fragmentation.

### Initial widget set (v1)

- `JobScriptEditor` — Monaco-based, reads/writes from DB
- `RunInspector` — Saga state viewer with step transitions
- `LogTail` — Live log streaming
- `WorkflowGraph` — DAG visualization
- `RepoTreeBrowser` — File tree of a workspace
- `EntityInspector` — Generic entity viewer with `kind`-aware rendering
- `MarkdownViewer` — Rendered markdown
- `Form` — Auto-rendered from JSON Schema
- `Table` — Tabular data with sort/filter
- `Chart` — Recharts/Plotly integration
- `MetricSparkline` — Compact metric display
- `ScheduleCalendar` — Cron-based job visualization

Plugin-provided widgets register at startup via `IWidgetRegistration` discovered from `/data/plugins/*.dll`.

### Tables

```sql
CREATE TABLE cr.dashboard_groups (
  id            uuid PRIMARY KEY,
  workspace_id  uuid NOT NULL REFERENCES cr.workspaces(id) ON DELETE CASCADE,
  slug          text NOT NULL,
  name          text NOT NULL,
  icon          text NOT NULL,
  position      int  NOT NULL DEFAULT 0,
  is_default    boolean NOT NULL DEFAULT false,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  created_by    uuid REFERENCES cr.users(id),
  UNIQUE (workspace_id, slug)
);

CREATE TABLE cr.dashboards (
  id            uuid PRIMARY KEY,
  workspace_id  uuid NOT NULL REFERENCES cr.workspaces(id) ON DELETE CASCADE,
  group_id      uuid REFERENCES cr.dashboard_groups(id) ON DELETE SET NULL,
  slug          text NOT NULL,
  name          text NOT NULL,
  icon          text,                       -- required for standalone, optional in groups
  layout        jsonb NOT NULL DEFAULT '{}', -- dockview layout state
  widgets       jsonb NOT NULL DEFAULT '[]', -- widget instances + props
  position      int  NOT NULL DEFAULT 0,
  is_default    boolean NOT NULL DEFAULT false,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  created_by    uuid REFERENCES cr.users(id),
  UNIQUE (workspace_id, slug)
);
```

URLs always reference dashboards directly (`/w/:slug/d/:dashboardSlug`); the URL doesn't carry the group, since the group is just a UI grouping construct. The icon bar item carries either a dashboard slug (standalone) or a group slug (group); the sub-sidebar resolves group → dashboards via the `group_id` FK.

## Authentication and authorization

The audience is **internal teams at one org** — operators and analysts working in a single Creuser deployment. Account creation is **invite-only**; there is no self-serve sign-up. Cookie-based sessions, Argon2id hashing, account state in Postgres `cr.users`, providers behind `IAuthProvider` so Google OAuth and OIDC can land later without rewiring callers.

### Authorization model

Two axes, kept deliberately small for v1:

**Global role** (column `cr.users.role`):

- `Admin` — can configure platform settings (Branding, Users, Environment, Workspaces) and has implicit `Editor` access to every workspace. Bootstrap admin from environment variables (see below) is always `Admin`.
- `User` — non-admin. Can only access workspaces they have been explicitly granted membership to via `cr.workspace_members`.

**Per-workspace role** (column `cr.workspace_members.role`):

- `Editor` — can create and edit jobs, dashboards, widgets, workflows, and run them within the workspace.
- `Viewer` — read-only. Can browse the workspace, view dashboards, inspect runs and traces, but cannot mutate state or trigger runs.

Admins do not need explicit `cr.workspace_members` rows — admin-ness implies `Editor` on every workspace. This keeps the membership table free of the "ghost rows for every admin × workspace" pattern.

Workspace membership is the **only** access-control axis for v1. There is no workspace-internal RBAC (no "can edit dashboards but not jobs"); that's deferred to post-v1 if a real use case appears. Endpoint authorization checks reduce to: *(global role == Admin) OR (workspace member with sufficient role)*.

`cr.workspace_members` shape:

```sql
CREATE TABLE cr.workspace_members (
  workspace_id  uuid NOT NULL REFERENCES cr.workspaces(id) ON DELETE CASCADE,
  user_id       uuid NOT NULL REFERENCES cr.users(id) ON DELETE CASCADE,
  role          text NOT NULL,            -- 'Editor' or 'Viewer'
  granted_at    timestamptz NOT NULL DEFAULT now(),
  granted_by    uuid REFERENCES cr.users(id),
  PRIMARY KEY (workspace_id, user_id)
);
```

After login, the SPA fetches the user's accessible workspaces (admins see all; users see their `cr.workspace_members` rows) and lands them on the last-used or first-accessible workspace. A user with zero accessible workspaces sees a "no access — contact your admin" landing instead of an empty shell.

The `Creuser.Auth.*` projects encapsulate the seam:

- `Creuser.Auth.Abstractions` — interfaces (`IUserStore`, `IPasswordHasher`, `IAuthProvider`), the PascalCase `User` domain record, role constants.
- `Creuser.Auth.Core` — `Argon2idPasswordHasher`, `TempPasswordGenerator`, `BootstrapAdminService`, `CookieAuthHelpers` (claim construction).
- `Creuser.Auth.Providers.Local` — the local username+password provider, registered as the default `IAuthProvider`.
- `Creuser.Auth.Providers.Google` — stub. Returns `AuthResult.NotSupported` until v0.2 lights up alongside SMTP-driven flows.

### Bootstrap admin

`Creuser.Persistence.DbInitializer` runs on every startup (idempotent). On first boot, when `cr.users` is empty, it seeds an admin from environment variables:

| Env var | Default | Purpose |
| --- | --- | --- |
| `CREUSER_BOOTSTRAP_EMAIL` | `admin@creuser.local` | Initial admin email |
| `CREUSER_BOOTSTRAP_PASSWORD` | `ChangeMe!` | Plaintext, hashed at seed |
| `CREUSER_BOOTSTRAP_PASSWORD_HASH` | (unset) | Pre-computed Argon2id hash; takes precedence over the plaintext for production deployments that won't put plaintext in env |

The seeded admin is always created with `must_change_password: true`. Subsequent boots are no-ops.

### Account creation (invite-only)

Admins create accounts via `POST /api/admin/users` with `{ email, displayName, role, temporaryPassword? }`. The server validates uniqueness, hashes the password (Argon2id), inserts the user with `must_change_password: true`, and returns `{ userId, email, temporaryPassword }` — the plaintext temp password is returned **once** so the admin can convey it out-of-band (Slack, text, voice). It is not retrievable again.

The admin can either:

- Omit `temporaryPassword` — server generates a strong 12-char password (no visually ambiguous characters, mix of upper/lower/digit/symbol).
- Supply `temporaryPassword` — admin picks a memorable value to dictate over voice. Validator enforces ≥ 8 characters.

The new user signs in with the temporary password, is forced through the password-change flow on first login, and proceeds normally thereafter. SMTP-driven email delivery is **deferred to v0.2** (configured in-app, not via env vars), at which point the same flow can also email the temp password automatically.

Admin-only operations:

- `POST /api/admin/users` — create a user (above)
- `POST /api/admin/users/{id}/reset-password` — generate a new temp password, force change on next login
- `POST /api/admin/users/{id}/active` — toggle `is_active` (deactivated users can't sign in; sessions remain until expiry)
- `GET /api/admin/users` — paginated list

Endpoints are gated by `RequireAuthorization(p => p.RequireRole("Admin"))`.

### Password storage

Argon2id via [Konscious.Security.Cryptography.Argon2](https://www.nuget.org/packages/Konscious.Security.Cryptography.Argon2). Parameters tuned for ~250ms hashing on commodity hardware (m=64MB, t=3, p=4). Hashes stored in PHC-like format: `argon2id:m=65536:t=3:p=4:{saltB64}:{hashB64}`. Verification is constant-time via `CryptographicOperations.FixedTimeEquals`.

### Session

ASP.NET Core cookie authentication. Cookie `creuser-session`, HttpOnly, SameSite=Lax, sliding 14-day expiry. Authentication failures from `[Authorize]` short-circuit to `401 Unauthorized` (no redirect to a login page) so the SPA can decide where to send the user; authorization failures return `403 Forbidden`. Both responses use ProblemDetails per the [minimal-api-contracts](.claude/skills/minimal-api-contracts/SKILL.md) skill.

Data protection keys are persisted to `<DataDir>/keys/` (default `/data/keys/` in the container) so cookies survive container restarts. The architecture-doc-original phrase "JWT in HTTP-only secure cookie" was updated: cookies carry signed claims via the standard ASP.NET cookie middleware, not a literal JWT — functionally equivalent for clients, simpler in implementation.

### Future (post-v0.1)

- SMTP **configured in-app** (not env vars). Lights up: invite-by-email, full forgot-password flow with single-use tokens, run/error notifications.
- Google OAuth provider (`Creuser.Auth.Providers.Google`) — implements the existing `IAuthProvider` seam.
- OIDC provider for corporate SSO (Okta / Entra / Auth0) — likely a generic `Creuser.Auth.Providers.Oidc` package.
- MFA (TOTP). Probably surfaces as a per-user `mfa_secret` column and a verify step between password and session-cookie issuance.

## Naming conventions

Two parallel naming styles, applied at different layers:

- **DapperMatic-managed table classes (`Creuser.Persistence/Tables/`) are lowercase**, with property names matching column names verbatim. Example: `class users { public Guid id; public string password_hash; … }`. This avoids Dapper's `MatchNamesWithUnderscores` runtime mapping and lets the entity-class shape exactly mirror the schema. `[DmColumn(...)]` attributes still carry the metadata that's not expressible in C# (length, default expressions, `providerDataType` for `timestamptz` etc., constraints). StyleCop / IDE warnings about lowercase identifiers are suppressed at file scope (`#pragma warning disable IDE1006, CA1707, SA1300, SA1308, CS8981`).
- **Domain records and DTOs are PascalCase.** Anything passed across project boundaries — the `User` record in `Creuser.Auth.Abstractions`, request/response DTOs in `Creuser.Web.Contracts`, validators, etc. — uses standard C# casing. Repositories convert between the lowercase row and the PascalCase domain object explicitly (see `usersRepository.ToDomain` / `ToRow`).

## White-labeling

Branding is configured in-app, stored as a single Marten document, applied via CSS variables and dynamic component rendering.

### Configurable surface

- **Product name** — visible string everywhere (e.g. rebrand "Creuser" to your organization's product name)
- **Logo** — uploaded SVG/PNG
- **Favicon** — uploaded ICO/PNG
- **Login page background** — uploaded image
- **Color palette** — primary, secondary, accent, plus dark/light mode overrides
- **Font family** — optional override (defaults to system stack)
- **Header tagline** — short HTML/Markdown
- **Footer text** — HTML/Markdown
- **Support contact** — email, URL, phone (optional)
- **Email templates** — Mustache/Liquid templates for password reset, run failure, etc.
- **Base URL** — for absolute links in emails and webhooks
- **i18n overrides** — per-key string overrides on top of the default locale

### Application

The branding doc is loaded at app boot and on configuration change. The SPA fetches it from `/api/branding` (no auth required; public). CSS variables are applied via a runtime-injected style tag. Components consume the branding from a Pinia store.

### Attribution

A "Powered by Creuser, an MJCZone open-source project" attribution appears on the About page and in the footer of unauthenticated pages (login, password reset). This is LGPL-3.0 attribution and is **not** hideable. Everything else can be re-skinned.

## Plugin model

Plugins are .NET assemblies dropped into `/data/plugins/`. **Loaded once per Creuser instance** at startup — they are not per-workspace runtime isolation. Discovered at startup. Manifest declares:

- Plugin name, version, author
- Required Creuser version range
- Provided extensions: job runners (new `type:` values for job scripts), workspace types (new backends beyond git/local/s3), widgets, agent providers, parsers, capability providers (`ICapabilityProvider`)
- Required tools (host-OS binaries the plugin's runners need)

No hot-reload in v1. Plugin changes require restart. Operators copy DLLs in via their normal infrastructure (Railway file mounts, Docker volume updates, etc.). The plugin status page surfaces clearly when a plugin failed to load (e.g. its `required_runtimes` aren't available on a `:slim` deployment) — see [docker-variants.md](./docker-variants.md).

### Per-workspace enablement

Plugins are loaded globally; a workspace **opts into** the plugin's contributions. The mental model: plugins are the verb library; workspaces choose which verbs are visible in their job-runner picker, widget palette, agent-provider list, and capability registry. This makes "enabled plugins" part of the workspace's settings surface (a tab inside `/settings` → `Workspaces` → workspace detail) without requiring the plugin loader to ever produce per-workspace AssemblyLoadContexts.

Persisted as a join table:

```sql
CREATE TABLE cr.workspace_plugins (
  workspace_id  uuid NOT NULL REFERENCES cr.workspaces(id) ON DELETE CASCADE,
  plugin_id     text NOT NULL REFERENCES cr.plugins(id) ON DELETE CASCADE,
  enabled       boolean NOT NULL DEFAULT false,
  enabled_at    timestamptz NOT NULL DEFAULT now(),
  enabled_by    uuid REFERENCES cr.users(id),
  PRIMARY KEY (workspace_id, plugin_id)
);
```

Lands when the plugin loader exists; designed now so the plugin loader can ship with the per-workspace gate already wired.

### Vocabulary: plugins vs scripts vs jobs

```
Plugin = capabilities the platform now knows how to do (verbs)
Script (in cr.job_scripts)  = a recipe that composes those verbs
Job run                     = an execution of a script, against a workspace,
                              on a schedule or trigger
```

Plugins teach the platform *new kinds of work*. Scripts compose them into the daily improvement loops that make a workspace's repo iteratively better.

## Persistent volume layout

```
/data/
├── secrets/                    # API keys, workspace credentials (chmod 600)
│   ├── anthropic.key
│   ├── openai.key
│   ├── workspace-<slug>.pat    # HTTPS PAT for a git workspace (https-pat mode)
│   └── workspace-<slug>.key    # OpenSSH private key for a git workspace (ssh-key mode)
├── keys/                       # ASP.NET data protection keys (cookies survive restart)
├── branding/                   # Logo / favicon / login-bg uploads, content-addressed
│   └── logo-<sha>.<ext>        # served via /api/branding/assets/...
├── workspaces/                 # Checked-out git repos (managed by WorkspaceFilesystemService)
│   └── <slug>/
│       ├── .git/
│       └── ...
├── plugins/                    # Drop-in DLLs (forward-looking)
├── scripts/                    # Materialized job scripts (forward-looking)
├── prompts/                    # Materialized prompt templates (forward-looking)
├── logs/                       # Serilog rolling files
└── tmp/                        # Agent scratch space, periodically cleaned (forward-looking)
```

The directory is configurable via `CREUSER_DATA_DIR`; it defaults to `/data` in the container and `<repo>/.data/` in dev / on-host runs (the dev default lets git workspaces clone into the repo's gitignored `.data/` so the IDE can browse the cloned tree.).

## Container tooling

The `:latest` (fat) image includes a curated tool palette pre-installed:

**Core text & search:** `git`, `ripgrep`, `fd`, `jq`, `yq`, `xq`, `tree`, `bat`
**Code-aware:** `ast-grep`, `tree-sitter` CLI, `srgn`
**Schema & data:** `psql`, `redis-cli`, `sqlite3`, `csvkit`
**Diff & merge:** `delta`, `difft`, `diff-so-fancy`
**Language runtimes:** Node 24 LTS, Python 3.13 + `uv`, .NET 10 SDK
**Specialized:** `atlas`, `dbmate`, `migra`

Image size: ~2 GB.

The `:slim` image is .NET-only, ~600 MB. Same Dockerfile, different build arg.

Beyond the baseline, plugins declare additional tool dependencies in their manifest and operators install them via `apt-get` in a derivative image. Runtime apt installs from inside the running container are not supported.

## API conventions

All endpoints return either:

- **Success:** `200 OK` with body `{ "result": <T> }` where `<T>` is `string | number | boolean | object | array`
- **Failure:** RFC 7807 Problem Details (`application/problem+json`)

Defined as:

```csharp
public record ApiResult<T>(T Result);
```

Generated TypeScript:

```typescript
interface ApiResult<T> { result: T }
```

Endpoints use minimal API typed results: `Results<Ok<ApiResult<T>>, ProblemHttpResult>`.

OpenAPI 3.1 emitted via `Microsoft.AspNetCore.OpenApi`. Scalar mounted at `/scalar` for interactive docs. The `openapi.json` is the source-of-truth for `@hey-api/openapi-ts` generating the TypeScript client into `src/Creuser.Web.Spa/src/api/`.

## SPA routing

The SPA uses **Vue Router in history mode** (`vueRouterMode: 'history'` in `quasar.config.ts`), not hash mode, so URLs are clean (`/w/acme/d/runs`, not `/#/w/acme/d/runs`). ASP.NET Core's `MapFallbackToFile("index.html")` serves `index.html` for any path that doesn't match a static file or `/api` / `/hub` / `/scalar` route, which is what makes deep-linking and refresh work.

Top-level structure:

| Path | Scope | Notes |
| --- | --- | --- |
| `/login` | Public | Branding-aware; no shell chrome |
| `/` | Authenticated | Home — workspace picker if zero/multi, redirects to last-used otherwise |
| `/w/:workspaceSlug` | Workspace | Workspace home (the standalone Home dashboard) |
| `/w/:workspaceSlug/d/:dashboardSlug` | Workspace | Any dashboard (standalone or grouped) |
| `/w/:workspaceSlug/settings/...` | Workspace admin | Workspace settings (members, plugin enablement, sync schedule, etc.) |
| `/settings/...` | Platform admin | Branding, Users, Environment, Workspaces (admin CRUD) |
| `/profile` | Authenticated user | Password change, personal preferences |

Workspace-scoped routes are the bulk of the app. The slug in the URL is the active-workspace identifier — there is no hidden global "current workspace" Pinia state to fight with. Two browser tabs open to `/w/acme/d/runs` and `/w/widgets/d/runs` show two different workspaces in parallel without interference. The workspace store is keyed by slug; the active slug is read from the route.

Dashboard groups don't appear in the URL — the icon bar resolves the group from the dashboard's `group_id` for sub-sidebar rendering, but the URL only carries the dashboard slug. This keeps URLs short and lets admins reorganize groups without breaking bookmarks.

The auth guard (`router/index.ts`) enforces four rules: (1) any non-public route requires an authenticated session; (2) `/settings/*` requires the platform `Admin` role; (3) `/w/:slug/*` requires that slug to be in the user's accessible-workspaces list (or the user is a platform Admin); (4) `/w/:slug/settings/*` requires the workspace `Editor` role on that slug (or platform Admin). Failures redirect to `/login`, the no-access landing, or 403 — not silent.

## Local development

The repository root has a `package.json` that orchestrates both the .NET solution and the Quasar SPA from a single command surface. Bootstrap once after a fresh clone:

```bash
npm install            # installs root devDeps and runs `postinstall`,
                       # which chains: `install:all` (SPA + Vitest project)
                       # then `dotnet tool restore` (CSharpier)
```

That single command sets up everything. Then start the backing services (Postgres + Redis) once per dev session:

```bash
npm run services:up    # docker compose -f docker/docker-compose.dev.yml up -d
```

After that:

| Command | What it runs |
| --- | --- |
| `npm run dev` | SPA dev server (`quasar dev`) and `dotnet watch` on `Creuser.Web` in parallel |
| `npm run build` | `dotnet build` (emits OpenAPI spec) → codegen → `quasar build` into `wwwroot` |
| `npm test` | `dotnet test` → `vitest run` |
| `npm run codegen` | Refresh the SPA's TypeScript API client from the latest backend OpenAPI spec |
| `npm run lint` / `lint:fix` | CSharpier check / format + ESLint check / `--fix` |
| `npm run format` | CSharpier format + Prettier write |
| `npm run services:up` / `services:down` / `services:logs` | Start / stop / tail Postgres + Redis dev containers |
| `npm run services:purge` | Stop containers AND delete the data volumes — clean slate |

### Backing services (Postgres + Redis)

Postgres 17 (with `pgvector`) and Redis 7 run as containers via `docker/docker-compose.dev.yml`. The .NET backend and SPA dev server run on the host (not containers) for fast feedback.

**Random host ports.** The compose file uses `127.0.0.1::5432` / `127.0.0.1::6379` — Docker assigns ephemeral host ports, so this stack coexists with other compose projects on the same machine that may already own the defaults. After `services:up`, `scripts/wire-dev-services.mjs` reads the assigned ports via `docker port` and writes them into `src/Creuser.Web/appsettings.Development.local.json` (gitignored) as proper connection strings. The .NET app loads that file at startup (Program.cs adds `appsettings.{Environment}.local.json` to the configuration sources), so the backend just connects — no manual port wrangling.

`appsettings.json` declares the keys with empty values:

```json
"ConnectionStrings": {
  "Postgres": "",
  "Redis": ""
}
```

In dev, `appsettings.Development.local.json` overrides them with the discovered ports. In production, environment variables (`ConnectionStrings__Postgres`, `ConnectionStrings__Redis`) override them via the standard env-var configuration provider — set in `docker/docker-compose.yml`.

Credentials in dev are intentionally weak — local-only. Production reads `${POSTGRES_PASSWORD}` from the environment (the compose file fails fast if it's unset). Volumes are named (`creuser-dev_postgres-data`, `creuser-dev_redis-data`) so `services:down` stops the containers but preserves data; `services:purge` deletes everything for a clean slate.

### Dev proxy: one origin in the browser

When `npm run dev` is running, the browser hits the **Quasar dev server only** (default `http://localhost:9000`). The Vite dev server proxies backend traffic to ASP.NET Core (`http://localhost:5128`, see `Properties/launchSettings.json`):

- `/api/*` → REST endpoints
- `/scalar` → interactive OpenAPI docs
- `/hub/*` → SignalR (proxied with WebSocket upgrade — `ws: true`)

This avoids CORS during development and matches the production shape, where ASP.NET Core serves the built SPA from `wwwroot/` and exposes `/api` and `/hub` from the same origin.

### Real-time updates (SignalR)

Creuser is a multi-operator workbench: many developers and analysts may be inspecting runs, editing job scripts, or watching log tails simultaneously. Push updates are first-class.

- **Hub:** `Creuser.Web/Hubs/NotificationsHub.cs`, mounted at `/hub/notifications`.
- **Pub/sub model:** clients call `Subscribe(channel)` / `Unsubscribe(channel)` to join SignalR groups; servers (or other clients with appropriate auth) call `Broadcast(channel, payload)` which fans out to the group as a `notification` event.
- **Channels are stringly-typed by convention:** `runs:<run_id>`, `workspace:<workspace_id>`, `entity:<kind>`, `system:branding`. The dashboard composer's widgets self-subscribe based on the data they're rendering.
- **Backplane:** for v1 we run a single Creuser instance per organization, so SignalR's in-process scaleout is enough. If we ever go multi-instance, swap in the Redis backplane (Redis is already in the stack).

## Deployment

### Single Dockerfile, multi-stage

The snippet below is illustrative of the shape; the canonical Dockerfile lives at `docker/Dockerfile` and may diverge as the build evolves (e.g. `dotnet tool restore` for CSharpier in CI, additional apt packages, build-arg toggles for `:slim` vs `:latest`).

```dockerfile
# Stage 1: SPA
FROM node:24-alpine AS spa-build
WORKDIR /spa
COPY src/Creuser.Web.Spa/package*.json ./
RUN npm ci
COPY src/Creuser.Web.Spa/ ./
RUN npm run build

# Stage 2: .NET
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY . .
COPY --from=spa-build /spa/dist/spa ./src/Creuser.Web/wwwroot
RUN dotnet publish src/Creuser.Web/Creuser.Web.csproj -c Release -o /app

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=dotnet-build /app .
RUN apt-get update && apt-get install -y \
    git ripgrep fd-find jq tree bat \
    nodejs python3 python3-pip \
    && rm -rf /var/lib/apt/lists/*
EXPOSE 8080
VOLUME ["/data"]
ENV CREUSER_DATA_DIR=/data
ENTRYPOINT ["dotnet", "Creuser.Web.dll"]
```

### docker-compose.yml

```yaml
services:
  creuser:
    image: ghcr.io/mjczone/creuser:latest
    ports:
      - "8080:8080"
    environment:
      CREUSER_POSTGRES: "Host=postgres;Database=creuser;Username=creuser;Password=${POSTGRES_PASSWORD}"
      CREUSER_REDIS: "redis:6379"
    volumes:
      - creuser-data:/data
    depends_on:
      postgres: { condition: service_healthy }
      redis: { condition: service_started }

  postgres:
    image: pgvector/pgvector:pg17
    environment:
      POSTGRES_DB: creuser
      POSTGRES_USER: creuser
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - postgres-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U creuser"]
      interval: 5s

  redis:
    image: redis:7-alpine
    command: redis-server --save 60 1 --loglevel warning
    volumes:
      - redis-data:/data

volumes:
  creuser-data:
  postgres-data:
  redis-data:
```

### CI / Release pipeline

GitHub Actions, three workflows:

- **`ci.yml`** — runs on every PR. Single job that runs `npm install && npm run install:all && dotnet tool restore && npm run lint && npm test && npm run build`. No image push.
- **`edge.yml`** — push to `main` → build, test, push to GHCR with `:edge` tag (dev builds for testing).
- **`release.yml`** — push of `v*` tag → build, test, push to **both** GHCR and Docker Hub with tags `:latest`, `:slim`, `:<version>`, `:<version>-slim`.

Release ceremony: `git tag v0.1.4 && git push --tags`. That's what makes a release real.

## Open architectural questions for post-v1

These are deliberately deferred to keep v1 scope tight:

- Multi-repo workflow orchestration (cross-repo dependencies)
- S3 workspace backend implementation
- libgit2sharp adoption to replace shell-out for read-side git ops
- Multi-instance deployments (Postgres advisory locks instead of in-memory `SemaphoreSlim` for workspace sync; advisory lock around the scheduler tick to prevent double-fire from two hosts; SignalR Redis backplane)
- Hot-reload plugin system
- Per-workspace plugin runtime isolation (separate AssemblyLoadContexts) — enablement gate is in the v1 design; isolation is post-v1
- Full per-workspace RBAC beyond Editor/Viewer
- OIDC integration for SSO (currently planned for v0.2)
- Workflow / job-script import/export
- Embedded observability dashboard
- Real sandboxing for arbitrary code execution (Firecracker / gVisor)
- Multi-tenant deployment mode (currently architecturally excluded)
- HTTP step caching (parallel to `cr.llm_cache`); the wire shape is forward-compatible
- Per-job time zones for cron schedules (UTC-only in v1)

## IP boundary (Creuser core vs. consumer applications)

Creuser is MJCZone IP, LGPL-3.0, public. Domain-specific consumers are private, built on top of Creuser as plugins or downstream applications.

**In Creuser core:**

- Workflow saga primitives
- Job script frontmatter contract
- Generic `cr.entities` projection table
- Workspace abstraction (git, s3, local)
- Agent runner over M.E.AI
- Dashboard composer with section/row/column/widget primitives
- Generic widget pack (JobScriptEditor, RunInspector, LogTail, etc.)
- White-labeling configuration system
- Plugin discovery mechanism

**In a consumer application (private repo):**

- Domain-specific architecture or framework models
- Domain-specific schema definitions and matrix structures
- Custom entity `kind`s and their schemas
- Specific business rules and their YAML/MD parsers
- Specific agent prompts that know the consumer's codebase conventions
- Specific widgets that render domain-specific views
- Consumer-specific workflows and job scripts
- Their branding configuration

The discipline: when a missing feature is discovered while building a consumer application, decide which side it belongs on before implementing. If it's generic, add it to Creuser. If it's specific to the consumer's domain, add it to the consumer's repo. **Do not blur this boundary under deadline pressure.** Every consumer-specific feature that lands in Creuser core is private IP accidentally given away.
