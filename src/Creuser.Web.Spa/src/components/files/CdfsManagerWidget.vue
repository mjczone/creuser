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
        round
        size="sm"
        icon="refresh"
        :loading="loading"
        aria-label="Refresh"
        @click="reload"
      >
        <q-tooltip>Refresh</q-tooltip>
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
import { Projections } from 'src/api';
import type { CdfsConventionRow, EntityDetail, EntitySummary } from 'src/api';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';

const props = defineProps<{
  workspaceSlug?: string;
}>();

const $q = useQuasar();
const { slug: activeWorkspaceSlug } = useActiveWorkspace();

const slug = computed(() => props.workspaceSlug ?? activeWorkspaceSlug.value ?? '');

const loading = ref(false);
const rows = ref<CdfsConventionRow[]>([]);
const activeConvention = ref<CdfsConventionRow | null>(null);
const entities = ref<EntitySummary[]>([]);
const selectedEntityId = ref<string | null>(null);
const entityLoading = ref(false);
const entityDetail = ref<EntityDetail | null>(null);

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
  void loadEntities(conv);
}

function enterRoot() {
  activeConvention.value = null;
  entities.value = [];
  selectedEntityId.value = null;
  entityDetail.value = null;
  if (rows.value.length === 0) void loadConventions();
}

async function openEntity(ent: EntitySummary) {
  if (!slug.value) return;
  selectedEntityId.value = ent.id;
  entityDetail.value = null;
  entityLoading.value = true;
  try {
    const res = await Projections.getEntity({
      path: { slug: slug.value, kind: ent.kind, entitySlug: ent.slug },
    });
    if (res.error) {
      notifyError(res.error, 'Failed to load entity.');
      return;
    }
    entityDetail.value = res.data?.result ?? null;
  } finally {
    entityLoading.value = false;
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
