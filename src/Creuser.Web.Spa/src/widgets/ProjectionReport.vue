<template>
  <div class="cr-w-pr">
    <div class="cr-w-pr-toolbar">
      <span class="cr-w-pr-title">Projection state</span>
      <q-space />
      <q-btn flat dense round icon="refresh" size="sm" :loading="loading" @click="refresh">
        <q-tooltip>Reload</q-tooltip>
      </q-btn>
    </div>

    <div v-if="loading && !loaded" class="cr-w-pr-state">
      <q-spinner size="24px" color="primary" />
      <span>Loading projection state…</span>
    </div>
    <div v-else-if="error" class="cr-w-pr-state cr-w-pr-error">
      <q-icon name="error_outline" size="24px" />
      <span>{{ error }}</span>
    </div>
    <div v-else class="cr-w-pr-body">
      <section class="cr-w-pr-section">
        <h3 class="cr-w-pr-section-title">Conventions ({{ conventions.length }})</h3>
        <div v-if="conventions.length === 0" class="cr-w-pr-empty">
          No conventions in <code>.creuser/conventions/</code>.
        </div>
        <ul v-else class="cr-w-pr-conv-list">
          <li v-for="c in conventions" :key="c.id" class="cr-w-pr-conv-row">
            <span class="cr-w-pr-conv-id">{{ c.id }}</span>
            <span v-if="c.glob" class="cr-w-pr-conv-glob">{{ c.glob }}</span>
            <span class="cr-w-pr-conv-prio">priority {{ c.priority }}</span>
          </li>
        </ul>
      </section>

      <section v-if="loadErrors.length > 0" class="cr-w-pr-section cr-w-pr-section-errors">
        <h3 class="cr-w-pr-section-title">Load errors ({{ loadErrors.length }})</h3>
        <ul class="cr-w-pr-error-list">
          <li v-for="(err, i) in loadErrors" :key="i" class="cr-w-pr-error-row">
            <q-icon name="error_outline" size="14px" />
            <span class="cr-w-pr-conv-id">{{ err.source ?? '?' }}</span>
            <span class="cr-w-pr-error-msg">{{ err.message }}</span>
          </li>
        </ul>
      </section>
    </div>

    <footer class="cr-w-pr-foot">
      <q-btn
        flat
        dense
        size="sm"
        icon="autorenew"
        label="Run projection sync"
        :loading="syncing"
        :disable="syncing"
        @click="runSync"
      />
      <span v-if="lastReport" class="cr-w-pr-last">
        Last run: {{ lastReport.entitiesByKindCount }} kinds,
        {{ lastReport.refsResolved }} refs resolved,
        {{ lastReport.refsUnresolved }} unresolved.
      </span>
    </footer>
  </div>
</template>

<script setup lang="ts">
/**
 * ProjectionReport — current state of the workspace's projection layer.
 *
 * v1 reads the conventions list (`GET /conventions/`) to surface
 * registered conventions + any YAML load errors. The "Run projection
 * sync" button calls `POST /projections/sync` on demand and renders the
 * returned report's totals. There's no GET endpoint for "last sync's
 * report yet" — that requires a small backend change to persist the
 * report and expose it. Until then, the widget is a snapshot of
 * conventions plus a manual sync trigger that reports its own results.
 */
import { onMounted, ref, watch } from 'vue';
import { Projections } from 'src/api';
import type { ConventionLoadError, ConventionSummary } from 'src/api';

const props = defineProps<{
  widgetType: string;
  propsData: Record<string, unknown>;
  workspaceSlug?: string | null;
}>();

const conventions = ref<ConventionSummary[]>([]);
const loadErrors = ref<ConventionLoadError[]>([]);
const loading = ref(false);
const loaded = ref(false);
const error = ref<string | null>(null);
const syncing = ref(false);
interface ReportSummary {
  entitiesByKindCount: number;
  refsResolved: number;
  refsUnresolved: number;
}
const lastReport = ref<ReportSummary | null>(null);

async function refresh() {
  if (!props.workspaceSlug) return;
  loading.value = true;
  error.value = null;
  try {
    const res = await Projections.listConventions({ path: { slug: props.workspaceSlug } });
    conventions.value = res.data?.result?.conventions ?? [];
    loadErrors.value = res.data?.result?.errors ?? [];
    loaded.value = true;
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Failed to load conventions.';
  } finally {
    loading.value = false;
  }
}

async function runSync() {
  if (!props.workspaceSlug) return;
  syncing.value = true;
  try {
    const res = await Projections.syncProjection({ path: { slug: props.workspaceSlug } });
    const report = res.data?.result?.report;
    if (report) {
      lastReport.value = {
        entitiesByKindCount: Object.keys(report.entitiesByKind ?? {}).length,
        refsResolved: numberFrom(report.refsResolved),
        refsUnresolved: numberFrom(report.refsUnresolved),
      };
    }
    await refresh();
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Sync failed.';
  } finally {
    syncing.value = false;
  }
}

function numberFrom(v: number | string | null | undefined): number {
  if (typeof v === 'number') return v;
  if (typeof v === 'string') return parseInt(v, 10) || 0;
  return 0;
}

// `propsData` is required by the WidgetHost contract but the projection
// report widget has no per-instance configuration today. Reference it so
// Vue's reactive system tracks the prop without flagging it unused.
void props.propsData;

onMounted(() => {
  void refresh();
});
watch(
  () => props.workspaceSlug,
  () => {
    void refresh();
  },
);
</script>

<style lang="scss" scoped>
.cr-w-pr {
  height: 100%;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-elevated, #1a1a1d);
}

.cr-w-pr-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border-bottom: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
  background: var(--cr-bg-subtle, #1f1f22);
}

.cr-w-pr-title {
  font-size: 11px;
  color: var(--cr-fg-tertiary, #888);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.cr-w-pr-state {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--cr-fg-tertiary, #888);
  font-size: 12px;
}

.cr-w-pr-error { color: var(--cr-fg-secondary, #ccc); }

.cr-w-pr-body {
  flex: 1;
  overflow: auto;
  padding: 12px 16px;
}

.cr-w-pr-section {
  margin-bottom: 16px;
}

.cr-w-pr-section-title {
  margin: 0 0 6px;
  font-size: 11px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.16em;
  color: var(--cr-fg-tertiary, #888);
}

.cr-w-pr-empty {
  font-size: 12px;
  color: var(--cr-fg-tertiary, #888);
  font-style: italic;

  code {
    background: var(--cr-bg-subtle, #262629);
    padding: 1px 5px;
    border-radius: 3px;
    font-family: var(--cr-font-mono, ui-monospace, monospace);
  }
}

.cr-w-pr-conv-list,
.cr-w-pr-error-list {
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.cr-w-pr-conv-row,
.cr-w-pr-error-row {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  padding: 4px 0;
}

.cr-w-pr-conv-id {
  font-family: var(--cr-font-mono, ui-monospace, monospace);
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-w-pr-conv-glob {
  font-family: var(--cr-font-mono, ui-monospace, monospace);
  font-size: 11px;
  color: var(--cr-fg-tertiary, #888);
}

.cr-w-pr-conv-prio {
  margin-left: auto;
  font-size: 10px;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary, #888);
}

.cr-w-pr-error-row {
  background: rgba(239, 68, 68, 0.06);
  border-left: 2px solid rgb(248, 113, 113);
  padding-left: 8px;
}

.cr-w-pr-error-msg {
  color: rgb(248, 113, 113);
  font-size: 11px;
}

.cr-w-pr-foot {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 12px;
  border-top: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
  background: var(--cr-bg-subtle, #1f1f22);
}

.cr-w-pr-last {
  font-size: 11px;
  color: var(--cr-fg-tertiary, #888);
}
</style>
