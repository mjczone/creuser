<template>
  <div class="cr-w-run">
    <div v-if="!resolvedRunId" class="cr-w-run-state">
      <q-icon name="troubleshoot" size="32px" />
      <p>Configure this widget with a <code>runId</code> to inspect a run.</p>
    </div>
    <div v-else-if="loading && !detail" class="cr-w-run-state">
      <q-spinner size="32px" color="primary" />
      <p>Loading run…</p>
    </div>
    <div v-else-if="error" class="cr-w-run-state cr-w-run-error">
      <q-icon name="error_outline" size="32px" />
      <p>{{ error }}</p>
    </div>
    <template v-else-if="detail">
      <header class="cr-w-run-header">
        <div class="cr-w-run-headline">
          <q-icon
            :name="iconFor(detail.run.status)"
            :color="colorFor(detail.run.status)"
            size="24px"
          />
          <div class="cr-w-run-title">
            <span class="cr-w-run-id">{{ detail.run.runId.slice(0, 8) }}</span>
            <span class="cr-w-run-status" :class="`cr-w-run-status-${detail.run.status}`">
              {{ detail.run.status }}
            </span>
          </div>
          <q-space />
          <q-btn flat dense round icon="refresh" size="sm" :loading="loading" @click="refresh">
            <q-tooltip>Reload</q-tooltip>
          </q-btn>
        </div>
        <div class="cr-w-run-meta">
          <span>trigger: {{ detail.run.triggerKind }}</span>
          <span v-if="detail.run.startedAt">started: {{ fmtDate(detail.run.startedAt) }}</span>
          <span>duration: {{ fmtDuration(detail.run.durationMs) }}</span>
          <span v-if="detail.run.totalTokensUsed"> tokens: {{ detail.run.totalTokensUsed }} </span>
        </div>
        <p v-if="detail.run.failureMessage" class="cr-w-run-failure">
          {{ detail.run.failureMessage }}
        </p>
      </header>

      <section class="cr-w-run-steps">
        <h3 class="cr-w-run-steps-title">Steps ({{ detail.steps.length }})</h3>
        <div v-for="step in detail.steps" :key="step.stepId" class="cr-w-run-step">
          <div class="cr-w-run-step-header">
            <q-icon :name="iconFor(step.status)" :color="colorFor(step.status)" size="16px" />
            <span class="cr-w-run-step-name">{{ step.name }}</span>
            <span class="cr-w-run-step-type">{{ step.stepType }}</span>
            <q-space />
            <span class="cr-w-run-step-duration">{{ fmtDuration(step.durationMs) }}</span>
          </div>
          <p v-if="step.errorMessage" class="cr-w-run-step-error">
            {{ step.errorMessage }}
          </p>
        </div>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
/**
 * RunInspector — single-run detail with step-by-step transitions.
 *
 * Reads `runId` from props (set by the dashboard config) or — when the
 * `runId` prop is null — falls back to `cr-active-run-id` provided by
 * the parent dashboard. This is the seam for the future
 * "click-row-in-RunsList-to-open-RunInspector" wiring.
 */
import { computed, inject, onMounted, ref, watch } from 'vue';
import type { Ref } from 'vue';
import { Jobs } from 'src/api';
import type { JobRunDetailResult } from 'src/api';

const props = defineProps<{
  widgetType: string;
  propsData: { runId?: string | null };
  workspaceSlug?: string | null;
}>();

const activeRunId = inject<Ref<string | null>>(
  'cr-active-run-id',
  null as unknown as Ref<string | null>,
);

const resolvedRunId = computed<string | null>(
  () => props.propsData?.runId ?? activeRunId?.value ?? null,
);

const detail = ref<JobRunDetailResult | null>(null);
const loading = ref(false);
const error = ref<string | null>(null);

async function refresh() {
  if (!props.workspaceSlug || !resolvedRunId.value) {
    detail.value = null;
    return;
  }
  loading.value = true;
  error.value = null;
  try {
    const res = await Jobs.getRun({
      path: { slug: props.workspaceSlug, runId: resolvedRunId.value },
    });
    detail.value = res.data?.result ?? null;
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Failed to load run.';
  } finally {
    loading.value = false;
  }
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

function fmtDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleString();
}

function fmtDuration(ms: number | string): string {
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
  () => [props.workspaceSlug, resolvedRunId.value],
  () => {
    void refresh();
  },
);
</script>

<style lang="scss" scoped>
.cr-w-run {
  height: 100%;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-elevated, #1a1a1d);
  overflow: auto;
}

.cr-w-run-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--cr-fg-tertiary, #888);
  padding: 24px;
  text-align: center;
}

.cr-w-run-error {
  color: var(--cr-fg-secondary, #ccc);
}

.cr-w-run-header {
  padding: 12px 16px;
  border-bottom: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
}

.cr-w-run-headline {
  display: flex;
  align-items: center;
  gap: 8px;
}

.cr-w-run-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.cr-w-run-id {
  font-family: var(--cr-font-mono, ui-monospace, monospace);
  font-size: 14px;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-w-run-status {
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  padding: 2px 8px;
  border-radius: 3px;
  background: var(--cr-bg-subtle, #1f1f22);
  color: var(--cr-fg-secondary, #ccc);
}
.cr-w-run-status-succeeded {
  background: rgba(34, 197, 94, 0.12);
  color: rgb(74, 222, 128);
}
.cr-w-run-status-failed {
  background: rgba(239, 68, 68, 0.12);
  color: rgb(248, 113, 113);
}
.cr-w-run-status-running {
  background: rgba(59, 130, 246, 0.12);
  color: rgb(96, 165, 250);
}

.cr-w-run-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-top: 6px;
  font-size: 11px;
  color: var(--cr-fg-tertiary, #888);
}

.cr-w-run-failure {
  margin: 8px 0 0;
  padding: 8px 10px;
  background: rgba(239, 68, 68, 0.08);
  border-left: 2px solid rgb(248, 113, 113);
  font-size: 12px;
  color: var(--cr-fg-secondary, #ccc);
}

.cr-w-run-steps {
  padding: 12px 16px;
}

.cr-w-run-steps-title {
  margin: 0 0 8px;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary, #888);
}

.cr-w-run-step {
  padding: 6px 0;
  border-bottom: 1px dashed var(--cr-border-subtle, rgba(255, 255, 255, 0.06));

  &:last-child {
    border-bottom: none;
  }
}

.cr-w-run-step-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
}

.cr-w-run-step-name {
  font-weight: 500;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-w-run-step-type {
  font-family: var(--cr-font-mono, ui-monospace, monospace);
  font-size: 10px;
  color: var(--cr-fg-tertiary, #888);
}

.cr-w-run-step-duration {
  font-size: 10px;
  color: var(--cr-fg-tertiary, #888);
}

.cr-w-run-step-error {
  margin: 4px 0 0 24px;
  font-size: 11px;
  color: rgb(248, 113, 113);
}
</style>
