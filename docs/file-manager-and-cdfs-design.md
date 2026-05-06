# File manager + Convention-Driven File System — design

Two complementary visualization surfaces for "what is in this workspace?"
that together close Creuser's biggest visibility gap: today, an admin who
has just connected a remote workspace through the platform has no
first-class way to see the workspace's actual content. The `/api/.../entities`
endpoint, the AI assistant, and dashboards-with-widgets all *can* answer
specific questions, but none of them satisfies the basic operator need to
"look at what I have."

This doc fixes the architectural framing, the on-disk schema, the wire
format, the convention-action model, and the staged delivery plan so the
slice can be built without re-litigating the shape per commit.

## Scope (v0.1.x)

**In scope:**

- A **raw file-system view** (FS view) — folders + files in the workspace
  working tree, like a stripped-down [tinyfilemanager](https://tinyfilemanager.github.io/).
  Read-only browse, click a file to open it in the existing Monaco
  reader. Surfaces in two places: as a settings tab (`/w/:slug/settings/files`)
  and as a dashboard widget that can be dropped on any dashboard.
- A **Convention-Driven File System view** (CDFS view) — same UI shape,
  different data adapter. Root rows are conventions; each convention's
  matched entities expand under it; metadata fields render as columns;
  `entity_refs` show as expandable inline children (refs_out / refs_in).
  Surfaces as the default content of a seeded `Operations` dashboard
  group's `cdfs` dashboard, and as a standalone widget.
- **Convention-declared actions** — additive YAML schema for
  `actions:` on a convention, surfaced as right-click context menus on
  CDFS rows. Each action declares what it runs (file-mutate script,
  agent prompt, job-runner job) and rules (`when:`, `confirm:`).
  The schema lands first; the runtime hookup comes in stages as
  the job runner matures.
- **Text-file CRUD in stage 1**: browse + view, edit existing text
  files in Monaco, save edits, create new text files, delete files
  and folders. All goes through the existing `applyWorkspaceChanges`
  pipeline (which already supports `write` and `delete` actions on
  string content). The convention editor is the prior art —
  same Monaco wiring, same save flow, just repointed at any file
  path. No backend pipeline work needed for this slice.

**Out of scope (post-v0.1.x):**

- **Binary file uploads** (images, PDFs, archives, anything that
  isn't a string). The existing `WorkspaceFileChange.Content` is
  `string?` JSON-serialized; binaries need either a multipart upload
  endpoint or a base64-encoded variant + an explicit
  `ContentEncoding` flag on the change record. Contract change held
  until there's a real consumer.
- Drag-and-drop reordering, drag-to-folder moves, multi-select bulk
  ops. All possible later; v0.1.x ships single-file actions.
- Search / filter inside either view. Both views render at most ~500
  rows; pagination + search slot in when a real workspace exceeds that.
- Right-click → "Create convention from this path" pre-filled glob
  flow. Authoring stays in the existing Conventions settings page;
  this surface is for browsing + acting, not for authoring.
- An "Open With…" registry that lets multiple applications register
  per kind. Convention-declared actions cover the same ground as a
  per-convention-defined registry; the global open-with model lands
  if and when a real cross-convention need shows up.

## Architectural framing

### Why two views, not one

The FS view answers *"is this file actually on disk?"* — operator's
mental model. The CDFS view answers *"is this file projected into the
graph the platform / AI assistant operates on?"* — platform's mental
model. The **gap between them is itself information**: a file on disk
that's missing from CDFS tells you a convention's glob is wrong, a
required frontmatter field is missing, or the file is excluded by
something. Today admins discover that gap only by running queries; both
views together make it eyes-on-glass.

Building one view that "tries to do both" hides the diagnostic. Two
sibling adapters over the same UI primitive is the cleaner shape.

### One UI primitive, two data adapters

```
<FileManagerWidget :data-adapter="fsAdapter | cdfsAdapter" />
```

The widget owns layout (breadcrumb, table, row click, context menu, file
viewer pane). The data adapter owns the rows + columns + actions. This
is the single biggest design lever — once the widget is right, switching
adapters costs almost nothing.

Adapters expose:

```ts
interface FileManagerAdapter {
  // Rows for the current "folder" (by id — could be a path or a kind/
  // convention id depending on adapter).
  list(folderId: string | null): Promise<FileManagerRow[]>;
  // Per-row context-menu actions. Adapter inspects the row + workspace
  // capability flags to gate.
  actionsFor(row: FileManagerRow): FileManagerAction[];
  // Default columns for this view. CDFS adapter returns columns
  // computed from the convention's metadata schema; FS adapter returns
  // a fixed [name, size, kind, mtime] list.
  columns(): FileManagerColumn[];
  // Render a file's content in the side pane (FS: read-via-Monaco;
  // CDFS: entity-detail with refs + metadata).
  preview(row: FileManagerRow): Promise<FileManagerPreview>;
}
```

### Surfaces

**Workspace Settings → File Manager** (`/w/:slug/settings/files`) — FS
adapter, full pane. One-off admin tasks: drill in, view a file, run
file-level actions. Settings-shape: structural, not operational.

**Operations dashboard → CDFS widget** — CDFS adapter, dashboard tile.
Operational visibility + entity actions. The Operations dashboard group
gets seeded on workspace creation (alongside Home) when this ships,
with the CDFS widget as its default content.

**Both as widgets** — registered in the widget palette so admins can
drop either adapter onto any dashboard. An Engineering dashboard might
have a small `docs/ADR/` FS tile; a domain-modeling dashboard might
have a CDFS view filtered to `kind=business_rule`.

### Right-click menus = the platform unlock

A file manager without convention-declared actions is "yet another
git-aware file browser." The actions are what make CDFS Creuser-shaped:
*"select 5 ADRs, mark all as accepted"* / *"generate a summary of this
business rule"* / *"find all skills that reference this RFC"* — these
are workspace-content actions, not file-system actions, and they belong
attached to the kinds they operate on, not to a global toolbar.

Pattern (additive to `Convention.cs`):

```yaml
id: adr
extends: creuser:standard/adr
actions:
  - id: mark-accepted
    label: "Mark as accepted"
    icon: check_circle
    when: "metadata.status == 'draft'"     # optional gate; row is filtered
                                            # out of the menu when false
    confirm: required                       # optional; UI shows confirm dialog
                                            # before firing
    runs:
      kind: file-mutate                     # file-mutate | agent-prompt | job
      script: ".creuser/jobs/mark-adr-accepted.cs"
  - id: find-impact
    label: "Find downstream impact"
    icon: hub
    runs:
      kind: query                           # synchronous; result rendered in panel
      tool: find_references
      args:
        target_path: "$entity.path"
  - id: generate-summary
    label: "Generate AI summary"
    icon: auto_awesome
    runs:
      kind: agent-prompt
      prompt: ".creuser/prompts/adr-summary.md"
      output:
        target: frontmatter.description     # or `body`, `comments`, etc.
```

`runs.kind`:

- `file-mutate` — runs a workspace-side script that emits FileChange
  records, dispatched to the existing `applyWorkspaceChanges` pipeline.
  Available today (the pipeline is built); the script-runner side is
  still partial.
- `agent-prompt` — invokes the AI assistant tool loop with the
  prompt + entity context, optionally writes the result to a target
  field. Available today (chat tool bridge is built).
- `query` — synchronous call to a projection-toolset tool, result
  rendered inline. Available today.
- `job` — invokes a workspace job by id. Blocked on the job runner.

`when:` is a tiny expression evaluated against the row's metadata —
v0.1.x supports literal-equality (`metadata.status == 'draft'`) and
nothing else. Full expression language defers.

`confirm: required` triggers a Quasar confirm dialog before fire.
v0.1.x value `required` only; `multi-row-required-N` for "confirm if
N+ rows selected" defers to multi-select.

## Wire format

### File-system list endpoint (new)

```
GET /api/workspaces/{slug}/files/list?path=<rel-path>
```

Returns folders + files at the given path. Path defaults to root.
Paths follow the same safety rules as `/files` (no `..`, no
absolute, no `.git/`). Provider-dispatched: git workspaces resolve
to the working tree, local workspaces to the configured path. Read
counterpart to the existing `/files` (which returns one file's
contents). Response:

```ts
interface WorkspaceFolderListing {
  path: string;                  // canonicalized request path
  folders: WorkspaceFolderEntry[];
  files: WorkspaceFileEntry[];
}

interface WorkspaceFolderEntry {
  name: string;
  path: string;
  childCount: number | null;     // null when computing children would
                                  // require a recursive descent (large
                                  // dirs); UI just shows ">"
}

interface WorkspaceFileEntry {
  name: string;
  path: string;
  sizeBytes: number;
  modifiedAt: string;            // ISO-8601 UTC
  // Optional: a hint for the renderer. v0.1.x derives from extension
  // ("text", "image", "binary") so the UI can pick the right preview.
  contentKind: 'text' | 'image' | 'binary' | 'unknown';
}
```

### CDFS-list endpoint (new, thin wrapper)

```
GET /api/workspaces/{slug}/cdfs/conventions
```

Returns conventions + their entity counts so the CDFS root row list
can render without N round-trips. Each convention is one row; the row's
"folder" view fetches `query_entities` with the matching kind.

```ts
interface CdfsConventionRow {
  id: string;                    // convention id (becomes the row label)
  description: string | null;
  matchGlob: string;
  entityCount: number;
  // Action descriptors collected from convention YAML; client
  // surfaces in the right-click menu.
  actions: CdfsActionDescriptor[];
}

interface CdfsActionDescriptor {
  id: string;
  label: string;
  icon: string | null;
  when: string | null;            // raw expression; client evaluates
                                   // against the row's metadata
  confirm: 'required' | null;
  runs: CdfsActionRuns;
}
```

CDFS entity rows reuse the existing `query_entities` response (the
`EntitySummary`); the widget reads metadata + refs from the same shape.

### Right-click action endpoints (deferred per stage)

Stage 3 fills these in once the supporting machinery is in place. Each
action kind has its own dispatch path:

- `file-mutate` → `POST /api/workspaces/{slug}/changes` (built).
- `agent-prompt` → invokes the chat tool loop with the prompt and
  entity context. Returns the assistant's reply or a writeback target.
- `query` → invokes the projection toolset directly (no LLM hop).
- `job` → `POST /api/workspaces/{slug}/jobs/{jobId}/run` (existing).

The client side dispatches to the right path based on `runs.kind`;
no single "actions" endpoint at the platform level.

## Convention schema additions

`Convention.cs` (Core record) gains:

```csharp
public IReadOnlyList<ConventionAction> Actions { get; init; } = [];

public sealed record ConventionAction(
    string Id,
    string Label,
    string? Icon,
    string? When,                  // raw expression string; v0.1.x evaluator
                                    // supports literal equality only
    string? Confirm,                // null | "required"
    ConventionActionRuns Runs
);

public sealed record ConventionActionRuns(
    string Kind,                    // "file-mutate" | "agent-prompt" | "query" | "job"
    string? Script,                 // for file-mutate
    string? Prompt,                 // for agent-prompt
    string? Tool,                   // for query (e.g. "find_references")
    Dictionary<string, string>? Args,
    string? JobId,                  // for job
    ConventionActionOutput? Output
);

public sealed record ConventionActionOutput(
    string Target                   // "frontmatter.<key>" | "body" | "comments"
);
```

`ConventionLoader.cs` parses the new block; `ProjectionScanner.cs`
ignores it (actions don't gate matching, they decorate). The standard
library doesn't need updates — `actions:` is optional everywhere.

## Staged delivery

### Stage 1 — FS view with text CRUD (~1-2 days)

Ships browsing + the full read/edit/create/delete loop on text
files. All writes route through the existing
`applyWorkspaceChanges` pipeline, so the SignalR-driven Commit
badge in the header increments naturally as files mutate.

- New endpoint `GET /api/workspaces/{slug}/files/list?path=...`
  delegating to the existing provider's `ResolveRootAsync` + a
  directory-list helper.
- New Vue component `FileManagerWidget.vue` — folder/file table,
  breadcrumb, right pane that swaps in:
  - Text file: Monaco editor (read-only on open, "Edit" button
    flips to editable; "Save" invokes `applyWorkspaceChanges` with
    a `write` action).
  - Image: rendered inline.
  - Binary / unknown: "binary file, N bytes" placeholder with a
    "view as text anyway" escape for unknown extensions like
    `.creuser` config files.
- Toolbar actions:
  - **New file** — prompt for path (relative to current folder;
    nested paths like `subdir/foo.md` allowed — the backend
    creates parent dirs automatically). Opens an empty Monaco
    buffer; first save creates the file.
  - **Refresh** — re-fetch the listing.
- Right-click row menu:
  - **Open** (default).
  - **Delete** — confirm dialog → `applyWorkspaceChanges` with a
    `delete` action. Files and folders both deletable; deleting a
    folder dispatches one delete per file inside (single batched
    call).
- New settings tab at `/w/:slug/settings/files` rendering the widget
  as a full pane.
- Widget registered in the dashboard widget palette so admins can
  drop it on any dashboard.

Stage 1 alone closes the visibility gap and ships add / edit /
delete via the same primitive the convention editor uses. Binary
upload is the only "file manager" feature that genuinely defers,
because of the contract change required.

### Stage 2 — CDFS adapter (~1 day)

Ships:
- New endpoint `GET /api/workspaces/{slug}/cdfs/conventions` returning
  one row per convention with entity counts.
- `CdfsAdapter` Vue composable: same shape as `FsAdapter`, different
  data sources (conventions + `query_entities` + `get_entity` for refs).
- Default columns derived from the convention's matched entities'
  metadata (union of frontmatter keys + size cap of ~10 columns to
  keep tables readable).
- Operations dashboard group seeded on workspace create alongside Home,
  with a `cdfs` dashboard whose default layout is one CDFS widget
  full-canvas.

CDFS reuses 80%+ of the FileManagerWidget; only the data adapter
changes.

### Stage 3 — Convention-declared actions (schema + agent-prompt dispatch shipped 2026-05-06)

Status: schema + UI surface + `agent-prompt` dispatch live; remaining
dispatch paths (`file-mutate`, `query`, `job`) parse and surface but
notify "not dispatched yet" when invoked. Wiring those is a one-PR
follow-on per kind.

Ships (live):
- `Convention.cs` schema additions: `Actions`, `ConventionAction`,
  `ConventionActionRuns`, `ConventionActionOutput`.
- `ConventionLoader.cs` parses the optional `actions:` block (extends
  the `MergeOnto` shallow-merge so a convention can inherit a base's
  actions and add more).
- `CdfsConventionRow.actions` populates from the parsed YAML; the
  CDFS widget's per-row context menu surfaces them.
- Client-side `when:` evaluator: literal equality only
  (`status == "draft"` or `metadata.status == "draft"`); unrecognized
  expressions return true so an action stays visible (fail open) —
  upgrading the evaluator narrows visibility, never widens it.
- `confirm: required` shows a Quasar dialog before dispatching.
- `agent-prompt` dispatch: opens the chat assistant and sends the
  templated prompt with `{path}` / `{slug}` / `{kind}` /
  `{metadata.<key>}` interpolation, plus an automatic entity-context
  block so the assistant can reason about the entity even if the
  template author didn't wire every field in.

Deferred:
- `file-mutate` dispatch (per-entity scripted change).
- `query` dispatch (direct projection-tool invocation, no LLM hop).
- `job` dispatch (waits on the job runner maturing).
- Output writeback per `runs.output.target` (frontmatter merge / body
  replace / chat-only). For `agent-prompt`, output today is whatever
  the assistant says in chat — writeback is the next slice.

### Stage 4 — Binary uploads + advanced file ops (~1 day, deferred)

The genuinely-deferred slice. Needs the contract change to
`WorkspaceFileChange` (multipart endpoint or base64 + an explicit
`ContentEncoding` flag) before any of this is reachable. Until a
real consumer asks, this stays parked.

- Binary file upload (drag-drop into the file manager → POST a
  multipart payload → backend writes bytes via the existing
  filesystem path).
- Bulk multi-select operations (delete N files, move N files).
- Rename/move via drag-and-drop or a context-menu "Rename".
  Trivially expressible as `delete-old + write-new` in one
  `applyWorkspaceChanges` batch (atomic per call), but the UX
  flow defers with the rest of this slice.

## Reuse map

The existing surface that this design leans on:

- `IWorkspaceProvider.ResolveRootAsync` (Core) — already does the
  per-provider workspace-root dispatch; the new `/files/list` endpoint
  uses it verbatim.
- `Workspaces.applyWorkspaceChanges` (existing endpoint) — file
  deletion in stage 4 reuses this; no new write paths.
- `Workspaces.getWorkspaceFile` (existing) — file-content reads in the
  preview pane reuse this.
- `Projections.queryEntities` / `Projections.getEntity` (existing) —
  CDFS row + detail reads reuse these.
- The Monaco editor wiring used in `JobScriptEditor.vue` and
  `WorkspaceSettingsConventionsPage.vue` — the file-preview pane uses
  it for text content.
- `useWorkspaceStatusStore` SignalR pattern — actions that mutate
  state (Stage 3 file-mutate, Stage 4 delete) get the same realtime
  status broadcast for free; the header Commit badge increments as
  files are written.
- The widget primitive in `DashboardPage.vue` — the FileManagerWidget
  registers as one widget kind, like any other.

## Risks + things deferred

**Performance on large directories**: a 5,000-file folder rendered as
a single table is a problem. v0.1.x ships with a hard cap (e.g. 500
rows) and a "this folder has more entries — drill in or use the
Conventions view" callout when exceeded. Real pagination defers.

**Action evaluator scope creep**: `when: "metadata.status == 'draft'
&& metadata.priority > 5"` is tempting. Resist. The literal-equality
evaluator stays the contract; if a complex action needs richer
predicates, it lives in the action's script body, not in the
declarative gate. Adopting a real expression language locks the
schema in ways that hurt later.

**`confirm: required` blast radius**: stage 3 ships single-row
confirm only. Multi-row select + bulk action lands with the multi-
select feature, not before. Until then, an admin who wants to "mark
all drafts as accepted" runs the action 17 times or writes a job —
intentionally annoying.

**Action provenance**: actions inherit via `extends:` like everything
else. A workspace convention extending `creuser:standard/adr` gets
the standard's actions automatically; overriding actions replaces
the inherited list (lists fully replace, scalars override-or-inherit
— same merge semantics as the rest of the schema).

**FS view writability**: stage 4 adds delete. Upload, rename, mkdir
all defer because (a) upload needs binary-payload contracts, (b)
rename is git-aware (a real rename is a delete+create pair the git
layer should detect and stage as a rename), (c) mkdir without
content is a no-op for git anyway. None of these block the value
prop; all of them are easy adds when a consumer demands them.

**Right-click "Create convention from path"**: explicitly deferred.
The argument earlier was that auto-glob inference encourages
half-formed conventions. The current Conventions settings page is
the authoring surface; FS-view → "Create convention" is a
shortcut we can add later when the convention authoring flow is
otherwise solid.

## Verification

1. **Stage 1**: a new workspace with a non-trivial source tree
   syncs, `/w/:slug/settings/files` shows folders + files; clicking
   any file renders content in the right pane; widget drops onto
   the home dashboard from the palette and renders identically.
2. **Stage 2**: same workspace shows in the Operations →
   `cdfs` dashboard with conventions as rows, each expandable to
   its matched entities; metadata columns reflect the convention's
   frontmatter; clicking an entity opens its detail with refs.
3. **Stage 3**: a workspace convention with one `actions:` entry
   (e.g. `kind: query`, `tool: find_references`) renders the
   action in the row's right-click menu; clicking dispatches and
   the result renders in a panel. Same flow for `kind: file-mutate`
   end-to-end against a tiny script.
4. **Stage 4**: file deletion via right-click triggers the
   `applyWorkspaceChanges` pipeline; the file vanishes; the header
   Commit badge increments by one (provider broadcasts status).
5. Lint + typecheck clean per stage; SignalR status updates
   reflect in real time.
