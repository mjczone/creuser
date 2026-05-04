<template>
  <div class="cr-w-list">
    <div class="cr-w-list-toolbar">
      <span class="cr-w-list-count">{{ filtered.length }} of {{ schedules.length }}</span>
      <q-space />
      <q-btn-toggle
        v-model="kindFilter"
        :options="kindOptions"
        size="sm"
        flat
        dense
        toggle-color="primary"
        text-color="grey-6"
      />
      <q-btn
        flat
        dense
        round
        icon="refresh"
        size="sm"
        :loading="loading"
        @click="refresh"
      >
        <q-tooltip>Reload</q-tooltip>
      </q-btn>
    </div>

    <div v-if="loading && schedules.length === 0" class="cr-w-list-state">
      <q-spinner size="24px" color="primary" />
      <span>Loading schedules…</span>
    </div>
    <div v-else-if="error" class="cr-w-list-state cr-w-list-error">
      <q-icon name="error_outline" size="24px" />
      <span>{{ error }}</span>
    </div>
    <div v-else-if="filtered.length === 0" class="cr-w-list-state">
      <q-icon name="schedule" size="24px" />
      <span>No schedules configured.</span>
    </div>
    <q-virtual-scroll
      v-else
      class="cr-w-list-list"
      :items="filtered"
      separator
      v-slot="{ item }"
    >
      <q-item :key="item.scheduleId" class="cr-w-list-row">
        <q-item-section avatar>
          <q-icon
            :name="item.kind === 'cron' ? 'event_repeat' : 'sync'"
            :color="item.enabled ? 'primary' : 'grey-6'"
            size="20px"
          />
        </q-item-section>
        <q-item-section>
          <q-item-label class="cr-w-list-row-name">
            {{ item.jobName }}
            <span v-if="!item.enabled" class="cr-w-list-row-disabled">disabled</span>
          </q-item-label>
          <q-item-label caption class="cr-w-list-row-meta">
            <span v-if="item.kind === 'cron' && item.cronExpression" class="cr-w-list-row-cron">
              {{ item.cronExpression }}
            </span>
            <span v-else class="cr-w-list-row-cron">post-sync</span>
            ·
            <span>next: {{ formatRelative(item.nextDueAt) }}</span>
            ·
            <span>last: {{ formatRelative(item.lastFiredAt) }}</span>
          </q-item-label>
        </q-item-section>
        <q-item-section side>
          <span class="cr-w-list-row-kind">{{ item.kind }}</span>
        </q-item-section>
      </q-item>
    </q-virtual-scroll>
  </div>
</template>

<script setup lang="ts">
/**
 * ScheduleList — cron + post-sync schedules with next-due / last-fired
 * chips. Read-only in v1; the workspace settings page is where operators
 * create + edit + fire schedules.
 */
import { computed, onMounted, ref, watch } from 'vue';
import { Schedules } from 'src/api';
import type { ScheduleResult } from 'src/api';

const props = defineProps<{
  widgetType: string;
  propsData: { limit?: number };
  workspaceSlug?: string | null;
}>();

const schedules = ref<ScheduleResult[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);
const kindFilter = ref<string>('all');

const kindOptions = [
  { value: 'all', label: 'All' },
  { value: 'cron', label: 'Cron' },
  { value: 'sync', label: 'Sync' },
];

const limit = computed(() => Math.max(1, Math.min(500, props.propsData.limit ?? 50)));

const filtered = computed<ScheduleResult[]>(() => {
  const subset =
    kindFilter.value === 'all'
      ? schedules.value
      : schedules.value.filter((s) => s.kind === kindFilter.value);
  return subset.slice(0, limit.value);
});

async function refresh() {
  if (!props.workspaceSlug) return;
  loading.value = true;
  error.value = null;
  try {
    const res = await Schedules.listSchedules({ path: { slug: props.workspaceSlug } });
    schedules.value = res.data?.result ?? [];
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Failed to load schedules.';
  } finally {
    loading.value = false;
  }
}

function formatRelative(iso: string | null): string {
  if (!iso) return 'never';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const diffMs = d.getTime() - Date.now();
  const future = diffMs > 0;
  const absMins = Math.round(Math.abs(diffMs) / 60000);
  if (absMins < 1) return future ? 'momentarily' : 'just now';
  if (absMins < 60) return future ? `in ${absMins}m` : `${absMins}m ago`;
  const hours = Math.round(absMins / 60);
  if (hours < 24) return future ? `in ${hours}h` : `${hours}h ago`;
  return d.toLocaleDateString();
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
</style>
