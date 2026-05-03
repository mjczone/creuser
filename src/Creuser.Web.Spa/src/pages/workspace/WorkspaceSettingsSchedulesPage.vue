<template>
  <div class="q-pa-lg cr-schedules">
    <header class="cr-schedules-header">
      <div class="cr-schedules-titlerow">
        <h1 class="text-h5 q-ma-none">Schedules</h1>
        <q-btn
          color="primary"
          unelevated
          no-caps
          icon="add"
          label="New schedule"
          class="q-ml-auto"
          :disable="!jobs.length"
          @click="openCreate"
        />
      </div>
      <p class="cr-schedules-subhead">
        Schedules fire jobs automatically. <strong>Cron</strong> schedules tick
        on a UTC cron expression (5 fields <code>m h dom mon dow</code> or 6
        with seconds). <strong>Sync</strong> schedules fire after every
        successful workspace sync — useful for "rebuild summary on every pull"
        loops. Use <em>Fire now</em> to run a schedule immediately, bypassing
        the tick.
      </p>
      <p v-if="!jobs.length" class="cr-schedules-empty-hint">
        No jobs in this workspace yet. Create one in <router-link :to="`/w/${slug}/settings/jobs`">Jobs</router-link>
        before scheduling it.
      </p>
    </header>

    <q-table
      :rows="schedules"
      :columns="cols"
      row-key="scheduleId"
      :loading="loading"
      flat
      bordered
      dense
      no-data-label="No schedules yet."
    >
      <template #body-cell-kind="props">
        <q-td :props="props">
          <q-chip
            dense
            outline
            :color="props.row.kind === 'cron' ? 'primary' : 'positive'"
            :text-color="props.row.kind === 'cron' ? 'primary' : 'positive'"
          >
            {{ props.row.kind }}
          </q-chip>
        </q-td>
      </template>
      <template #body-cell-cronExpression="props">
        <q-td :props="props">
          <code v-if="props.row.cronExpression" class="cr-schedules-tag">{{
            props.row.cronExpression
          }}</code>
          <span v-else class="cr-schedules-muted">—</span>
        </q-td>
      </template>
      <template #body-cell-enabled="props">
        <q-td :props="props">
          <q-chip
            dense
            outline
            :color="props.row.enabled ? 'positive' : 'grey-7'"
            :text-color="props.row.enabled ? 'positive' : 'grey-7'"
          >
            {{ props.row.enabled ? 'enabled' : 'disabled' }}
          </q-chip>
        </q-td>
      </template>
      <template #body-cell-nextDueAt="props">
        <q-td :props="props">{{ formatRelative(props.row.nextDueAt) }}</q-td>
      </template>
      <template #body-cell-lastFiredAt="props">
        <q-td :props="props">{{ formatRelative(props.row.lastFiredAt) }}</q-td>
      </template>
      <template #body-cell-actions="props">
        <q-td :props="props" auto-width>
          <q-btn
            flat
            dense
            round
            icon="play_arrow"
            size="sm"
            color="primary"
            :loading="firingId === props.row.scheduleId"
            :aria-label="`Fire ${props.row.jobName} now`"
            @click="onFire(props.row)"
          >
            <q-tooltip>Fire now</q-tooltip>
          </q-btn>
          <q-btn
            flat
            dense
            round
            icon="edit"
            size="sm"
            aria-label="Edit"
            @click="openEdit(props.row)"
          />
          <q-btn
            flat
            dense
            round
            icon="delete"
            size="sm"
            color="negative"
            :aria-label="`Delete schedule for ${props.row.jobName}`"
            @click="onDelete(props.row)"
          />
        </q-td>
      </template>
    </q-table>

    <q-dialog v-model="dialogOpen" persistent>
      <q-card class="cr-schedules-dialog">
        <q-card-section>
          <div class="text-h6">{{ editingId ? 'Edit schedule' : 'New schedule' }}</div>
          <div class="text-caption" :style="{ color: 'var(--cr-fg-secondary)' }">
            Pick a job, a trigger kind, and (for cron) a UTC expression.
          </div>
        </q-card-section>
        <q-card-section>
          <q-form class="q-gutter-md" @submit.prevent="onSubmit">
            <q-select
              v-model="form.jobScriptId"
              :options="jobOptions"
              label="Job"
              dense
              outlined
              emit-value
              map-options
              :readonly="!!editingId"
              :rules="[(v) => !!v || 'Required']"
            />
            <q-select
              v-model="form.kind"
              :options="kindOptions"
              label="Trigger kind"
              dense
              outlined
              emit-value
              map-options
            />
            <q-input
              v-if="form.kind === 'cron'"
              v-model="form.cronExpression"
              label="Cron expression (UTC)"
              dense
              outlined
              hint="5 fields `m h dom mon dow` or 6 with seconds. Examples: `0 6 * * *` (daily 06:00 UTC), `*/15 * * * *` (every 15 min)."
              :rules="cronRules"
              input-class="cr-schedules-mono"
            />
            <q-toggle v-model="form.enabled" label="Enabled" />

            <div v-if="error" class="text-negative text-caption">{{ error }}</div>

            <div class="row justify-end q-gutter-sm">
              <q-btn flat label="Cancel" no-caps @click="closeDialog" />
              <q-btn
                type="submit"
                :label="editingId ? 'Save' : 'Create'"
                color="primary"
                unelevated
                no-caps
                :loading="submitting"
              />
            </div>
          </q-form>
        </q-card-section>
      </q-card>
    </q-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { useQuasar, type QTableColumn } from 'quasar';
import {
  Jobs,
  Schedules,
  type JobScriptResult,
  type ScheduleResult,
} from 'src/api';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';

const $q = useQuasar();
const { slug } = useActiveWorkspace();

const schedules = ref<ScheduleResult[]>([]);
const jobs = ref<JobScriptResult[]>([]);
const loading = ref(false);
const dialogOpen = ref(false);
const editingId = ref<string | null>(null);
const submitting = ref(false);
const firingId = ref<string | null>(null);
const error = ref('');

interface FormState {
  jobScriptId: string;
  kind: string;
  cronExpression: string;
  enabled: boolean;
}

const form = reactive<FormState>(emptyForm());

const kindOptions = [
  { label: 'Cron — fire on a schedule', value: 'cron' },
  { label: 'Sync — fire after workspace sync', value: 'sync' },
];

const jobOptions = computed(() =>
  jobs.value.map((j) => ({ label: `${j.name} (${j.slug})`, value: j.jobScriptId })),
);

// Cron field is only required when kind === 'cron'. Validation is best-effort
// here — the server's NCrontab parse is the canonical check.
const cronRules = [
  (v: string) => form.kind !== 'cron' || !!v?.trim() || 'Required for cron schedules.',
  (v: string) => {
    if (form.kind !== 'cron' || !v?.trim()) return true;
    const fields = v.trim().split(/\s+/).length;
    return fields === 5 || fields === 6 || 'Cron expression must have 5 or 6 fields.';
  },
];

const cols: QTableColumn<ScheduleResult>[] = [
  { name: 'jobName', label: 'Job', field: 'jobName', align: 'left', sortable: true },
  { name: 'kind', label: 'Kind', field: 'kind', align: 'left' },
  { name: 'cronExpression', label: 'Cron', field: 'cronExpression', align: 'left' },
  { name: 'enabled', label: 'Status', field: 'enabled', align: 'left' },
  { name: 'nextDueAt', label: 'Next due', field: 'nextDueAt', align: 'left' },
  { name: 'lastFiredAt', label: 'Last fired', field: 'lastFiredAt', align: 'left' },
  { name: 'actions', label: '', field: () => '', align: 'right' },
];

function formatRelative(when: string | null | undefined): string {
  if (!when) return '—';
  const d = new Date(when);
  if (Number.isNaN(d.getTime())) return '—';
  const diffMs = d.getTime() - Date.now();
  const future = diffMs > 0;
  const absMins = Math.round(Math.abs(diffMs) / 60000);
  if (absMins < 1) return future ? 'in <1m' : 'just now';
  if (absMins < 60) return future ? `in ${absMins}m` : `${absMins}m ago`;
  const hrs = Math.round(absMins / 60);
  if (hrs < 24) return future ? `in ${hrs}h` : `${hrs}h ago`;
  return d.toLocaleString();
}

function emptyForm(): FormState {
  return {
    jobScriptId: '',
    kind: 'cron',
    cronExpression: '0 6 * * *',
    enabled: true,
  };
}

async function load() {
  if (!slug.value) return;
  loading.value = true;
  try {
    // Schedules + jobs in parallel — both inform the table render and the
    // create dialog's job-picker.
    const [schedRes, jobRes] = await Promise.all([
      Schedules.listSchedules({ path: { slug: slug.value } }),
      Jobs.listJobs({ path: { slug: slug.value } }),
    ]);
    schedules.value = schedRes.data?.result ?? [];
    jobs.value = jobRes.data?.result ?? [];
  } finally {
    loading.value = false;
  }
}

function openCreate() {
  editingId.value = null;
  Object.assign(form, emptyForm(), {
    jobScriptId: jobs.value[0]?.jobScriptId ?? '',
  });
  error.value = '';
  dialogOpen.value = true;
}

function openEdit(s: ScheduleResult) {
  editingId.value = s.scheduleId;
  Object.assign(form, {
    jobScriptId: s.jobScriptId,
    kind: s.kind,
    cronExpression: s.cronExpression ?? '',
    enabled: s.enabled,
  });
  error.value = '';
  dialogOpen.value = true;
}

function closeDialog() {
  dialogOpen.value = false;
  error.value = '';
}

async function onSubmit() {
  if (!slug.value) return;
  error.value = '';
  submitting.value = true;
  try {
    // Sync schedules forbid a cron expression on the wire; null-out so the
    // server-side validator doesn't reject the request.
    const cronExpression =
      form.kind === 'cron' ? form.cronExpression.trim() || null : null;
    if (editingId.value) {
      const res = await Schedules.updateSchedule({
        path: { slug: slug.value, scheduleId: editingId.value },
        body: {
          kind: form.kind,
          cronExpression,
          enabled: form.enabled,
        },
      });
      if (res.error) {
        error.value = problemMessage(res.error) ?? 'Failed to save schedule.';
        return;
      }
    } else {
      const res = await Schedules.createSchedule({
        path: { slug: slug.value },
        body: {
          jobScriptId: form.jobScriptId,
          kind: form.kind,
          cronExpression,
          enabled: form.enabled,
        },
      });
      if (res.error) {
        error.value = problemMessage(res.error) ?? 'Failed to create schedule.';
        return;
      }
    }
    closeDialog();
    void load();
  } finally {
    submitting.value = false;
  }
}

async function onFire(s: ScheduleResult) {
  if (!slug.value) return;
  firingId.value = s.scheduleId;
  try {
    const res = await Schedules.fireSchedule({
      path: { slug: slug.value, scheduleId: s.scheduleId },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Manual fire failed.',
      });
      return;
    }
    $q.notify({
      type: 'positive',
      position: 'top',
      message: `${s.jobName}: dispatched`,
      timeout: 4000,
    });
    void load();
  } finally {
    firingId.value = null;
  }
}

function onDelete(s: ScheduleResult) {
  if (!slug.value) return;
  $q.dialog({
    title: 'Delete schedule?',
    message: `<p>Stop firing <strong>${s.jobName}</strong> on this schedule? Run history is preserved.</p>`,
    html: true,
    ok: { label: 'Delete', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async () => {
    if (!slug.value) return;
    const res = await Schedules.deleteSchedule({
      path: { slug: slug.value, scheduleId: s.scheduleId },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Delete failed.',
      });
      return;
    }
    void load();
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

watch(slug, () => void load());
onMounted(() => void load());
</script>

<style lang="scss" scoped>
.cr-schedules-header {
  margin-bottom: 16px;
}

.cr-schedules-titlerow {
  display: flex;
  align-items: center;
  gap: 12px;
}

.cr-schedules-subhead {
  margin: 8px 0 0;
  font-size: 13px;
  color: var(--cr-fg-secondary);
  max-width: 760px;
  line-height: 1.5;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}

.cr-schedules-empty-hint {
  margin: 8px 0 0;
  font-size: 12px;
  color: var(--cr-fg-tertiary);

  a {
    color: var(--q-primary);
    text-decoration: none;
  }
}

.cr-schedules-tag {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  background: var(--cr-bg-elevated);
  color: var(--cr-fg-primary);
  padding: 1px 6px;
  border-radius: 3px;
}

.cr-schedules-muted {
  color: var(--cr-fg-tertiary);
}

.cr-schedules-dialog {
  min-width: 520px;
  max-width: 90vw;
}

:deep(.cr-schedules-mono) {
  font-family: var(--cr-font-family-mono);
  font-size: 12px;
}
</style>
