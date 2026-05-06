<template>
  <div class="cr-cdfs">
    <header class="cr-cdfs-header">
      <q-breadcrumbs class="cr-cdfs-crumbs" separator="/">
        <q-breadcrumbs-el
          label="conventions"
          icon="schema"
          class="cr-cdfs-crumb"
          :class="{ 'cr-cdfs-crumb--last': activeConvention === null }"
          @click="enterRoot"
        />
        <q-breadcrumbs-el
          v-if="activeConvention"
          :label="activeConvention.id"
          class="cr-cdfs-crumb cr-cdfs-crumb--last"
        />
      </q-breadcrumbs>
      <q-space />
      <q-btn
        flat
        dense
        no-caps
        size="sm"
        icon="autorenew"
        label="Re-scan"
        :loading="syncing"
        :disable="loading"
        aria-label="Re-scan workspace and reload"
        @click="onResync"
      >
        <q-tooltip>
          Re-scan the working tree (runs projection-sync), then reload. Use this when files
          changed on disk — direct writes don't auto-sync.
        </q-tooltip>
      </q-btn>
      <q-btn
        flat
        dense
        round
        size="sm"
        icon="refresh"
        :loading="loading"
        :disable="syncing"
        aria-label="Reload from current projection"
        @click="reload"
      >
        <q-tooltip>Reload from the current projection (no re-scan).</q-tooltip>
      </q-btn>
    </header>

    <div class="cr-cdfs-body">
      <aside class="cr-cdfs-list">
        <div v-if="loading && rows.length === 0 && entities.length === 0" class="cr-cdfs-empty">
          Loading…
        </div>

        <ul v-else-if="activeConvention === null && rows.length > 0" class="cr-cdfs-rows">
          <li
            v-for="row in rows"
            :key="row.id"
            class="cr-cdfs-row cr-cdfs-row--conv"
            @click="enterConvention(row)"
          >
            <q-icon name="folder_special" size="18px" class="cr-cdfs-row-icon" />
            <span class="cr-cdfs-row-name">{{ row.id }}</span>
            <span class="cr-cdfs-row-count">{{ row.entityCount }}</span>
            <q-icon name="chevron_right" size="14px" class="cr-cdfs-row-chev" />
            <q-tooltip v-if="row.description" anchor="top middle" self="bottom middle">
              {{ row.description }}
            </q-tooltip>
          </li>
        </ul>

        <div v-else-if="activeConvention === null" class="cr-cdfs-empty">
          No conventions match. Define one under
          <code>.creuser/conventions/</code> and re-sync the projection.
        </div>

        <ul v-else-if="entities.length > 0" class="cr-cdfs-rows">
          <li
            v-for="ent in entities"
            :key="ent.id"
            class="cr-cdfs-row cr-cdfs-row--entity"
            :class="{ 'cr-cdfs-row--active': selectedEntityId === ent.id }"
            @click="openEntity(ent)"
          >
            <q-icon name="article" size="18px" class="cr-cdfs-row-icon" />
            <span class="cr-cdfs-row-name">{{ ent.slug }}</span>
            <span class="cr-cdfs-row-path" :title="ent.path">{{ ent.path }}</span>
            <q-menu
              v-if="visibleActionsFor(ent).length > 0"
              class="cr-cdfs-action-menu"
              touch-position
              context-menu
              auto-close
            >
              <q-list dense style="min-width: 200px">
                <q-item
                  v-for="action in visibleActionsFor(ent)"
                  :key="action.id"
                  clickable
                  @click="onActionClick(ent, action)"
                >
                  <q-item-section avatar v-if="action.icon">
                    <q-icon :name="action.icon" size="16px" />
                  </q-item-section>
                  <q-item-section>
                    {{ action.label }}
                  </q-item-section>
                  <q-item-section side>
                    <code class="cr-cdfs-action-kind">{{ action.runs.kind }}</code>
                  </q-item-section>
                </q-item>
              </q-list>
            </q-menu>
          </li>
        </ul>

        <div v-else class="cr-cdfs-empty">
          No entities matched this convention. The glob may not match anything in
          the working tree, or the projection may need a re-sync.
        </div>
      </aside>

      <main class="cr-cdfs-pane">
        <div v-if="!selectedEntityId" class="cr-cdfs-pane-empty">
          <q-icon name="grid_view" size="40px" />
          <p v-if="activeConvention === null">
            Pick a convention to see what entities the projection has matched for it.
          </p>
          <p v-else>Pick an entity to inspect its metadata and references.</p>
        </div>

        <div v-else-if="entityLoading" class="cr-cdfs-pane-empty">Loading entity…</div>

        <template v-else-if="entityDetail">
          <header class="cr-cdfs-pane-header">
            <q-icon name="article" size="18px" />
            <code class="cr-cdfs-pane-slug">{{ entityDetail.slug }}</code>
            <q-space />
            <code class="cr-cdfs-pane-kind">{{ entityDetail.kind }}</code>
          </header>

          <div class="cr-cdfs-pane-body">
            <section class="cr-cdfs-section">
              <h3 class="cr-cdfs-section-title">Path</h3>
              <code class="cr-cdfs-pane-path">{{ entityDetail.path }}</code>
            </section>

            <section class="cr-cdfs-section">
              <h3 class="cr-cdfs-section-title">Metadata</h3>
              <pre class="cr-cdfs-meta">{{ formattedMetadata }}</pre>
            </section>

            <section v-if="entityDetail.refsOut.length > 0" class="cr-cdfs-section">
              <h3 class="cr-cdfs-section-title">
                Outbound references ({{ entityDetail.refsOut.length }})
              </h3>
              <ul class="cr-cdfs-refs">
                <li v-for="r in entityDetail.refsOut" :key="r.id">
                  <code>{{ r.relationship }}</code>
                  <span v-if="r.targetKind">
                    → <code>{{ r.targetKind }}</code>
                  </span>
                  <span v-if="r.targetSlug">
                    /<code>{{ r.targetSlug }}</code>
                  </span>
                  <span v-if="!r.toEntityId" class="cr-cdfs-refs-unresolved">unresolved</span>
                </li>
              </ul>
            </section>

            <section v-if="entityDetail.refsIn.length > 0" class="cr-cdfs-section">
              <h3 class="cr-cdfs-section-title">
                Inbound references ({{ entityDetail.refsIn.length }})
              </h3>
              <ul class="cr-cdfs-refs">
                <li v-for="r in entityDetail.refsIn" :key="r.id">
                  <code>{{ r.relationship }}</code>
                  <span v-if="r.targetKind">
                    ← <code>{{ r.targetKind }}</code>
                  </span>
                  <span v-if="r.targetSlug">
                    /<code>{{ r.targetSlug }}</code>
                  </span>
                </li>
              </ul>
            </section>
          </div>
        </template>
      </main>
    </div>
  </div>
</template>

<script setup lang="ts">
/**
 * CDFS (Convention-Driven File System) widget — Stage 2 of the file-manager
 * design. Same shell shape as `FileManagerWidget.vue`, but the data adapter
 * is the projection layer instead of the raw working tree:
 *
 *   root          → list of conventions (with entity counts)
 *   convention/   → list of entities matched by that convention
 *   entity        → metadata + refs in the right pane
 *
 * Convention-declared right-click actions land in Stage 3 (waits on a
 * `Convention.Actions` schema addition); the `actions: []` array on every
 * row is intentional forward-compatibility.
 */
import { computed, onMounted, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { useRoute } from 'vue-router';
import { Projections, Workspaces } from 'src/api';
import type {
  CdfsActionDescriptor,
  CdfsConventionRow,
  EntityDetail,
  EntitySummary,
} from 'src/api';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';
import { useAssistantStore } from 'src/stores/assistant';

const props = defineProps<{
  workspaceSlug?: string;
}>();

const $q = useQuasar();
const route = useRoute();
const assistant = useAssistantStore();
const { slug: activeWorkspaceSlug } = useActiveWorkspace();

const slug = computed(() => props.workspaceSlug ?? activeWorkspaceSlug.value ?? '');

const loading = ref(false);
const syncing = ref(false);
const rows = ref<CdfsConventionRow[]>([]);
const activeConvention = ref<CdfsConventionRow | null>(null);
const entities = ref<EntitySummary[]>([]);
const selectedEntityId = ref<string | null>(null);
const entityLoading = ref(false);
const entityDetail = ref<EntityDetail | null>(null);
// Detail cache scoped to the active convention. Cleared on convention
// change + on root return so a stale row's metadata can't gate an action.
const entityDetailCache = ref(new Map<string, EntityDetail>());

const formattedMetadata = computed(() => {
  if (!entityDetail.value) return '';
  const raw = entityDetail.value.metadataJson;
  if (!raw) return '(no metadata)';
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
});

async function reload() {
  if (activeConvention.value) {
    await loadEntities(activeConvention.value);
  } else {
    await loadConventions();
  }
}

async function onResync() {
  if (!slug.value) return;
  syncing.value = true;
  try {
    const res = await Projections.syncProjection({ path: { slug: slug.value } });
    if (res.error) {
      notifyError(res.error, 'Projection sync failed.');
      return;
    }
    const report = res.data?.result?.report;
    const total = report?.entityTotal ?? 0;
    $q.notify({
      type: 'positive',
      position: 'top',
      timeout: 2500,
      message: `Re-scanned: ${total} entit${total === 1 ? 'y' : 'ies'}.`,
    });
    // After sync, conventions could be different (added/removed/renamed) so
    // always refetch the root list. If the user was inside a convention,
    // reload entities under it; if that convention no longer exists, fall
    // back to root.
    await loadConventions();
    if (activeConvention.value) {
      const stillThere = rows.value.find((r) => r.id === activeConvention.value!.id);
      if (stillThere) {
        activeConvention.value = stillThere;
        entityDetailCache.value.clear();
        await loadEntities(stillThere);
      } else {
        enterRoot();
      }
    }
  } finally {
    syncing.value = false;
  }
}

async function loadConventions() {
  if (!slug.value) return;
  loading.value = true;
  try {
    const res = await Projections.listCdfsConventions({ path: { slug: slug.value } });
    if (res.error) {
      notifyError(res.error, 'Failed to load CDFS conventions.');
      return;
    }
    rows.value = res.data?.result?.conventions ?? [];
  } finally {
    loading.value = false;
  }
}

async function loadEntities(conv: CdfsConventionRow) {
  if (!slug.value) return;
  loading.value = true;
  try {
    const res = await Projections.queryEntities({
      path: { slug: slug.value },
      query: { kind: conv.id, limit: 500 },
    });
    if (res.error) {
      notifyError(res.error, 'Failed to load entities.');
      return;
    }
    entities.value = res.data?.result ?? [];
  } finally {
    loading.value = false;
  }
}

function enterConvention(conv: CdfsConventionRow) {
  activeConvention.value = conv;
  entities.value = [];
  selectedEntityId.value = null;
  entityDetail.value = null;
  entityDetailCache.value.clear();
  void loadEntities(conv);
}

function enterRoot() {
  activeConvention.value = null;
  entities.value = [];
  selectedEntityId.value = null;
  entityDetail.value = null;
  entityDetailCache.value.clear();
  if (rows.value.length === 0) void loadConventions();
}

async function openEntity(ent: EntitySummary) {
  if (!slug.value) return;
  selectedEntityId.value = ent.id;
  entityDetail.value = null;
  entityLoading.value = true;
  try {
    const detail = await fetchEntityDetail(ent);
    entityDetail.value = detail;
  } finally {
    entityLoading.value = false;
  }
}

async function fetchEntityDetail(ent: EntitySummary): Promise<EntityDetail | null> {
  if (!slug.value) return null;
  // Cache one detail per entity id during the session — clicking actions
  // shortly after opening shouldn't re-fetch. Cleared whenever the active
  // convention changes.
  const cached = entityDetailCache.value.get(ent.id);
  if (cached) return cached;
  const res = await Projections.getEntity({
    path: { slug: slug.value, kind: ent.kind, entitySlug: ent.slug },
  });
  if (res.error) {
    notifyError(res.error, 'Failed to load entity.');
    return null;
  }
  const detail = res.data?.result ?? null;
  if (detail) entityDetailCache.value.set(ent.id, detail);
  return detail;
}

function visibleActionsFor(ent: EntitySummary): CdfsActionDescriptor[] {
  if (!activeConvention.value) return [];
  const actions = activeConvention.value.actions ?? [];
  if (actions.length === 0) return [];
  // EntitySummary now carries metadataJson, so the per-row `when:` filter
  // runs at list-render time without a detail fetch. The detail cache
  // still backs the inspector pane (refs + pretty-printed metadata).
  return actions.filter((a) => evaluateWhenAgainstMetadata(a.when, ent.metadataJson));
}

async function onActionClick(ent: EntitySummary, action: CdfsActionDescriptor) {
  if (!slug.value) return;

  // Make sure we have detail before evaluating `when:` and interpolating
  // `{path}` / `{slug}` / `{metadata.key}` placeholders.
  const detail = await fetchEntityDetail(ent);
  if (!detail) return;

  if (!evaluateWhen(action.when, detail)) {
    $q.notify({
      type: 'info',
      position: 'top',
      message: `Action "${action.label}" doesn't match this entity (when: ${action.when}).`,
    });
    return;
  }

  if (action.confirm === 'required') {
    $q.dialog({
      title: action.label,
      message:
        `<p>Run <strong>${action.label}</strong> on <code>${detail.kind}/${detail.slug}</code>?</p>` +
        `<p class="text-caption">${describeRuns(action)}</p>`,
      html: true,
      ok: { label: 'Run', color: 'primary', unelevated: true, noCaps: true },
      cancel: { flat: true, noCaps: true },
      // eslint-disable-next-line @typescript-eslint/no-misused-promises
    }).onOk(async () => {
      await dispatchAction(action, detail);
    });
  } else {
    await dispatchAction(action, detail);
  }
}

async function dispatchAction(action: CdfsActionDescriptor, detail: EntityDetail) {
  switch (action.runs.kind) {
    case 'agent-prompt':
      await dispatchAgentPrompt(action, detail);
      return;
    case 'file-mutate':
    case 'query':
    case 'job':
    default:
      $q.notify({
        type: 'info',
        position: 'top',
        timeout: 5000,
        message: `Action kind "${action.runs.kind}" isn't dispatched yet — schema is recognized, dispatch arrives in a follow-on commit.`,
      });
      return;
  }
}

async function dispatchAgentPrompt(action: CdfsActionDescriptor, detail: EntityDetail) {
  const rawPrompt = action.runs.prompt ?? '';
  if (!rawPrompt.trim()) {
    $q.notify({
      type: 'warning',
      position: 'top',
      message: `Action "${action.label}" has no prompt configured.`,
    });
    return;
  }
  const interpolated = interpolatePrompt(rawPrompt, detail);

  // Inline the file body so the assistant doesn't have to round-trip a
  // fetch tool to read its own context. Capped at 4000 chars — enough
  // for a typical markdown note or doc; longer entities get truncated
  // with a marker so the assistant knows it's seeing a partial.
  let bodySnippet = '';
  if (slug.value) {
    const fileRes = await Workspaces.getWorkspaceFile({
      path: { slug: slug.value },
      query: { path: detail.path },
    });
    const content = fileRes.data?.result?.content;
    if (typeof content === 'string') {
      bodySnippet = content.length > BODY_INLINE_CAP
        ? content.slice(0, BODY_INLINE_CAP) +
          `\n\n…(truncated; ${content.length - BODY_INLINE_CAP} chars omitted)`
        : content;
    }
  }

  const contextBlock =
    `\n\n---\n` +
    `Entity context:\n` +
    `- kind: ${detail.kind}\n` +
    `- slug: ${detail.slug}\n` +
    `- path: ${detail.path}` +
    (bodySnippet ? `\n\nFile body (\`${detail.path}\`):\n\`\`\`\n${bodySnippet}\n\`\`\`` : '');
  assistant.open();
  await assistant.send(interpolated + contextBlock, route.fullPath);
}

const BODY_INLINE_CAP = 4000;

// Literal-equality `when:` evaluator. Recognizes one form:
//   <key> == "value"   (or equivalently "value" == <key>)
// where <key> is a metadata key like `status` or `metadata.status`.
// Anything richer (&&, ||, !=, regex) returns true so the action stays
// visible and a future evaluator upgrade gates it properly.
function evaluateWhen(expr: string | null | undefined, detail: EntityDetail): boolean {
  return evaluateWhenAgainstMetadata(expr, detail.metadataJson);
}

function evaluateWhenAgainstMetadata(
  expr: string | null | undefined,
  metadataJson: string | null | undefined,
): boolean {
  if (!expr || !expr.trim()) return true;
  const m = expr.match(/^\s*([\w.]+)\s*==\s*"([^"]*)"\s*$/);
  if (!m) {
    // Try the inverse order: "value" == key
    const m2 = expr.match(/^\s*"([^"]*)"\s*==\s*([\w.]+)\s*$/);
    if (!m2) return true;
    const [, value, rawKey] = m2;
    return readMetadataValue(metadataJson, rawKey ?? '') === value;
  }
  const [, rawKey, value] = m;
  return readMetadataValue(metadataJson, rawKey ?? '') === value;
}

function readMetadata(detail: EntityDetail, rawKey: string): string | undefined {
  return readMetadataValue(detail.metadataJson, rawKey);
}

function readMetadataValue(
  metadataJson: string | null | undefined,
  rawKey: string,
): string | undefined {
  if (!rawKey) return undefined;
  const key = rawKey.startsWith('metadata.') ? rawKey.slice('metadata.'.length) : rawKey;
  let metadata: Record<string, unknown>;
  try {
    metadata = JSON.parse(metadataJson ?? '{}') as Record<string, unknown>;
  } catch {
    return undefined;
  }
  const parts = key.split('.');
  let cur: unknown = metadata;
  for (const p of parts) {
    if (cur && typeof cur === 'object') {
      cur = (cur as Record<string, unknown>)[p];
    } else {
      return undefined;
    }
  }
  if (typeof cur === 'string') return cur;
  if (cur == null) return undefined;
  if (typeof cur === 'number' || typeof cur === 'boolean') return String(cur);
  // Arrays / nested objects can't equality-match a literal string in v0.1.x
  // and would stringify as "[object Object]" — return undefined so the
  // when-clause cleanly fails to match instead.
  return undefined;
}

// `{path}`, `{slug}`, `{kind}`, `{metadata.key}` interpolation. Unknown
// placeholders are left in place so a typo is visible in the chat.
function interpolatePrompt(template: string, detail: EntityDetail): string {
  return template.replace(/\{([\w.]+)\}/g, (_full, raw: string) => {
    if (raw === 'path') return detail.path;
    if (raw === 'slug') return detail.slug;
    if (raw === 'kind') return detail.kind;
    if (raw === 'convention_id') return detail.conventionId;
    if (raw.startsWith('metadata.')) {
      return readMetadata(detail, raw) ?? `{${raw}}`;
    }
    return readMetadata(detail, raw) ?? `{${raw}}`;
  });
}

function describeRuns(action: CdfsActionDescriptor): string {
  const r = action.runs;
  switch (r.kind) {
    case 'agent-prompt':
      return `Sends a prompt to the chat assistant with this entity as context.`;
    case 'file-mutate':
      return `Runs the script "${r.script ?? '(unset)'}" against this entity's file.`;
    case 'query':
      return `Invokes the projection tool "${r.tool ?? '(unset)'}".`;
    case 'job':
      return `Runs job "${r.jobId ?? '(unset)'}".`;
    default:
      return `Dispatches a "${r.kind}" action.`;
  }
}

function notifyError(err: unknown, fallback: string) {
  $q.notify({
    type: 'negative',
    position: 'top',
    message: problemMessage(err) ?? fallback,
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
  if (slug.value && rows.value.length === 0 && !loading.value) void loadConventions();
});

watch(
  () => slug.value,
  (next) => {
    if (next) {
      activeConvention.value = null;
      entities.value = [];
      selectedEntityId.value = null;
      entityDetail.value = null;
      void loadConventions();
    }
  },
);
</script>

<style lang="scss" scoped>
.cr-cdfs {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 360px;
}

.cr-cdfs-header {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--cr-border-subtle);
  background: var(--cr-bg-elevated);
}

.cr-cdfs-crumbs {
  font-size: 12px;
  color: var(--cr-fg-secondary);
}

.cr-cdfs-crumb {
  cursor: pointer;
  color: var(--cr-fg-secondary);

  &:hover {
    color: var(--cr-fg-primary);
  }
}

.cr-cdfs-crumb--last {
  color: var(--cr-fg-primary);
  font-weight: 500;
}

.cr-cdfs-body {
  display: flex;
  flex: 1;
  min-height: 0;
}

.cr-cdfs-list {
  width: 320px;
  flex-shrink: 0;
  overflow-y: auto;
  border-right: 1px solid var(--cr-border-subtle);
  background: var(--cr-bg-surface);
}

.cr-cdfs-empty {
  padding: 24px 16px;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
  text-align: center;
}

.cr-cdfs-rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.cr-cdfs-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  cursor: pointer;
  font-size: 12px;
  border-bottom: 1px solid var(--cr-border-faint);

  &:hover {
    background: var(--cr-bg-hover);
  }
}

.cr-cdfs-row--active {
  background: var(--cr-brand-tint-soft);
}

.cr-cdfs-row-icon {
  flex-shrink: 0;
  color: var(--cr-fg-secondary);
}

.cr-cdfs-row-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--cr-fg-primary);
}

.cr-cdfs-row-count {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-elevated);
  padding: 1px 6px;
  border-radius: 8px;
  min-width: 20px;
  text-align: center;
}

.cr-cdfs-row-path {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  color: var(--cr-fg-tertiary);
  max-width: 60%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cr-cdfs-row-chev {
  color: var(--cr-fg-tertiary);
}

.cr-cdfs-pane {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-base);
}

.cr-cdfs-pane-empty {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--cr-fg-tertiary);
  font-size: 12px;
  padding: 24px;
  text-align: center;

  p {
    margin: 0;
    max-width: 340px;
  }
}

.cr-cdfs-pane-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 16px;
  border-bottom: 1px solid var(--cr-border-subtle);
  background: var(--cr-bg-elevated);
}

.cr-cdfs-pane-slug {
  font-family: var(--cr-font-family-mono);
  font-size: 12px;
  color: var(--cr-fg-primary);
}

.cr-cdfs-pane-kind {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-surface);
  padding: 2px 6px;
  border-radius: 3px;
}

.cr-cdfs-pane-body {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.cr-cdfs-section-title {
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  margin: 0 0 6px;
}

.cr-cdfs-section {
  font-size: 12px;
}

.cr-cdfs-pane-path {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  color: var(--cr-fg-secondary);
  word-break: break-all;
}

.cr-cdfs-meta {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  color: var(--cr-fg-primary);
  background: var(--cr-bg-elevated);
  padding: 8px 10px;
  border-radius: 3px;
  margin: 0;
  overflow-x: auto;
  white-space: pre;
  max-height: 360px;
  overflow-y: auto;
}

.cr-cdfs-refs {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 11px;

  li {
    display: flex;
    align-items: center;
    gap: 4px;
    flex-wrap: wrap;
  }
}

.cr-cdfs-action-kind {
  font-family: var(--cr-font-family-mono);
  font-size: 9px;
  letter-spacing: 0.04em;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-elevated);
  padding: 1px 4px;
  border-radius: 3px;
}


.cr-cdfs-refs-unresolved {
  font-size: 10px;
  font-weight: 500;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--cr-status-warning);
  background: var(--cr-bg-elevated);
  padding: 1px 5px;
  border-radius: 3px;
  margin-left: 4px;
}
</style>
