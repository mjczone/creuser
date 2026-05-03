<template>
  <div class="q-pa-lg cr-jobs">
    <header class="cr-jobs-header">
      <div class="cr-jobs-titlerow">
        <h1 class="text-h5 q-ma-none">Jobs</h1>
        <q-btn
          color="primary"
          unelevated
          no-caps
          icon="add"
          label="New job"
          class="q-ml-auto"
          @click="openCreate"
        />
      </div>
      <p class="cr-jobs-subhead">
        Job scripts compose steps (LLM calls, scripts, file mutations, frontmatter
        edits, HTTP requests) into runs. Each run is recorded with full audit —
        inputs, outputs, token usage, file changes, commit SHAs, replay handles.
        Single-step jobs declare a top-level <code>type:</code> + body; multi-step
        jobs declare a <code>steps:</code> array with <code>depends_on</code> +
        <code>$step_id.field</code> bindings between them. Available step types:
        <code>llm-chat</code>, <code>shell</code>, <code>csharp</code>,
        <code>python</code>, <code>node</code>, <code>file-mutate</code>,
        <code>file-frontmatter</code>, and <code>http</code>. The agentic /
        plan-then-execute patterns land in subsequent passes.
      </p>
    </header>

    <q-table
      :rows="jobs"
      :columns="cols"
      row-key="jobScriptId"
      :loading="loading"
      flat
      bordered
      dense
    >
      <template #body-cell-status="props">
        <q-td :props="props">
          <q-chip
            dense
            outline
            :color="statusColor(props.row.status)"
            :text-color="statusColor(props.row.status)"
          >
            {{ props.row.status }}
          </q-chip>
        </q-td>
      </template>
      <template #body-cell-pattern="props">
        <q-td :props="props">
          <code class="cr-jobs-tag">{{ props.row.pattern }}</code>
        </q-td>
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
            :loading="runningId === props.row.jobScriptId"
            :aria-label="`Run ${props.row.slug}`"
            @click="onRun(props.row)"
          >
            <q-tooltip>Run now</q-tooltip>
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
            :aria-label="`Delete ${props.row.slug}`"
            @click="onDelete(props.row)"
          />
        </q-td>
      </template>
    </q-table>

    <header class="cr-jobs-header q-mt-lg">
      <h2 class="text-h6 q-ma-none">Recent runs</h2>
      <p class="cr-jobs-subhead">
        Most recent <strong>{{ recentRuns.length }}</strong> runs across this
        workspace's jobs.
      </p>
    </header>

    <q-table
      :rows="recentRuns"
      :columns="runCols"
      row-key="runId"
      :loading="runsLoading"
      flat
      bordered
      dense
    >
      <template #body-cell-status="props">
        <q-td :props="props">
          <q-chip
            dense
            outline
            :color="runStatusColor(props.row.status)"
            :text-color="runStatusColor(props.row.status)"
          >
            {{ props.row.status }}
          </q-chip>
        </q-td>
      </template>
      <template #body-cell-startedAt="props">
        <q-td :props="props">{{ formatRelative(props.row.startedAt) }}</q-td>
      </template>
      <template #body-cell-tokens="props">
        <q-td :props="props">{{ props.row.totalTokensUsed ?? '—' }}</q-td>
      </template>
    </q-table>

    <q-dialog v-model="dialogOpen" persistent>
      <q-card class="cr-jobs-dialog">
        <q-card-section>
          <div class="text-h6">{{ editingId ? 'Edit job' : 'New job' }}</div>
          <div class="text-caption" :style="{ color: 'var(--cr-fg-secondary)' }">
            v0.1: single-step jobs. Frontmatter declares the runner type
            (<code>type: llm-chat</code>); body becomes the prompt.
          </div>
        </q-card-section>
        <q-card-section>
          <q-form class="q-gutter-md" @submit.prevent="onSubmit">
            <q-input
              v-model="form.slug"
              label="Slug"
              hint="kebab-case identifier, unique per workspace."
              dense
              outlined
              :readonly="!!editingId"
              :rules="slugRules"
            />
            <q-input v-model="form.name" label="Name" dense outlined />
            <q-input
              v-model="form.description"
              label="Description (optional)"
              dense
              outlined
              autogrow
            />
            <q-select
              v-model="form.pattern"
              :options="patternOptions"
              label="Pattern"
              dense
              outlined
              emit-value
              map-options
            />
            <q-select
              v-model="form.status"
              :options="statusOptions"
              label="Status"
              dense
              outlined
              emit-value
              map-options
            />

            <ToolPicker v-model="form.allowedCommands" />

            <div class="cr-jobs-section-title">Frontmatter (YAML)</div>
            <q-input
              v-model="form.frontmatter"
              type="textarea"
              dense
              outlined
              autogrow
              hint="Declare the step type and any inputs. e.g. `type: llm-chat`. The `allowed_commands:` block is managed by the picker above and reinjected on save."
              input-class="cr-jobs-mono"
            />

            <div class="cr-jobs-section-title">Body</div>
            <q-input
              v-model="form.body"
              type="textarea"
              dense
              outlined
              autogrow
              hint="For llm-chat: the prompt sent to the model."
              input-class="cr-jobs-mono"
            />

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
import { onMounted, reactive, ref, watch } from 'vue';
import { useQuasar, type QTableColumn } from 'quasar';
import {
  Jobs,
  type JobRunResult,
  type JobScriptResult,
} from 'src/api';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';
import {
  injectAllowedCommands,
  splitAllowedCommands,
} from 'src/composables/jobYamlHelpers';
import ToolPicker from 'components/ToolPicker.vue';

const $q = useQuasar();
const { slug } = useActiveWorkspace();

const jobs = ref<JobScriptResult[]>([]);
const recentRuns = ref<JobRunResult[]>([]);
const loading = ref(false);
const runsLoading = ref(false);
const dialogOpen = ref(false);
const editingId = ref<string | null>(null);
const submitting = ref(false);
const runningId = ref<string | null>(null);
const error = ref('');

interface FormState {
  slug: string;
  name: string;
  description: string;
  pattern: string;
  /** Raw frontmatter with the `allowed_commands:` block stripped out — that field is owned by the picker. */
  frontmatter: string;
  body: string;
  status: string;
  /** Source-of-truth for the `allowed_commands:` YAML block. Re-injected into the saved frontmatter. */
  allowedCommands: string[];
}

const form = reactive<FormState>(emptyForm());

const patternOptions = [
  { label: 'Deterministic', value: 'deterministic' },
  { label: 'Plan-then-execute', value: 'plan-then-execute', disable: true },
  { label: 'Agentic', value: 'agentic', disable: true },
];

const statusOptions = [
  { label: 'Draft', value: 'draft' },
  { label: 'Active', value: 'active' },
  { label: 'Disabled', value: 'disabled' },
];

const slugRules = [
  (v: string) => !!v?.trim() || 'Required',
  (v: string) =>
    /^[a-z0-9](?:[a-z0-9-]{1,62}[a-z0-9])?$/.test(v) ||
    'Lowercase letters, digits, hyphens. No leading or trailing hyphen.',
];

const cols: QTableColumn<JobScriptResult>[] = [
  { name: 'name', label: 'Name', field: 'name', align: 'left', sortable: true },
  { name: 'slug', label: 'Slug', field: 'slug', align: 'left' },
  { name: 'pattern', label: 'Pattern', field: 'pattern', align: 'left' },
  { name: 'status', label: 'Status', field: 'status', align: 'left' },
  { name: 'actions', label: '', field: () => '', align: 'right' },
];

const runCols: QTableColumn<JobRunResult>[] = [
  { name: 'startedAt', label: 'When', field: 'startedAt', align: 'left' },
  { name: 'jobScriptId', label: 'Job', field: 'jobScriptId', align: 'left',
    format: (v: unknown) => jobs.value.find((j) => j.jobScriptId === v)?.name ?? String(v).slice(0, 8) },
  { name: 'status', label: 'Status', field: 'status', align: 'left' },
  { name: 'durationMs', label: 'Duration', field: 'durationMs', align: 'right',
    format: (v: unknown) => `${typeof v === 'number' ? v : 0}ms` },
  { name: 'tokens', label: 'Tokens', field: 'totalTokensUsed', align: 'right' },
];

function statusColor(status: string): string {
  if (status === 'active') return 'positive';
  if (status === 'draft') return 'grey-7';
  if (status === 'disabled') return 'warning';
  return 'grey-7';
}

function runStatusColor(status: string): string {
  if (status === 'succeeded') return 'positive';
  if (status === 'failed') return 'negative';
  if (status === 'running') return 'primary';
  if (status === 'paused') return 'warning';
  return 'grey-7';
}

function formatRelative(when: string | null | undefined): string {
  if (!when) return '—';
  const d = new Date(when);
  if (Number.isNaN(d.getTime())) return '—';
  const diffMs = Date.now() - d.getTime();
  const diffMins = Math.round(diffMs / 60000);
  if (diffMins < 1) return 'Just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  const diffHours = Math.round(diffMins / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  return d.toLocaleDateString();
}

function emptyForm(): FormState {
  return {
    slug: '',
    name: '',
    description: '',
    pattern: 'deterministic',
    frontmatter: 'type: llm-chat\n',
    body: 'Write a haiku about reproducible builds.',
    status: 'active',
    allowedCommands: [],
  };
}


async function load() {
  if (!slug.value) return;
  loading.value = true;
  try {
    const res = await Jobs.listJobs({ path: { slug: slug.value } });
    jobs.value = res.data?.result ?? [];
  } finally {
    loading.value = false;
  }
  void loadRuns();
}

async function loadRuns() {
  if (!slug.value) return;
  runsLoading.value = true;
  try {
    const res = await Jobs.listWorkspaceRuns({ path: { slug: slug.value } });
    recentRuns.value = res.data?.result ?? [];
  } finally {
    runsLoading.value = false;
  }
}

function openCreate() {
  editingId.value = null;
  Object.assign(form, emptyForm());
  error.value = '';
  dialogOpen.value = true;
}

function openEdit(job: JobScriptResult) {
  editingId.value = job.jobScriptId;
  const split = splitAllowedCommands(job.frontmatter ?? '');
  Object.assign(form, {
    slug: job.slug,
    name: job.name,
    description: job.description ?? '',
    pattern: job.pattern,
    frontmatter: split.yaml,
    body: job.body,
    status: job.status,
    allowedCommands: split.commands,
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
    // Reassemble the full YAML by injecting the picker-managed
    // allowed_commands list back into the user-edited frontmatter. This is
    // the source-of-truth direction: picker → YAML on save.
    const fullFrontmatter = injectAllowedCommands(form.frontmatter, form.allowedCommands);
    if (editingId.value) {
      const res = await Jobs.updateJob({
        path: { slug: slug.value, jobId: editingId.value },
        body: {
          name: form.name,
          description: form.description || null,
          pattern: form.pattern,
          frontmatter: fullFrontmatter,
          body: form.body,
          status: form.status,
        },
      });
      if (res.error) {
        error.value = problemMessage(res.error) ?? 'Failed to save job.';
        return;
      }
    } else {
      const res = await Jobs.createJob({
        path: { slug: slug.value },
        body: {
          slug: form.slug,
          name: form.name,
          description: form.description || null,
          pattern: form.pattern,
          frontmatter: fullFrontmatter,
          body: form.body,
          status: form.status,
        },
      });
      if (res.error) {
        error.value = problemMessage(res.error) ?? 'Failed to create job.';
        return;
      }
    }
    closeDialog();
    void load();
  } finally {
    submitting.value = false;
  }
}

async function onRun(job: JobScriptResult) {
  if (!slug.value) return;
  runningId.value = job.jobScriptId;
  try {
    const res = await Jobs.runJob({
      path: { slug: slug.value, jobId: job.jobScriptId },
      body: { parameters: {} },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Run failed.',
      });
      return;
    }
    const result = res.data?.result;
    const ok = result?.status === 'succeeded';
    $q.notify({
      type: ok ? 'positive' : 'negative',
      position: 'top',
      message: ok
        ? `${job.name}: ${result?.totalTokensUsed ?? 0} tokens, ${result?.durationMs ?? 0}ms`
        : result?.failureMessage ?? 'Run failed.',
      timeout: 6000,
    });
    void loadRuns();
  } finally {
    runningId.value = null;
  }
}

function onDelete(job: JobScriptResult) {
  if (!slug.value) return;
  $q.dialog({
    title: 'Delete job?',
    message: `<p>Permanently delete <strong>${job.slug}</strong>? Run history is preserved.</p>`,
    html: true,
    ok: { label: 'Delete', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async () => {
    if (!slug.value) return;
    const res = await Jobs.deleteJob({
      path: { slug: slug.value, jobId: job.jobScriptId },
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
.cr-jobs-header {
  margin-bottom: 16px;
}

.cr-jobs-titlerow {
  display: flex;
  align-items: center;
  gap: 12px;
}

.cr-jobs-subhead {
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

.cr-jobs-tag {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  background: var(--cr-bg-elevated);
  color: var(--cr-fg-secondary);
  padding: 1px 6px;
  border-radius: 3px;
}

.cr-jobs-dialog {
  min-width: 640px;
  max-width: 90vw;
}

.cr-jobs-section-title {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  margin-top: 4px;
}

:deep(.cr-jobs-mono) {
  font-family: var(--cr-font-family-mono);
  font-size: 12px;
  min-height: 80px;
}
</style>
