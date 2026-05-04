<template>
  <div class="cr-w-runs">
    <div class="cr-w-runs-toolbar">
      <span class="cr-w-runs-count">{{ filtered.length }} of {{ runs.length }}</span>
      <q-space />
      <q-btn-toggle
        v-model="activeFilter"
        :options="filterOptions"
        size="sm"
        flat
        dense
        toggle-color="primary"
        text-color="grey-6"
      />
      <q-btn flat dense round icon="refresh" size="sm" :loading="loading" @click="refresh">
        <q-tooltip>Reload</q-tooltip>
      </q-btn>
    </div>

    <div v-if="loading && runs.length === 0" class="cr-w-runs-state">
      <q-spinner size="24px" color="primary" />
      <span>Loading runs…</span>
    </div>
    <div v-else-if="error" class="cr-w-runs-state cr-w-runs-error">
      <q-icon name="error_outline" size="24px" />
      <span>{{ error }}</span>
    </div>
    <div v-else-if="filtered.length === 0" class="cr-w-runs-state">
      <q-icon name="inbox" size="24px" />
      <span>No runs match the current filter.</span>
    </div>
    <q-virtual-scroll v-else class="cr-w-runs-list" :items="filtered" separator v-slot="{ item }">
      <q-item :key="item.runId" clickable class="cr-w-runs-row" @click="openRun(item)">
        <q-item-section avatar>
          <q-icon :name="iconFor(item.status)" :color="colorFor(item.status)" size="20px" />
        </q-item-section>
        <q-item-section>
          <q-item-label class="cr-w-runs-row-id">
            {{ item.runId.slice(0, 8) }}
            <span class="cr-w-runs-row-trigger">{{ item.triggerKind }}</span>
          </q-item-label>
          <q-item-label caption>
            {{ formatStarted(item.startedAt) }}
            ·
            {{ formatDuration(item.durationMs) }}
            <span
              v-if="item.failureMessage"
              class="cr-w-runs-row-error"
              :title="item.failureMessage"
            >
              · {{ item.failureMessage.slice(0, 60) }}
            </span>
          </q-item-label>
        </q-item-section>
        <q-item-section side>
          <span class="cr-w-runs-row-status" :class="`cr-w-runs-row-status-${item.status}`">
            {{ item.status }}
          </span>
        </q-item-section>
      </q-item>
    </q-virtual-scroll>
  </div>
</template>

<script setup lang="ts">
/**
 * RunsList — paginated runs across the workspace with status filter.
 *
 * Reads `propsData` (`{ limit, statusFilter }`) from the dashboard's
 * widget instance and pulls runs from `/api/workspaces/{slug}/runs/`.
 * Status filter is local-only; the API returns the latest 100 runs and
 * we filter in-memory because the typical workspace's run set is small
 * enough that round-tripping per filter change is wasted latency.
 *
 * Click a row → navigate to the run detail (placeholder — currently
 * no /runs/{id} view; TODO: open RunInspector in a side panel via the
 * inter-widget messaging slot when that lands).
 */
import { computed, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { Jobs } from 'src/api';
import type { JobRunResult } from 'src/api';

const props = defineProps<{
  widgetType: string;
  propsData: { limit?: number; statusFilter?: string };
  workspaceSlug?: string | null;
}>();

const router = useRouter();
const runs = ref<JobRunResult[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);
const activeFilter = ref<string>(props.propsData.statusFilter ?? 'all');

const filterOptions = [
  { value: 'all', label: 'All' },
  { value: 'succeeded', label: 'OK' },
  { value: 'failed', label: 'Failed' },
  { value: 'running', label: 'Running' },
];

const limit = computed(() => Math.max(1, Math.min(500, props.propsData.limit ?? 25)));

const filtered = computed<JobRunResult[]>(() => {
  const subset =
    activeFilter.value === 'all'
      ? runs.value
      : runs.value.filter((r) => r.status === activeFilter.value);
  return subset.slice(0, limit.value);
});

async function refresh() {
  if (!props.workspaceSlug) return;
  loading.value = true;
  error.value = null;
  try {
    const res = await Jobs.listWorkspaceRuns({ path: { slug: props.workspaceSlug } });
    runs.value = res.data?.result ?? [];
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Failed to load runs.';
  } finally {
    loading.value = false;
  }
}

function openRun(r: JobRunResult) {
  // Run detail navigation lands when the RunInspector widget is wired
  // in. For now, log + no-op so the click doesn't feel broken.
  console.info('[RunsList] open run', r.runId);
  void router; // imported for the future router.push
}

function iconFor(status: string): string {
  switch (status) {
    case 'succeeded':
      return 'check_circle';
    case 'failed':
      return 'error';
    case 'cancelled':
      return 'cancel';
    case 'running':
      return 'play_circle';
    default:
      return 'help';
  }
}

function colorFor(status: string): string {
  switch (status) {
    case 'succeeded':
      return 'positive';
    case 'failed':
      return 'negative';
    case 'cancelled':
      return 'grey-6';
    case 'running':
      return 'primary';
    default:
      return 'grey-7';
  }
}

function formatStarted(iso: string): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const diffMs = Date.now() - d.getTime();
  const mins = Math.round(diffMs / 60000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  return d.toLocaleDateString();
}

function formatDuration(ms: number | string): string {
  const n = typeof ms === 'string' ? parseInt(ms, 10) : ms;
  if (!Number.isFinite(n) || n < 0) return '—';
  if (n < 1000) return `${n}ms`;
  if (n < 60_000) return `${(n / 1000).toFixed(1)}s`;
  return `${Math.round(n / 60_000)}m`;
}

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
.cr-w-runs {
  height: 100%;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-elevated, #1a1a1d);
}

.cr-w-runs-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border-bottom: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
  background: var(--cr-bg-subtle, #1f1f22);
}

.cr-w-runs-count {
  font-size: 11px;
  color: var(--cr-fg-tertiary, #888);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.cr-w-runs-list {
  flex: 1;
  min-height: 0;
}

.cr-w-runs-state {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--cr-fg-tertiary, #888);
  font-size: 12px;
}

.cr-w-runs-error {
  color: var(--cr-fg-secondary, #ccc);
}

.cr-w-runs-row {
  padding: 6px 12px;
}

.cr-w-runs-row-id {
  font-family: var(--cr-font-mono, ui-monospace, monospace);
  font-size: 12px;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-w-runs-row-trigger {
  font-family: inherit;
  font-size: 10px;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--cr-fg-tertiary, #888);
  margin-left: 6px;
}

.cr-w-runs-row-error {
  color: var(--cr-fg-tertiary, #888);
  font-style: italic;
}

.cr-w-runs-row-status {
  font-size: 10px;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  padding: 2px 6px;
  border-radius: 3px;
  background: var(--cr-bg-elevated, #262629);
  color: var(--cr-fg-secondary, #ccc);
}

.cr-w-runs-row-status-succeeded {
  background: rgba(34, 197, 94, 0.12);
  color: rgb(74, 222, 128);
}

.cr-w-runs-row-status-failed {
  background: rgba(239, 68, 68, 0.12);
  color: rgb(248, 113, 113);
}

.cr-w-runs-row-status-running {
  background: rgba(59, 130, 246, 0.12);
  color: rgb(96, 165, 250);
}
</style>
