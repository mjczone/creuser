# Creuser Architecture

> **Status:** Pre-release. Architecture document for v0.1.0 and the path to v1.0.
> **Last updated:** 2026-05-01
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

## Stack

**Backend**

- .NET 10
- ASP.NET Core minimal APIs
- ASP.NET Core SignalR (real-time push to the dashboard; many concurrent dev/operator sessions)
- Marten (event-sourced documents, sagas, JSONB-native queries)
- Wolverine (durable message dispatch, scheduling, sagas)
- MJCZone.DapperMatic (DDL abstraction and migrations for relational tables, DML auto-mapping for Dapper)
- libgit2sharp (git operations) with shell-out to `git` binary as fallback
- Microsoft.Extensions.AI (LLM abstraction; Anthropic + OpenAI providers)
- Serilog (structured logging)
- OpenTelemetry (traces and metrics)
- Microsoft.AspNetCore.OpenApi (native OpenAPI 3.1 generation)
- Scalar (interactive API docs at `/scalar`)
- CSharpier (formatter; pinned via `dotnet-tools.json`, enforced by pre-commit hook)

**Frontend**

- Quasar 2 (Vue 3 Composition API)
- TypeScript strict mode
- Vite (via Quasar CLI in SPA mode)
- dockview-vue (dense tiling/docking dashboards)
- Pinia (state management)
- Monaco Editor (script editing)
- @hey-api/openapi-ts (TypeScript client generation from OpenAPI)
- @microsoft/signalr (SignalR JS client for real-time dashboard updates)
- Vitest + @vue/test-utils + jsdom (SPA unit tests; project at `tests/Creuser.Web.Spa.Tests/`)
- husky + lint-staged (pre-commit: CSharpier on staged `*.cs`)
- Node 24 LTS, npm only (no pnpm or yarn)

**Infrastructure**

- Postgres 17 with pgvector extension
- Redis 7
- Docker (single image, multi-stage build)

## Solution layout

```txt
creuser/
├── src/
│   ├── Creuser.Core/                       # Domain primitives, no infra deps
│   │   ├── Workflows/                      # Saga base classes, step definitions
│   │   ├── Jobs/                           # JobScript, frontmatter parser, JobType
│   │   ├── Repositories/                   # Workspace, RepoFile, FileProjection
│   │   ├── Agents/                         # IAgent, AgentRequest, AgentResponse
│   │   └── Dashboards/                     # Dashboard, Section, Row, Column, Widget
│   ├── Creuser.Persistence/                # Marten + Wolverine config, migrations
│   ├── Creuser.Git/                        # libgit2sharp wrapper, branch strategies
│   ├── Creuser.Scripting/                  # Shell/Node/Python/.NET runners
│   ├── Creuser.Agents/                     # M.E.AI wiring + ToolLoopRunner
│   ├── Creuser.Auth.Abstractions/          # IUserStore, IPasswordHasher, IAuthProvider
│   ├── Creuser.Auth.Core/                  # Implementation
│   ├── Creuser.Auth.Providers.Local/       # Username/email + password
│   ├── Creuser.Auth.Providers.Google/      # OAuth (stubbed in v1)
│   ├── Creuser.Web/                        # ASP.NET host, serves SPA
│   │   ├── Program.cs
│   │   ├── Endpoints/                      # Grouped endpoint extensions
│   │   ├── Branding/                       # White-label config + middleware
│   │   └── wwwroot/                        # SPA build output lands here
│   └── Creuser.Web.Spa/                    # Quasar/Vue/TS app
│       ├── quasar.config.ts
│       ├── package.json
│       ├── tsconfig.json
│       └── src/
│           ├── boot/
│           ├── layouts/
│           ├── pages/
│           ├── components/
│           │   ├── widgets/                # WidgetRegistry components
│           │   └── dock/                   # dockview-vue integration
│           ├── stores/                     # Pinia
│           ├── composables/
│           └── api/                        # Generated TS client (hey-api)
├── tests/
│   ├── Creuser.Core.Tests/
│   ├── Creuser.Integration.Tests/          # Testcontainers for Postgres
│   └── Creuser.Web.Spa.Tests/              # Vitest
├── docker/
│   ├── Dockerfile                          # Multi-stage: SPA → .NET → runtime
│   ├── docker-compose.yml                  # Production-shape compose
│   └── docker-compose.dev.yml              # Local dev (separate SPA dev server)
├── docs/
│   ├── architecture.md                     # This document
│   ├── job-script-format.md                # Frontmatter spec
│   └── widget-development.md               # How to build custom widgets
├── .github/workflows/
│   ├── ci.yml                              # Build + test on PR
│   ├── edge.yml                            # Push to main → GHCR :edge
│   └── release.yml                         # Tag v* → GHCR + Docker Hub
├── .husky/
│   └── pre-commit                          # Runs `npx lint-staged`
├── README.md                               # Includes LGPL-3.0 license summary
├── LICENSE                                 # LGPL-3.0 full text
├── CONTRIBUTING.md
├── Creuser.slnx                            # XML solution format (.NET 10)
├── dotnet-tools.json                       # Pinned local .NET tools (CSharpier)
├── global.json                             # Pin .NET 10 SDK
├── package.json                            # Root orchestration: build / test / dev / lint
└── package-lock.json
```

## Data model

### Postgres schemas

Two schemas in one database:

- `mt` — Marten-managed document tables (workflows, runs, run steps, agent traces, branding config, secrets metadata, anything event-sourced or JSONB-native)
- `cr` — Creuser relational tables (DapperMatic-managed; entities, workspaces, job scripts, plugin registry, hot-path read models)

The split rule: append-mostly with rich JSONB querying → Marten. Relational with hot reads and explicit indexes → DapperMatic. Don't fight either tool by misusing it for the other's strengths.

### Core tables (DapperMatic-managed, schema `cr`)

See <https://dappermatic.mjczone.com/llms-full.txt>.

```txt
cr.workspaces              -- Configured repository connections
cr.workspace_settings      -- Type-specific configuration (git/s3/local)
cr.entities                -- Generic projection: (id, kind, schema_version, source_ref, data jsonb, projections jsonb)
cr.entity_refs             -- Edges between entities (graph queries for traceability)
cr.job_scripts             -- Frontmatter-parsed scripts (DB is canonical; filesystem is materialized)
cr.workflows               -- Workflow definitions (DB-canonical)
cr.dashboards              -- Dashboard layouts (sections, rows, columns, widgets)
cr.plugins                 -- Registered plugin metadata
cr.users                   -- Authentication users
cr.user_sessions           -- Active sessions
```

### Marten document types (schema `mt`)

```
WorkflowRun                -- Saga state, event-sourced; canonical run record
RunStep                    -- Individual step execution within a run
AgentTrace                 -- Full LLM conversation + tool-call trace per agentic step
BrandingConfig             -- Single-document table; current branding state
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

A workspace is a configured connection to a content source. Three implementations in v1 plans, with `Git` shipping in v0.1.0:

```csharp
public interface IRepositoryWorkspace
{
    WorkspaceId Id { get; }
    Task<IReadOnlyList<RepoFile>> ListFilesAsync(string? pathPrefix = null);
    Task<RepoFileContent> ReadFileAsync(string path);
    Task<WorkingTreeHandle> CheckoutAsync();        // Returns IDisposable lock
    Task SyncProjectionAsync();                     // Re-parse, update cr.entities
}

public interface IWritableWorkspace : IRepositoryWorkspace
{
    Task<CommitResult> CommitChangesAsync(
        WorkingTreeHandle handle,
        IReadOnlyList<FileChange> changes,
        CommitMessage message);
    Task PushAsync(WorkingTreeHandle handle);
}
```

### Git workspace specifics

Configuration:

- Repository URL (HTTPS or SSH)
- Authentication (SSH key path or PAT, stored in `/data/secrets/`)
- **Working branch** (default: `creuser/main`, configurable — e.g. `myorg/development`)
- **Source branch to sync from** (default: `main`, override per repo)
- **Mode toggle:** "Direct push to working branch" (default) OR "Open pull request" (with CI-tax warning)
- **Push frequency:** `every-commit` (real-time) OR `batched` (accumulate before push)

Operational vocabulary against git is small: `status`, `fetch`, `pull` (fast-forward only), `checkout`, `branch`, `add`, `commit`, `push`, `log`, `show`, `diff`, `rev-parse`, `ls-files`. No rebase, no merge-with-conflicts, no history rewriting. libgit2sharp handles everything; the `git` binary is in the image as fallback only.

### Concurrency model

Concurrent step execution against the same workspace is serialized via Postgres advisory lock keyed on workspace ID. Different workspaces run in parallel; same workspace is single-writer. Avoids libgit2 thread-safety pitfalls and keeps the audit log linear.

### Commit batching

A workflow saga step claims a workspace, gets a working tree at HEAD of the working branch, makes N file mutations, and at the end of the step produces exactly one commit with a structured message:

```txt
[creuser] <step.name> (run=<run_id> step=<step_id>)

<human_summary>

Updated:
- src/foo.md
- docs/index.md
```

Multiple steps in a workflow each produce their own commit. The sequential commit history on the working branch IS the audit log of what the platform did. `git log creuser/main` is itself a useful debugging tool.

## Workflow engine

Workflows are Wolverine sagas backed by Marten's event store. A workflow definition is a class that yields steps. Steps are Wolverine messages.

Two flavors of step:

**Static** — declared in the workflow definition at design time. Standard DAG semantics, dependencies between steps, retries, timeouts.

**Dynamic** — agentic steps can emit "spawn child workflow" or "insert step before resuming" messages back into the saga. The saga is the inspectable record — every step transition is an event in Marten — so the dashboard renders the full execution graph including dynamically-discovered branches.

Example: a SQL-DDL-parsing step encounters an undocumented table. It spawns a discovery sub-workflow that inspects how the table is used elsewhere in the codebase. That sub-workflow generates a documentation step that updates the DDL file. Original step resumes. Three explicit saga states, three step events, fully auditable.

Planning is *explicit*, not hidden in an LLM loop. A `PlannerAgent` produces a `WorkflowPlan` (a structured list of step descriptors with dependencies). The plan becomes a Wolverine saga. The planner can only emit plans against registered step types. This is the discipline that keeps the system inspectable.

## Job scripts

A job script is YAML frontmatter plus a body. Frontmatter declares:

```yaml
---
id: update-toc
name: Update Table of Contents
type: llm-tool-loop          # See "Job types" below
parameters:
  schema:
    type: object
    properties:
      directory: { type: string }
    required: [directory]
cron: "0 2 * * *"            # Optional
default_dependencies: []      # Other job IDs that must succeed first
allowed_commands: [git, rg, fd]   # Allow-list for run_shell tool
required_secrets: [anthropic.key]
retry_policy:
  max_attempts: 3
  backoff: exponential
timeout_seconds: 600
---

# Body — depends on `type`. For llm-tool-loop, this is the system prompt.
You are a documentation maintenance agent...
```

### Job types (v1)

Initial set of registered runners:

- `shell` — bash command(s)
- `node` — Node.js script
- `python` — Python script (via `uv run`)
- `csharp` — C# script as a single `.cs` file, executed via .NET 10 file-based apps (`dotnet run script.cs`). Frontmatter `#:package`/`#:property` directives are honored. See <https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps#cli-commands>.
- `llm-chat` — single prompt, structured output, no tools
- `llm-tool-loop` — bounded ReAct loop with tool registry
- `llm-planner` — produces a structured WorkflowPlan
- `http` — HTTP request with response parsing
- `git` — direct git operations on a workspace
- `sql` — parameterized SQL against a configured connection

### Storage

Job scripts live in BOTH the database AND the filesystem, with the database canonical:

- `cr.job_scripts` table is the source-of-truth
- `/data/scripts/{type}/{id}.{ext}` is materialized from the DB by a sync job
- Edit in the dashboard → DB updates first, filesystem follows
- Edit in the repo → reverse sync detects the change, prompts to merge
- Conflict policy: last-write-wins with audit trail

This dual-storage matters for LLM-generated jobs: agent writes to DB with `status='draft'`, human reviews rendered diff in dashboard, approval flips status to `active` and triggers the filesystem/git sync. **No agent-generated code reaches the filesystem without human review.**

## Agent layer

Built on Microsoft.Extensions.AI as the abstraction. Concrete providers wired via plugins:

- Anthropic (Claude Opus 4.7 default)
- OpenAI (GPT-5 family)
- Future providers via `IAgentProvider` interface

API keys live in `/data/secrets/` files (chmod 600), referenced from in-app config by filename, never stored in the database.

### Tool namespaces

Two distinct tool registries available to agents:

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

### ToolLoopRunner

Bounded ReAct implementation, ~200 lines. Takes a tool registry, a max-step budget, a max-token budget, and runs the loop. Tools are .NET methods decorated with attributes; the runner serializes them into the function-calling schema. Step transcripts are recorded as `AgentTrace` documents in Marten for inspection.

### Sandbox model

`run_shell` enforces a command allow-list per job, declared in frontmatter. Anything outside the list returns "command not permitted" to the agent. The agent can then try alternatives or report back.

For arbitrary code execution (agent writes a Python script and wants to run it), the runner spawns the script in a separate process under a non-root UID with bounded CPU/memory/timeout, in a working directory scoped to that run's tmp space. No Linux-namespace gymnastics in v1; just UID separation and ulimits. Sufficient for single-tenant on-premise; would need real sandboxing (Firecracker, gVisor) if multi-tenancy ever became a requirement.

## Dashboard composer

Opinionated layout: **Section → Row → Column → Widget**. Each level is a JSON document, stored in `cr.dashboards`.

```typescript
interface Dashboard {
  id: string;
  name: string;
  sections: Section[];
}
interface Section {
  id: string;
  title?: string;
  rows: Row[];
}
interface Row {
  id: string;
  columns: Column[];      // Column widths sum to 12 (Bootstrap-style)
}
interface Column {
  id: string;
  width: number;          // 1-12
  widgets: WidgetInstance[];
}
interface WidgetInstance {
  id: string;
  widgetType: string;     // Looked up in WidgetRegistry
  props: Record<string, unknown>;  // Conforms to widget's prop schema
}
```

Widgets are Vue components registered in a `WidgetRegistry` by name. Each widget declares a JSON Schema for its props. The "widget designer" is form-driven: pick a widget from the registry, the form auto-renders from its schema, drop it into a tile.

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

### Layout engine

Quasar's `q-page` provides the outer shell. Inside the dashboard content area, **dockview-vue** handles the dense tiling — splittable, dockable, resizable panels. This is what gives Creuser its "stock-trading-system feel" without fighting Quasar's grid.

## Authentication

The audience is **internal teams at one org** — operators and analysts working in a single Creuser deployment. Account creation is **invite-only**; there is no self-serve sign-up. Cookie-based sessions, Argon2id hashing, account state in Postgres `cr.users`, providers behind `IAuthProvider` so Google OAuth and OIDC can land later without rewiring callers.

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

Plugins are .NET assemblies dropped into `/data/plugins/`. Discovered at startup. Manifest declares:

- Plugin name, version, author
- Required Creuser version range
- Provided extensions (job types, workspace types, widgets, agent providers, parsers)
- Required tools (host-OS binaries the plugin's runners need)

No hot-reload in v1. Plugin changes require restart. Operators copy DLLs in via their normal infrastructure (Railway file mounts, Docker volume updates, etc.).

## Persistent volume layout

```
/data/
├── secrets/                    # API keys, OAuth secrets (chmod 600)
│   ├── anthropic.key
│   ├── openai.key
│   └── google-oauth.json
├── keys/                       # ASP.NET data protection keys
├── workspaces/                 # Checked-out git repos and S3 caches
│   ├── example-monorepo/
│   │   ├── .git/
│   │   └── ...
│   └── another-repo/
├── plugins/                    # Drop-in DLLs
├── scripts/                    # Materialized job scripts (synced from DB)
├── prompts/                    # Materialized prompt templates (synced from DB)
├── logs/                       # Serilog rolling files
└── tmp/                        # Agent scratch space, periodically cleaned
```

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
- Native Python and Node script runners (vs. shell-out)
- Hot-reload plugin system
- Full RBAC beyond single-admin
- OIDC integration for SSO (currently planned for v0.2)
- Workflow import/export
- Embedded observability dashboard
- Real sandboxing for arbitrary code execution (Firecracker / gVisor)
- Multi-tenant deployment mode (currently architecturally excluded)

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
