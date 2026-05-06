<template>
  <div class="cr-conv-page">
    <header class="cr-conv-header">
      <div>
        <h1 class="text-h6 q-ma-none">Conventions</h1>
        <p class="cr-conv-subhead">
          YAML files under <code>.creuser/conventions/</code> in your workspace's working tree. Each
          convention maps a file pattern (plus frontmatter shape) into typed entities for the
          projection. Edits save as a single git commit on the working branch and re-fire
          projection-sync automatically.
        </p>
      </div>
      <q-space />
      <q-btn
        flat
        dense
        no-caps
        icon="refresh"
        size="sm"
        :loading="loading"
        @click="loadConventions"
      >
        <q-tooltip>Reload from working tree</q-tooltip>
      </q-btn>
    </header>

    <div v-if="errors.length > 0" class="cr-conv-errors">
      <div class="cr-conv-errors-title">
        <q-icon name="error_outline" size="16px" />
        {{ errors.length }} load error{{ errors.length === 1 ? '' : 's' }}
      </div>
      <ul class="cr-conv-errors-list">
        <li v-for="(err, i) in errors" :key="i">
          <code v-if="err.source">{{ err.source }}</code>
          <span v-else class="cr-conv-errors-no-source">(no source path)</span>
          — {{ err.message }}
        </li>
      </ul>
    </div>

    <div class="cr-conv-body">
      <aside class="cr-conv-list">
        <div class="cr-conv-list-header">
          <span>Defined conventions</span>
          <q-btn
            flat
            dense
            no-caps
            icon="add"
            label="New"
            size="sm"
            class="cr-conv-list-new"
            @click="onNewConvention"
          />
        </div>

        <div v-if="loading" class="cr-conv-list-empty">Loading…</div>
        <div v-else-if="conventions.length === 0" class="cr-conv-list-empty">
          None found. Click <strong>New</strong> to create
          <code>.creuser/conventions/&lt;id&gt;.yaml</code>.
        </div>
        <ul v-else class="cr-conv-list-items">
          <li
            v-for="conv in conventions"
            :key="conv.id"
            class="cr-conv-item"
            :class="{ 'cr-conv-item--active': conv.sourcePath === selectedPath }"
            @click="selectConvention(conv)"
          >
            <div class="cr-conv-item-id">{{ conv.id }}</div>
            <div class="cr-conv-item-glob">
              <code>{{ conv.glob }}</code>
            </div>
            <div v-if="conv.sourcePath" class="cr-conv-item-source">
              {{ conv.sourcePath }}
            </div>
            <div v-else class="cr-conv-item-bundled">bundled</div>
          </li>
        </ul>

        <details class="cr-conv-help">
          <summary>Schema reference (all options)</summary>
          <p class="cr-conv-help-intro">
            Every field a convention can declare, with inline comments. Most are optional — the
            smallest valid file is just <code>id</code> + <code>match.glob</code>.
          </p>
          <pre class="cr-conv-std-yaml cr-conv-schema-yaml">{{ schemaReference }}</pre>
          <div class="cr-conv-std-actions">
            <q-btn
              flat
              dense
              no-caps
              size="xs"
              icon="content_copy"
              label="Copy reference"
              @click="copySchemaReference"
            />
          </div>
        </details>

        <details class="cr-conv-help">
          <summary>Bundled standard library</summary>
          <p class="cr-conv-help-intro">
            Workspace conventions can <code>extends:</code> any of these to inherit its match globs,
            slug derivation, metadata source, and relationships. Click an entry to expand its full
            YAML.
          </p>
          <div v-if="standardsLoading" class="cr-conv-help-empty">Loading…</div>
          <div v-else-if="standards.length === 0" class="cr-conv-help-empty">
            None loaded — check API logs.
          </div>
          <ul v-else class="cr-conv-standards">
            <li v-for="entry in standards" :key="entry.reference" class="cr-conv-std-item">
              <details>
                <summary class="cr-conv-std-summary">
                  <code class="cr-conv-std-ref">{{ entry.reference }}</code>
                  <span v-if="standardLine(entry)" class="cr-conv-std-desc">
                    {{ standardLine(entry) }}
                  </span>
                </summary>
                <pre class="cr-conv-std-yaml">{{ entry.yaml }}</pre>
                <div class="cr-conv-std-actions">
                  <q-btn
                    flat
                    dense
                    no-caps
                    size="xs"
                    icon="content_copy"
                    label="Copy extends:"
                    @click="copyExtends(entry.reference)"
                  />
                  <q-btn
                    flat
                    dense
                    no-caps
                    size="xs"
                    icon="add"
                    :label="`Use as base for new`"
                    @click="newFromStandard(entry)"
                  />
                </div>
              </details>
            </li>
          </ul>
        </details>
      </aside>

      <main class="cr-conv-editor">
        <div v-if="!selectedPath" class="cr-conv-editor-empty">
          <q-icon name="article" size="40px" />
          <p>Pick a convention on the left, or click <strong>New</strong> to create one.</p>
        </div>

        <template v-else>
          <div class="cr-conv-editor-header">
            <q-input
              v-model="editingPath"
              dense
              outlined
              label="Path (workspace-relative)"
              :readonly="!isNew"
              class="cr-conv-editor-path"
            />
            <q-btn
              v-if="!isNew"
              flat
              dense
              no-caps
              icon="delete_outline"
              color="negative"
              :disable="saving || deleting"
              :loading="deleting"
              aria-label="Delete convention"
              @click="onDelete"
            >
              <q-tooltip>Delete this convention file</q-tooltip>
            </q-btn>
            <q-space />
            <q-btn
              flat
              dense
              no-caps
              label="Discard"
              :disable="!dirty || saving"
              @click="discardChanges"
            />
            <q-btn
              unelevated
              dense
              no-caps
              label="Save"
              color="primary"
              :loading="saving"
              :disable="!dirty || !editingPath.trim()"
              @click="onSave"
            >
              <q-tooltip>
                Save the file. Commit/Push (when supported) live in the header.
              </q-tooltip>
            </q-btn>
          </div>

          <vue-monaco-editor
            v-model:value="editingContent"
            theme="vs-dark"
            language="yaml"
            :options="editorOptions"
            class="cr-conv-monaco"
          >
            <template #default>
              <div class="cr-conv-monaco-loading">Loading editor…</div>
            </template>
            <template #failure>
              <div class="cr-conv-monaco-loading cr-conv-monaco-error">
                Editor failed to load. Check network access to jsdelivr.net.
              </div>
            </template>
          </vue-monaco-editor>

          <footer v-if="dirty" class="cr-conv-editor-dirty">
            <q-icon name="edit" size="14px" />
            Unsaved changes — Save commits to the working branch and re-fires projection-sync.
          </footer>
        </template>
      </main>
    </div>
  </div>
</template>

<script setup lang="ts">
/**
 * Convention editor — list of `.creuser/conventions/*.yaml` files on the
 * left, Monaco editor on the right. Save commits a single
 * WorkspaceFileChange via `Workspaces.applyWorkspaceChanges` (commit
 * message templated from the file path). The endpoint auto-fires
 * projection-sync after a successful commit, so the projection
 * re-projects without a separate trigger.
 *
 * Only convention files with a non-null `sourcePath` are editable —
 * bundled `creuser:standard/*` definitions are read-only and surface in
 * the list as a hint about what's available to `extends:`. Editing a
 * bundled-only entry would mean shadowing it with a workspace-local file
 * of the same id, which the `priority` field handles naturally.
 */
import { computed, onMounted, ref } from 'vue';
import { copyToClipboard, useQuasar } from 'quasar';
import { Projections, Workspaces } from 'src/api';
import type { ConventionLoadError, ConventionSummary, StandardConventionEntry } from 'src/api';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';

const $q = useQuasar();
const { slug: workspaceSlug } = useActiveWorkspace();

const conventions = ref<ConventionSummary[]>([]);
const errors = ref<ConventionLoadError[]>([]);
const loading = ref(false);

const standards = ref<StandardConventionEntry[]>([]);
const standardsLoading = ref(false);

// Editor state. `selectedPath` is the path of the currently-open
// convention (used to highlight the row + drive load). `editingPath` is
// what's bound to the path input — only editable for new conventions.
// `isNew` distinguishes a fresh file (which uses `editingPath` from the
// input) from one loaded from disk (which uses `selectedPath` and is
// frozen). `editingContent` is the in-memory YAML; `baseline` is what
// was last loaded/saved so the dirty bit is meaningful.
const selectedPath = ref<string | null>(null);
const editingPath = ref('');
const editingContent = ref('');
const baseline = ref('');
const isNew = ref(false);
const saving = ref(false);
const deleting = ref(false);

const dirty = computed(() => editingContent.value !== baseline.value);

const editorOptions = {
  automaticLayout: true,
  minimap: { enabled: false },
  fontSize: 12,
  scrollBeyondLastLine: false,
  tabSize: 2,
  wordWrap: 'on' as const,
};

async function loadConventions() {
  if (!workspaceSlug.value) return;
  loading.value = true;
  try {
    const res = await Projections.listConventions({ path: { slug: workspaceSlug.value } });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Failed to load conventions.',
      });
      return;
    }
    const result = res.data?.result;
    conventions.value = result?.conventions ?? [];
    errors.value = result?.errors ?? [];
  } finally {
    loading.value = false;
  }
}

async function selectConvention(conv: ConventionSummary) {
  if (!workspaceSlug.value) return;
  if (!conv.sourcePath) {
    // Bundled-only convention — read-only. Open it as if it were a new
    // workspace file the admin is about to author, pre-populating the
    // path with the conventional location and the body with an
    // `extends:` stub that points back at the bundled id.
    isNew.value = true;
    selectedPath.value = `.creuser/conventions/${conv.id}.yaml`;
    editingPath.value = selectedPath.value;
    editingContent.value = `id: ${conv.id}\nextends: creuser:${conv.id.startsWith('standard/') ? conv.id : 'standard/' + conv.id}\n# Override match.glob, slug, metadata, etc. as needed.\n`;
    baseline.value = '';
    return;
  }

  isNew.value = false;
  selectedPath.value = conv.sourcePath;
  editingPath.value = conv.sourcePath;
  try {
    const res = await Workspaces.getWorkspaceFile({
      path: { slug: workspaceSlug.value },
      query: { path: conv.sourcePath },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Failed to load file.',
      });
      return;
    }
    const content = res.data?.result?.content ?? '';
    editingContent.value = content;
    baseline.value = content;
  } catch (e: unknown) {
    $q.notify({
      type: 'negative',
      position: 'top',
      message: e instanceof Error ? e.message : 'Failed to load file.',
    });
  }
}

async function loadStandards() {
  standardsLoading.value = true;
  try {
    const res = await Projections.listStandardConventions();
    if (res.error) {
      // Don't surface as a notify — the help section will just show
      // "None loaded" and admins can move on. Standards-library failure
      // is unusual (it's a static C# catalog); a console warning is
      // enough for diagnostics.
      console.warn('Failed to load standard conventions library', res.error);
      return;
    }
    standards.value = res.data?.result?.standards ?? [];
  } finally {
    standardsLoading.value = false;
  }
}

// Pull the `description:` line out of the YAML so the summary row reads
// as a one-glance label. Falls back to empty string when absent.
function standardLine(entry: StandardConventionEntry): string {
  const m = entry.yaml.match(/^\s*description:\s*(.+)$/m);
  return m ? m[1]!.trim() : '';
}

async function copyExtends(reference: string) {
  try {
    await copyToClipboard(`extends: ${reference}`);
    $q.notify({
      type: 'info',
      position: 'top',
      message: `Copied: extends: ${reference}`,
      timeout: 1500,
    });
  } catch {
    // Ignore — clipboard rejection (focus issues, permissions) is the
    // browser's problem, not ours.
  }
}

// Annotated YAML covering every field the convention loader recognizes.
// Mirrors `Creuser.Core.Projections.Convention` + its sub-records — keep
// this in sync if those gain or rename fields. The reference is intentionally
// non-runnable (the `glob` is a placeholder) so admins copy and edit rather
// than dropping it in as-is.
const schemaReference = `# Convention schema — all options annotated. Most fields are optional.
# Smallest valid file: just \`id\` and \`match.glob\`.

id: my_convention            # required. Becomes \`kind\` on each cr.entities row.
                             # Must be unique within the workspace.
description: One-line summary shown in the convention list.   # optional.
extends: creuser:standard/markdown-doc  # optional. Inherit from a bundled
                             # entry (see "Bundled standard library" below).
                             # Local fields override inherited ones.
priority: 0                  # optional. Higher wins when two conventions
                             # match the same file. Default 0.

match:
  glob: "docs/**/*.md"       # required. The selector. POSIX-style globbing
                             # via DotNet.Glob; \`**\` crosses directories.
  exclude:                   # optional. Subtractive globs, ANDed with glob.
    - "**/node_modules/**"
    - "**/_drafts/**"
  frontmatter_must_have:     # optional. Files lacking these YAML
    - status                 # frontmatter keys are skipped. Useful when a
                             # glob is too broad on its own.

slug:                        # required. How to derive cr.entities.slug.
  from: filename             # \`filename\` | \`path\` | \`frontmatter.<key>\` | \`template\`
  transform: kebab           # \`kebab\` | \`snake\` | \`lower\` | \`as-is\` (default).
  # template: "{parent_dir}-{filename}"  # only when from: template.
                             # Variables: filename, parent_dir, path, extension.

metadata:                    # optional. Default: source: none.
  source: frontmatter        # \`frontmatter\` | \`none\`. (header / filename
                             # patterns reserved for v0.2.)
  computed:                  # optional. Synthetic fields stitched in from
    line_count: file.line_count          # the file or git context. Dotted
    last_commit: git.last_commit_sha     # accessors; see docs/projections.
  required:                  # optional. Fail validation if any of these
    - title                  # frontmatter keys are missing. Surfaces in
    - status                 # \`find_invalid\` / projection report errors.

relationships:               # optional. Typed edges into cr.entity_refs.
  - kind: parent             # The relationship column on the row. Free-form.
    select_path: "{file_dir}/index.md"   # Resolve target by interpolated
                                         # path. Mutually exclusive with
                                         # select_frontmatter.
    target_kind: business_rule           # Required entity \`kind\` on the
                                         # other end.
  - kind: references
    select_frontmatter: references       # Read this frontmatter key (list
                                         # or scalar) and resolve each value
                                         # to (target_kind, slug).
    target_kind: business_rule

validation:                  # optional. Declarative rules; failures bubble
  - rule: has_title          # up via \`find_invalid\` and the projection
    expr: metadata.title != null         # report. Expressions evaluate
                                         # against metadata + relationships.
`;

async function copySchemaReference() {
  try {
    await copyToClipboard(schemaReference);
    $q.notify({
      type: 'info',
      position: 'top',
      message: 'Schema reference copied to clipboard.',
      timeout: 1500,
    });
  } catch {
    // Ignore — clipboard rejection is the browser's problem, not ours.
  }
}

function newFromStandard(entry: StandardConventionEntry) {
  // Pull the bundled `id:` so the new file's path matches the canonical
  // location (e.g., `creuser:standard/adr` → `.creuser/conventions/adr.yaml`).
  // Falls back to a placeholder when the id can't be parsed.
  const idMatch = entry.yaml.match(/^\s*id:\s*([\w-]+)/m);
  const baseId = idMatch ? idMatch[1] : (entry.reference.split('/').pop() ?? 'my-convention');
  const path = `.creuser/conventions/${baseId}.yaml`;

  isNew.value = true;
  selectedPath.value = path;
  editingPath.value = path;
  editingContent.value = `id: ${baseId}\nextends: ${entry.reference}\n# Override match.glob, slug, metadata, etc. as needed.\n# The full bundled YAML for ${entry.reference} is shown in the help panel.\n`;
  baseline.value = '';
}

function onNewConvention() {
  isNew.value = true;
  selectedPath.value = '.creuser/conventions/';
  editingPath.value = '.creuser/conventions/';
  editingContent.value = `id: my-convention\nmatch:\n  glob: "docs/**/*.md"\nslug:\n  from: filename\nmetadata:\n  source: frontmatter\n`;
  baseline.value = '';
}

function discardChanges() {
  editingContent.value = baseline.value;
}

async function onSave() {
  if (!workspaceSlug.value) return;
  const path = editingPath.value.trim();
  if (!path) {
    $q.notify({ type: 'negative', position: 'top', message: 'Path is required.' });
    return;
  }

  saving.value = true;
  try {
    const res = await Workspaces.applyWorkspaceChanges({
      path: { slug: workspaceSlug.value },
      body: {
        changes: [{ path, action: 'write', content: editingContent.value }],
      },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Save failed.',
      });
      return;
    }

    const result = res.data?.result;
    if (!result?.ok) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: result?.error ?? 'Save failed.',
        timeout: 8000,
      });
      return;
    }

    $q.notify({
      type: 'positive',
      position: 'top',
      message: result.message ?? 'Saved.',
    });

    // After a successful save, baseline = saved content (so dirty=false)
    // and we transition out of "new" mode to "editing existing."
    baseline.value = editingContent.value;
    isNew.value = false;
    selectedPath.value = path;
    editingPath.value = path;

    // Reload the conventions list so the new file appears + its load
    // status is reflected. Projection-sync runs server-side
    // fire-and-forget; the list endpoint re-parses every call so we
    // see fresh state.
    await loadConventions();
  } finally {
    saving.value = false;
  }
}

function onDelete() {
  if (!workspaceSlug.value || isNew.value) return;
  const path = editingPath.value.trim();
  if (!path) return;
  $q.dialog({
    title: 'Delete convention?',
    message:
      `<p>Remove <code>${path}</code> from the working tree.</p>` +
      `<p>The file gets unlinked and projection-sync re-fires; entities this convention was projecting will disappear from the projection on the next sync.</p>` +
      `<p>For git workspaces, the deletion is uncommitted until you click Commit in the header.</p>`,
    html: true,
    ok: { label: 'Delete', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async () => {
    if (!workspaceSlug.value) return;
    deleting.value = true;
    try {
      const res = await Workspaces.applyWorkspaceChanges({
        path: { slug: workspaceSlug.value },
        body: { changes: [{ path, action: 'delete' }] },
      });
      if (res.error) {
        $q.notify({
          type: 'negative',
          position: 'top',
          message: problemMessage(res.error) ?? 'Delete failed.',
        });
        return;
      }
      const result = res.data?.result;
      if (!result?.ok) {
        $q.notify({
          type: 'negative',
          position: 'top',
          message: result?.error ?? 'Delete failed.',
          timeout: 8000,
        });
        return;
      }

      $q.notify({
        type: 'positive',
        position: 'top',
        message: `Deleted ${path}.`,
      });

      // Clear the editor — the file is gone.
      selectedPath.value = null;
      editingPath.value = '';
      editingContent.value = '';
      baseline.value = '';
      isNew.value = false;
      await loadConventions();
    } finally {
      deleting.value = false;
    }
  });
}

function problemMessage(err: unknown): string | undefined {
  if (err && typeof err === 'object') {
    const e = err as { detail?: unknown; title?: unknown };
    if (typeof e.detail === 'string' && e.detail.length) return e.detail;
    if (typeof e.title === 'string' && e.title.length) return e.title;
  }
  return undefined;
}

onMounted(() => {
  void loadConventions();
  void loadStandards();
});
</script>

<style lang="scss" scoped>
.cr-conv-page {
  display: flex;
  flex-direction: column;
  padding: 24px 32px 32px;
  gap: 16px;
  min-height: calc(100vh - var(--cr-header-height));
}

.cr-conv-header {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}

.cr-conv-subhead {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--cr-fg-secondary);
  max-width: 880px;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}

.cr-conv-errors {
  background: var(--cr-bg-surface);
  border: 1px solid var(--q-negative);
  border-left-width: 4px;
  border-radius: 4px;
  padding: 10px 14px;
}

.cr-conv-errors-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--q-negative);
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 6px;
}

.cr-conv-errors-list {
  margin: 0;
  padding-left: 22px;
  font-size: 12px;
  color: var(--cr-fg-secondary);

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    color: var(--cr-fg-primary);
  }
}

.cr-conv-errors-no-source {
  color: var(--cr-fg-tertiary);
  font-style: italic;
}

.cr-conv-body {
  display: flex;
  flex: 1;
  min-height: 0;
  gap: 16px;
}

.cr-conv-list {
  width: 280px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-surface);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 4px;
  overflow: hidden;
}

.cr-conv-list-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-elevated);
  border-bottom: 1px solid var(--cr-border-subtle);
}

.cr-conv-list-new {
  font-size: 11px;
}

.cr-conv-list-empty {
  padding: 16px;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
  text-align: center;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}

.cr-conv-list-items {
  list-style: none;
  margin: 0;
  padding: 4px 0;
  flex: 1;
  overflow-y: auto;
}

.cr-conv-item {
  padding: 8px 12px;
  cursor: pointer;
  border-left: 2px solid transparent;
  transition:
    background 80ms ease-out,
    border-color 80ms ease-out;

  &:hover {
    background: var(--cr-bg-hover);
  }

  &--active {
    background: var(--cr-brand-tint-soft);
    border-left-color: var(--q-primary);
  }
}

.cr-conv-item-id {
  font-size: 12px;
  font-weight: 600;
  color: var(--cr-fg-primary);
  margin-bottom: 2px;
}

.cr-conv-item-glob {
  font-size: 11px;
  color: var(--cr-fg-secondary);
  margin-bottom: 2px;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 10px;
  }
}

.cr-conv-item-source {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  color: var(--cr-fg-tertiary);
}

.cr-conv-item-bundled {
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-elevated);
  padding: 1px 5px;
  border-radius: 2px;
  width: max-content;
}

.cr-conv-help {
  border-top: 1px solid var(--cr-border-subtle);
  padding: 8px 12px;
  font-size: 11px;
  color: var(--cr-fg-secondary);
  max-height: 380px;
  overflow-y: auto;

  > summary {
    cursor: pointer;
    font-weight: 500;
    color: var(--cr-fg-primary);
  }

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 10px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}

.cr-conv-help-intro {
  margin: 6px 0 8px;
  font-size: 11px;
  color: var(--cr-fg-secondary);
}

.cr-conv-help-empty {
  padding: 8px 0;
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  text-align: center;
}

.cr-conv-standards {
  list-style: none;
  margin: 0;
  padding: 0;
}

.cr-conv-std-item {
  margin: 4px 0;
  border: 1px solid var(--cr-border-subtle);
  border-radius: 3px;
  background: var(--cr-bg-page);

  details > summary {
    list-style: none;
  }
  details > summary::-webkit-details-marker {
    display: none;
  }
}

.cr-conv-std-summary {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  cursor: pointer;
  font-weight: 500;
  color: var(--cr-fg-primary);

  &:hover {
    background: var(--cr-bg-hover);
  }
}

.cr-conv-std-ref {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  background: var(--cr-bg-elevated);
  padding: 1px 4px;
  border-radius: 3px;
  white-space: nowrap;
}

.cr-conv-std-desc {
  flex: 1;
  font-size: 10px;
  font-weight: 400;
  color: var(--cr-fg-tertiary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cr-conv-std-yaml {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  background: var(--cr-bg-elevated);
  padding: 6px 8px;
  margin: 0 8px 6px;
  border-radius: 3px;
  overflow-x: auto;
  white-space: pre;
}

.cr-conv-schema-yaml {
  max-height: 360px;
  overflow-y: auto;
}

.cr-conv-std-actions {
  display: flex;
  gap: 4px;
  padding: 0 8px 6px;
}

.cr-conv-editor {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-surface);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 4px;
  overflow: hidden;
}

.cr-conv-editor-empty {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--cr-fg-tertiary);
  font-size: 13px;
}

.cr-conv-editor-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--cr-border-subtle);
}

.cr-conv-editor-path {
  flex: 1;
  max-width: 480px;
}

.cr-conv-monaco {
  flex: 1;
  min-height: 300px;
}

.cr-conv-monaco-loading {
  padding: 16px;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
}

.cr-conv-monaco-error {
  color: var(--q-negative);
}

.cr-conv-editor-dirty {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  font-size: 11px;
  color: var(--cr-fg-secondary);
  background: var(--cr-brand-tint-soft);
  border-top: 1px solid var(--cr-border-subtle);
}
</style>
