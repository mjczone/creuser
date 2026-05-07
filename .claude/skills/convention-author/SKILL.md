---
name: convention-author
description: Add, update, remove, validate, and test entity conventions in this Creuser workspace. Use this skill whenever the user asks to add a relationship to a convention, change how a kind resolves edges, carve a frontmatter list into folders, declare an inverse, set computed accessors, or otherwise edit a `.creuser/conventions/*.yaml` file. Apply this skill even when the user doesn't say "convention" — phrases like "add an ADR-to-plan link", "make `related` show ADRs and plans separately", "version-bump the slug template" all map to convention edits. The skill's whole point is to make these changes feel like seconds-long structured edits, not YAML hand-editing.
---

# Convention authoring

Conventions are the schema for how files in `.creuser/conventions/*.yaml` map files in the working tree into entities + typed edges in the Creuser projection. **Don't edit YAML by hand.** Call the structured ops below — they validate before writing, surface deterministic errors, and keep the assistant's edits indistinguishable from the SPA editor's.

## The whole grammar in 30 seconds

A convention declares: which files match (`match.glob`), how to derive a slug (`slug`), what counts as metadata (`metadata.source`, `metadata.computed`, `metadata.required`), and a list of `relationships` rules. Each rule renders as one navigable folder under matching entities in the CDFS file manager.

A relationship rule has:

| Field | Purpose |
|---|---|
| `kind` | machine-readable edge label, snake_case (`related`, `supersedes`, `related_adrs`). Required. |
| `name` | CDFS folder display name. Defaulted from `kind` when absent. |
| `icon` / `description` / `order` | CDFS folder rendering. |
| `source` | where to read values: `frontmatter.<key>` / `path-template:{file_dir}/index.md` / `glob:packages/db/**` / `literal:[...]`. |
| `filter` | optional value-level filter so one source dispatches into many folders. `{kind: glob, pattern: ...}` / `{kind: regex, ...}` / `{kind: type, pattern: url}`. |
| `interpret` | how each consumed value resolves: `auto` (URL → glob → path → slug), `path`, `slug`, `glob`, `url`. Default `auto`. |
| `target_kind` | `any`, a single kind, or `[adr, plan]`. |
| `inverse` / `inverse_name` / `inverse_icon` | reverse-edge label + display. Auto-emits a mirrored ref. |
| `metadata` | per-edge metadata template; `${value}` substitutes the raw input. |

## Endpoints — call these, not YAML edits

All endpoints live under `/api/workspaces/{slug}/conventions/`. Reads are User-role; mutations require Admin.

| Op | Verb / Path | When to use |
|---|---|---|
| `describe_capabilities` | GET `/capabilities` | First call per session. Pulls schema, accessor registry, workspace-known kinds, common patterns. **Cache it** — same payload across edits. |
| `validate_convention` | POST `/validate` body `{yaml}` | Pre-flight a hand-written YAML or a synthesized one before applying. |
| `test_convention` | POST `/{id}/test` body `{againstPath}` | Dry-run resolve a single file against this convention. Confirms a rule produces the entities + refs you expect. |
| `add_relationship` | POST `/{id}/relationships` body = rule shape | Add a rule. Fails 400 if the kind already exists. |
| `update_relationship` | PUT `/{id}/relationships/{kind}` body = rule shape | Replace an existing rule wholesale. Use when changing source/filter/interpret. |
| `remove_relationship` | DELETE `/{id}/relationships/{kind}` | Drop a rule. |

## The canonical flow

> User: *"Add a relationship to ADRs on the plan convention."*

Assistant (one turn):

1. **Capabilities** — `GET /api/workspaces/<slug>/conventions/capabilities` (cached; skip if you've called it this session). Confirm `adr` is in `workspaceKinds`.
2. **Pick defaults** — for "to ADRs" with no other context, the right shape is the canonical "related-adrs filtered carve" pattern from `commonPatterns`. If the convention already has a `related` field, carve via `filter`; if not, pick a kind-and-name pair.
3. **Add** — `POST /api/workspaces/<slug>/conventions/plan/relationships` with:
    ```json
    {
      "kind": "related_adrs",
      "name": "Related ADRs",
      "source": "frontmatter.related_adrs",
      "interpret": "auto",
      "targetKind": "adr",
      "inverse": "related_plans",
      "inverseName": "Related Plans",
      "order": 100
    }
    ```
4. **Confirm** — `POST /api/workspaces/<slug>/conventions/plan/test` with a sample plan file path. Assert the edit produced the rule and the dry run resolves cleanly.
5. **Report** — one short sentence: what got added, the new frontmatter key, the CDFS folder name, and the reverse edge.

Time-to-result: under 5 seconds. No YAML editing. No ambiguity.

## Smart defaults — apply when the user underspecifies

When the user asks "add a relationship to X" without spelling out every field:

- **`name`**: humanize `kind` ("related_adrs" → "Related ADRs"). Override only when the user gave one explicitly.
- **`source`**: default to `frontmatter.<kind>`. Document authors will use that key.
- **`interpret`**: `auto` unless the user named a specific shape ("link to a code path" → `path`; "link to a URL" → `url`).
- **`target_kind`**: derive from the user's intent. "to ADRs" → `adr`. "to anything" → `any`. "to ADRs or plans" → `[adr, plan]`.
- **`inverse`**: for symmetric relationships (`related`), inverse = kind. For directional (`supersedes`), pick the natural opposite (`superseded_by`). When unsure, ask one short clarifying question, otherwise omit.
- **`order`**: 100 default; bump down (10/20/30) when explicitly making something prominent.
- **`metadata`**: omit. Only add when the user specifies what to record per-edge.

## Common patterns (copy these)

### One frontmatter list, multiple folders (filter dispatch)

The user has a flat `related: [...]` field with mixed paths and wants typed CDFS folders.

```json
{
  "kind": "related_adrs",
  "name": "Related ADRs",
  "source": "frontmatter.related",
  "filter": { "kind": "glob", "pattern": "docs/ADR/**/*.md" },
  "interpret": "path",
  "targetKind": "adr"
}
```

Then a second add for `related_plans` with `pattern: "docs/PLANS/**/*.md"`, etc. Each becomes a CDFS folder.

### Symmetric `related`

```json
{
  "kind": "related",
  "name": "Related",
  "source": "frontmatter.related",
  "interpret": "auto",
  "targetKind": "any",
  "inverse": "related"
}
```

### Directional ADR pair

```json
{
  "kind": "supersedes",
  "name": "Supersedes",
  "source": "frontmatter.supersedes",
  "interpret": "auto",
  "targetKind": "adr",
  "inverse": "superseded_by",
  "inverseName": "Superseded by"
}
```

### Path-template parent

```json
{
  "kind": "parent",
  "name": "Parent",
  "source": "path-template:{file_dir}/index.md",
  "interpret": "path",
  "targetKind": "doc",
  "inverse": "children",
  "inverseName": "Children"
}
```

### Code references via glob

For an ADR that references implementation code:

```json
{
  "kind": "related_code",
  "name": "Related Code",
  "source": "frontmatter.related",
  "filter": { "kind": "type", "pattern": "glob" },
  "interpret": "glob",
  "targetKind": "any"
}
```

The author then writes `related: [packages/database/**/*.ts]` and the resolver expands the glob to N file refs.

## Validation gates

Every mutating op runs the resulting YAML through the loader before writing. If validation fails the file is **not modified** and you receive a 400 with the error message. Surface that error to the user verbatim — don't paper over it.

If `validate_convention` returns errors and you can fix them deterministically (typo in a kind id, missing `name`), re-emit the corrected request. If the error needs a judgment call, ask the user.

## Constraints

- **Comments are not preserved** through structured edits. If the user has explanatory comments in the YAML, warn them once before the first edit; subsequent edits don't re-warn.
- **Mutations require Admin role.** Reads (`capabilities`, `validate`, `test`) work for any signed-in user. If a non-admin tries to add/update/remove a rule the API returns 403; surface that as "you need admin role to edit conventions" — don't try to bypass.
- **Convention id is immutable from this surface.** To rename a convention's `id`, the user edits the file directly; structured ops won't.

## When NOT to use these ops

- The user wants to author a wholly new convention (not edit an existing one). The CLI scaffold (`creuser conventions new`) lands in Stage D; until then, write the YAML directly.
- The user wants to remove a convention entirely. Delete the file by hand.
- The user wants to bulk-edit many conventions. Loop the ops, but mention it'll be N round-trips — they may want the SPA bulk editor instead (Stage E).

## After every edit

Summarize in **one** sentence: what changed, what frontmatter key the user now writes, what CDFS folder will appear. Don't list every field of the rule — they can `GET /capabilities` if they want the full spec.
