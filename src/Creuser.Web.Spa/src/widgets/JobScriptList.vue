<template>
  <div class="cr-w-list">
    <div class="cr-w-list-toolbar">
      <span class="cr-w-list-count">{{ filtered.length }} of {{ scripts.length }}</span>
      <q-space />
      <q-btn-toggle
        v-model="patternFilter"
        :options="patternOptions"
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

    <div v-if="loading && scripts.length === 0" class="cr-w-list-state">
      <q-spinner size="24px" color="primary" />
      <span>Loading scripts…</span>
    </div>
    <div v-else-if="error" class="cr-w-list-state cr-w-list-error">
      <q-icon name="error_outline" size="24px" />
      <span>{{ error }}</span>
    </div>
    <div v-else-if="filtered.length === 0" class="cr-w-list-state">
      <q-icon name="description" size="24px" />
      <span>No job scripts in this workspace yet.</span>
    </div>
    <q-virtual-scroll v-else class="cr-w-list-list" :items="filtered" separator v-slot="{ item }">
      <q-item :key="item.jobScriptId" class="cr-w-list-row">
        <q-item-section avatar>
          <q-icon
            :name="iconFor(item.pattern)"
            :color="item.status === 'active' ? 'primary' : 'grey-6'"
            size="20px"
          />
        </q-item-section>
        <q-item-section>
          <q-item-label class="cr-w-list-row-name">
            {{ item.name }}
            <span v-if="item.status !== 'active'" class="cr-w-list-row-disabled">
              {{ item.status }}
            </span>
          </q-item-label>
          <q-item-label caption class="cr-w-list-row-meta">
            <span class="cr-w-list-row-cron">{{ item.slug }}</span>
            <span v-if="item.description">· {{ item.description.slice(0, 60) }}</span>
          </q-item-label>
        </q-item-section>
        <q-item-section side class="cr-w-list-row-actions">
          <span class="cr-w-list-row-kind">{{ item.pattern }}</span>
          <q-btn
            flat
            dense
            round
            icon="play_arrow"
            size="sm"
            :loading="firingId === item.jobScriptId"
            @click="runScript(item)"
          >
            <q-tooltip>Run now</q-tooltip>
          </q-btn>
        </q-item-section>
      </q-item>
    </q-virtual-scroll>
  </div>
</template>

<script setup lang="ts">
/**
 * JobScriptList — workspace's job scripts with a "Run now" affordance per
 * row. The ad-hoc-fire endpoint is the same one the Schedules page uses
 * (POST /api/workspaces/{slug}/jobs/{id}/run); a successful fire emits a
 * SignalR notification that the RunsList widget picks up so the new run
 * appears next to it.
 */
import { computed, onMounted, ref, watch } from 'vue';
import { Jobs } from 'src/api';
import type { JobScriptResult } from 'src/api';

const props = defineProps<{
  widgetType: string;
  propsData: { limit?: number };
  workspaceSlug?: string | null;
}>();

const scripts = ref<JobScriptResult[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);
const firingId = ref<string | null>(null);
const patternFilter = ref<string>('all');

const patternOptions = [
  { value: 'all', label: 'All' },
  { value: 'deterministic', label: 'Det' },
  { value: 'agentic', label: 'Agent' },
  { value: 'plan-then-execute', label: 'Plan' },
];

const limit = computed(() => Math.max(1, Math.min(500, props.propsData.limit ?? 50)));

const filtered = computed<JobScriptResult[]>(() => {
  const subset =
    patternFilter.value === 'all'
      ? scripts.value
      : scripts.value.filter((s) => s.pattern === patternFilter.value);
  return subset.slice(0, limit.value);
});

async function refresh() {
  if (!props.workspaceSlug) return;
  loading.value = true;
  error.value = null;
  try {
    const res = await Jobs.listJobs({ path: { slug: props.workspaceSlug } });
    scripts.value = res.data?.result ?? [];
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Failed to load scripts.';
  } finally {
    loading.value = false;
  }
}

async function runScript(script: JobScriptResult) {
  if (!props.workspaceSlug) return;
  firingId.value = script.jobScriptId;
  try {
    await Jobs.runJob({
      path: { slug: props.workspaceSlug, jobId: script.jobScriptId },
      body: { parameters: {} },
    });
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Run failed.';
  } finally {
    firingId.value = null;
  }
}

function iconFor(pattern: string): string {
  switch (pattern) {
    case 'deterministic':
      return 'description';
    case 'agentic':
      return 'auto_awesome';
    case 'plan-then-execute':
      return 'account_tree';
    default:
      return 'description';
  }
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
@use './_widget-list.scss';

.cr-w-list-row-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}
</style>
