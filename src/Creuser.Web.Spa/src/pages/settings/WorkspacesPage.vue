<template>
  <q-page class="q-pa-lg">
    <header class="cr-ws-header">
      <h1 class="text-h5 q-ma-none">Workspaces</h1>
      <p class="cr-ws-subhead">
        Configured repository connections. Each workspace is an operational target for jobs, agents,
        and dashboards. v1 supports git repos and local filesystem paths — S3 lands later. Sync
        clones (first time) or fetches + resets to the working branch.
      </p>
    </header>

    <div class="cr-ws-actions">
      <q-btn
        color="primary"
        unelevated
        no-caps
        icon="add"
        label="Connect workspace"
        @click="openCreate"
      />
    </div>

    <q-table
      :rows="workspaces"
      :columns="cols"
      row-key="workspaceId"
      :loading="loading"
      flat
      bordered
      dense
    >
      <template #body-cell-slug="props">
        <q-td :props="props">
          <code class="cr-ws-slug">{{ props.row.slug }}</code>
        </q-td>
      </template>

      <template #body-cell-type="props">
        <q-td :props="props">
          <q-chip
            dense
            outline
            :color="props.row.type === 'git' ? 'primary' : 'grey-7'"
            :text-color="props.row.type === 'git' ? 'primary' : 'grey-7'"
          >
            {{ props.row.type }}
          </q-chip>
        </q-td>
      </template>

      <template #body-cell-repository="props">
        <q-td :props="props" class="cr-ws-repo-cell">
          <span v-if="props.row.gitSettings" class="cr-ws-repo">
            {{ props.row.gitSettings.repositoryUrl }}
          </span>
          <span v-else-if="props.row.localSettings" class="cr-ws-repo">
            {{ props.row.localSettings.path }}
            <q-chip
              v-if="!props.row.localSettings.writable"
              dense
              outline
              size="xs"
              color="grey-7"
              text-color="grey-7"
              class="q-ml-xs"
            >
              read-only
            </q-chip>
          </span>
          <span v-else class="cr-ws-empty">—</span>
        </q-td>
      </template>

      <template #body-cell-lastSync="props">
        <q-td :props="props" class="cr-ws-sync-cell">
          <span v-if="!props.row.lastSyncAt" class="cr-ws-empty">Never</span>
          <span v-else class="cr-ws-sync-stack">
            <span class="cr-ws-sync-row">
              <q-icon
                :name="props.row.lastSyncStatus === 'ok' ? 'check_circle' : 'error'"
                :color="props.row.lastSyncStatus === 'ok' ? 'positive' : 'negative'"
                size="14px"
              />
              <span class="cr-ws-sync-when">{{ formatRelative(props.row.lastSyncAt) }}</span>
              <code v-if="props.row.lastSyncSha" class="cr-ws-sync-sha">
                {{ props.row.lastSyncSha.slice(0, 7) }}
              </code>
              <q-tooltip v-if="props.row.lastSyncMessage" anchor="top middle" self="bottom middle">
                {{ props.row.lastSyncMessage }}
              </q-tooltip>
            </span>
          </span>
        </q-td>
      </template>

      <template #body-cell-actions="props">
        <q-td :props="props" auto-width>
          <q-btn
            flat
            dense
            round
            :icon="props.row.type === 'local' ? 'fact_check' : 'sync'"
            size="sm"
            :loading="syncingSlug === props.row.slug"
            :aria-label="`${props.row.type === 'local' ? 'Verify path for' : 'Sync'} ${props.row.slug}`"
            @click="onSync(props.row)"
          >
            <q-tooltip>{{ props.row.type === 'local' ? 'Verify path' : 'Sync now' }}</q-tooltip>
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

    <!-- Create / Edit dialog -->
    <q-dialog v-model="dialogOpen" persistent>
      <q-card class="cr-ws-dialog">
        <q-card-section>
          <div class="text-h6">{{ editingSlug ? 'Edit workspace' : 'Connect workspace' }}</div>
          <div class="text-caption" :style="{ color: 'var(--cr-fg-secondary)' }">
            {{
              editingSlug
                ? 'Update the connection settings. Slug is fixed once created.'
                : 'Add a new git repository the platform can read from and commit to.'
            }}
          </div>
        </q-card-section>

        <q-card-section>
          <q-form class="q-gutter-md" @submit.prevent="onSubmit">
            <q-input
              v-model="form.slug"
              label="Slug"
              hint="URL-safe identifier (kebab-case). Used in /w/:slug/... routes."
              dense
              outlined
              :readonly="!!editingSlug"
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

            <div class="cr-ws-section-title">Provider type</div>
            <q-btn-toggle
              v-model="form.type"
              :options="typeOptions"
              unelevated
              no-caps
              toggle-color="primary"
              class="cr-ws-toggle"
            />

            <template v-if="form.type === 'git'">
              <div class="cr-ws-section-title">Git connection</div>
              <q-input
                v-model="form.repositoryUrl"
                label="Repository URL"
                placeholder="https://github.com/org/repo.git"
                hint="HTTPS or SSH (git@host:org/repo.git)."
                dense
                outlined
              />

              <div class="cr-ws-auth-row">
                <span class="cr-ws-auth-label">Authentication</span>
                <q-chip
                  v-if="editingSlug && currentWorkspace?.authSecretPresent"
                  dense
                  outline
                  color="positive"
                  text-color="positive"
                  class="cr-ws-auth-chip"
                >
                  Credential set
                </q-chip>
                <q-chip
                  v-else-if="editingSlug && form.authMode !== 'none'"
                  dense
                  outline
                  color="grey-6"
                  text-color="grey-6"
                  class="cr-ws-auth-chip"
                >
                  Optional · Not set
                </q-chip>
              </div>
              <q-btn-toggle
                v-model="form.authMode"
                :options="authModeOptions"
                unelevated
                no-caps
                toggle-color="primary"
                class="cr-ws-toggle"
              />

              <q-input
                v-if="form.authMode === 'https-pat'"
                v-model="form.authCredential"
                :type="showSecret ? 'text' : 'password'"
                label="Personal Access Token"
                :hint="
                  editingSlug && currentWorkspace?.authSecretPresent
                    ? 'Leave blank to keep the existing token. Type a new one to rotate.'
                    : 'Paste your PAT. Stored at /data/secrets/workspace-' +
                      form.slug +
                      '.pat (chmod 600).'
                "
                dense
                outlined
                autocomplete="off"
              >
                <template #append>
                  <q-icon
                    :name="showSecret ? 'visibility_off' : 'visibility'"
                    class="cursor-pointer"
                    @click="showSecret = !showSecret"
                  />
                </template>
              </q-input>

              <q-input
                v-if="form.authMode === 'ssh-key'"
                v-model="form.authCredential"
                type="textarea"
                label="OpenSSH private key"
                placeholder="-----BEGIN OPENSSH PRIVATE KEY-----&#10;...&#10;-----END OPENSSH PRIVATE KEY-----"
                :hint="
                  editingSlug && currentWorkspace?.authSecretPresent
                    ? 'Leave blank to keep the existing key. Paste a new one to rotate.'
                    : 'Paste the full key. Stored at /data/secrets/workspace-' +
                      form.slug +
                      '.key (chmod 600). Generate-keypair flow lands next pass.'
                "
                dense
                outlined
                autogrow
                input-class="cr-ws-key-input"
                autocomplete="off"
              />

              <div class="cr-ws-section-title">Branching</div>
              <q-input
                v-model="form.workingBranch"
                label="Working branch"
                hint="Branch the platform commits to. Default: creuser/main"
                dense
                outlined
              />
              <q-input
                v-model="form.sourceBranch"
                label="Source branch"
                hint="Branch to sync content from."
                dense
                outlined
              />
              <q-select
                v-model="form.mode"
                :options="modeOptions"
                label="Mode"
                dense
                outlined
                emit-value
                map-options
              />
              <q-select
                v-model="form.pushFrequency"
                :options="pushFrequencyOptions"
                label="Push frequency"
                dense
                outlined
                emit-value
                map-options
              />
            </template>

            <template v-if="form.type === 'local'">
              <div class="cr-ws-section-title">Local path</div>
              <p class="cr-ws-local-hint">
                A filesystem path the Creuser process can read (and optionally write). In Docker
                deployments, this is typically a mounted volume (<code>/workspaces/myrepo</code>);
                in dev / on-host runs it can be any directory. No checkout / commits / branches —
                writes go directly to disk.
              </p>
              <q-input
                v-model="form.localPath"
                label="Path"
                placeholder="/workspaces/myrepo"
                hint="Absolute path. Must exist when you save."
                dense
                outlined
              />
              <q-toggle
                v-model="form.localWritable"
                label="Allow writes (uncheck for read-only mounts)"
                color="primary"
              />
            </template>

            <StatusBanner
              v-if="testResult"
              :variant="testResult.ok ? 'success' : 'error'"
              :title="testResult.ok ? 'Connected' : 'Test failed'"
              dismissable
              @dismiss="testResult = null"
            >
              <span v-if="testResult.ok">· {{ testSuccessDetail }}</span>
              <span v-else-if="testResult.error">· {{ testResult.error }}</span>
            </StatusBanner>

            <div v-if="error" class="text-negative text-caption">{{ error }}</div>

            <div class="row justify-between items-center q-gutter-sm">
              <q-btn
                flat
                no-caps
                icon="check_circle_outline"
                label="Test connection"
                :loading="testing"
                :disable="!canTest || submitting"
                @click="onTest"
              />
              <div class="row q-gutter-sm">
                <q-btn flat label="Cancel" no-caps @click="closeDialog" />
                <q-btn
                  type="submit"
                  :label="editingSlug ? 'Save' : 'Connect'"
                  color="primary"
                  unelevated
                  no-caps
                  :loading="submitting"
                />
              </div>
            </div>
          </q-form>
        </q-card-section>
      </q-card>
    </q-dialog>
  </q-page>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { useQuasar, type QTableColumn } from 'quasar';
import { Workspaces, type WorkspaceConnectionTestResult, type WorkspaceResult } from 'src/api';
import StatusBanner from 'components/StatusBanner.vue';

const $q = useQuasar();

interface FormState {
  slug: string;
  name: string;
  description: string;
  /** One of `git`, `local`, `s3`. v1 ships `git` and `local`. */
  type: string;
  // Git fields
  repositoryUrl: string;
  authMode: string;
  authCredential: string;
  workingBranch: string;
  sourceBranch: string;
  mode: string;
  pushFrequency: string;
  // Local fields
  localPath: string;
  localWritable: boolean;
}

const workspaces = ref<WorkspaceResult[]>([]);
const loading = ref(false);
const dialogOpen = ref(false);
const editingSlug = ref<string | null>(null);
const submitting = ref(false);
const testing = ref(false);
const showSecret = ref(false);
const testResult = ref<WorkspaceConnectionTestResult | null>(null);
const error = ref('');
const syncingSlug = ref<string | null>(null);

const form = reactive<FormState>(emptyForm());

// The workspace currently being edited (if any). Drives the "credential set"
// chip and the "leave blank to keep existing" hint copy on the credential
// input. Recomputed from the source list whenever the dialog opens.
const currentWorkspace = computed<WorkspaceResult | null>(() =>
  editingSlug.value ? (workspaces.value.find((w) => w.slug === editingSlug.value) ?? null) : null,
);

// Whether the form has enough type-specific input to attempt a test.
// Git needs a repository URL; local needs a path.
const canTest = computed(() => {
  if (form.type === 'git') return form.repositoryUrl.trim().length > 0;
  if (form.type === 'local') return form.localPath.trim().length > 0;
  return false;
});

// Type-aware success copy for the test-result banner. Git includes the
// round-trip latency (network call); local reports access semantics
// (latencyMs would always be 0 since there's no network involved).
const testSuccessDetail = computed(() => {
  if (form.type === 'git')
    return `git smart-HTTP responded in ${testResult.value?.latencyMs ?? 0}ms`;
  if (form.type === 'local')
    return form.localWritable
      ? 'Path is accessible (read-write).'
      : 'Path is accessible (read-only).';
  return 'Connection succeeded.';
});

const cols: QTableColumn<WorkspaceResult>[] = [
  { name: 'name', label: 'Name', field: 'name', align: 'left', sortable: true },
  { name: 'slug', label: 'Slug', field: 'slug', align: 'left', sortable: true },
  { name: 'type', label: 'Type', field: 'type', align: 'left' },
  { name: 'repository', label: 'Repository', field: 'gitSettings', align: 'left' },
  {
    name: 'lastSync',
    label: 'Last sync',
    field: 'lastSyncAt',
    align: 'left',
    sortable: true,
  },
  { name: 'actions', label: '', field: () => '', align: 'right' },
];

const modeOptions = [
  { label: 'Direct push (no PR review)', value: 'direct-push' },
  { label: 'Pull request', value: 'pull-request' },
];

const pushFrequencyOptions = [
  { label: 'Every commit (real-time)', value: 'every-commit' },
  { label: 'Batched', value: 'batched' },
];

const authModeOptions = [
  { label: 'None (public repo)', value: 'none' },
  { label: 'HTTPS PAT', value: 'https-pat' },
  { label: 'SSH key', value: 'ssh-key' },
];

// Provider types — `git` and `local` are fully implemented; `s3` is
// reserved. The disabled S3 option keeps the planned breadth visible
// without exposing a half-built backend.
const typeOptions = [
  { label: 'Git', value: 'git' },
  { label: 'Local path', value: 'local' },
  { label: 'S3 (coming soon)', value: 's3', disable: true },
];

const slugRules = [
  (v: string) => !!v?.trim() || 'Required',
  (v: string) =>
    /^[a-z0-9](?:[a-z0-9-]{1,62}[a-z0-9])?$/.test(v) ||
    'Lowercase letters, digits, hyphens. No leading or trailing hyphen.',
];

function emptyForm(): FormState {
  return {
    slug: '',
    name: '',
    description: '',
    type: 'git',
    repositoryUrl: '',
    authMode: 'none',
    authCredential: '',
    workingBranch: 'creuser/main',
    sourceBranch: 'main',
    mode: 'direct-push',
    pushFrequency: 'every-commit',
    localPath: '',
    localWritable: true,
  };
}

async function load() {
  loading.value = true;
  try {
    const res = await Workspaces.listWorkspaces();
    workspaces.value = res.data?.result ?? [];
  } finally {
    loading.value = false;
  }
}

function openCreate() {
  editingSlug.value = null;
  Object.assign(form, emptyForm());
  error.value = '';
  testResult.value = null;
  showSecret.value = false;
  dialogOpen.value = true;
}

function openEdit(ws: WorkspaceResult) {
  editingSlug.value = ws.slug;
  Object.assign(form, {
    slug: ws.slug,
    name: ws.name,
    description: ws.description ?? '',
    type: ws.type,
    // git fields (populated when ws.type === 'git', otherwise defaults)
    repositoryUrl: ws.gitSettings?.repositoryUrl ?? '',
    authMode: ws.gitSettings?.authMode ?? 'none',
    authCredential: '',
    workingBranch: ws.gitSettings?.workingBranch ?? 'creuser/main',
    sourceBranch: ws.gitSettings?.sourceBranch ?? 'main',
    mode: ws.gitSettings?.mode ?? 'direct-push',
    pushFrequency: ws.gitSettings?.pushFrequency ?? 'every-commit',
    // local fields (populated when ws.type === 'local', otherwise defaults)
    localPath: ws.localSettings?.path ?? '',
    localWritable: ws.localSettings?.writable ?? true,
  });
  error.value = '';
  testResult.value = null;
  showSecret.value = false;
  dialogOpen.value = true;
}

function closeDialog() {
  dialogOpen.value = false;
  error.value = '';
  testResult.value = null;
}

function buildGitSettings() {
  // The credential is sent inline only when the admin provides a fresh
  // value; on edit-without-rotation the server keeps the existing secret.
  return {
    repositoryUrl: form.repositoryUrl,
    authMode: form.authMode,
    authSecret: null,
    authCredential: form.authCredential.trim() === '' ? null : form.authCredential,
    workingBranch: form.workingBranch,
    sourceBranch: form.sourceBranch,
    mode: form.mode,
    pushFrequency: form.pushFrequency,
  };
}

function buildLocalSettings() {
  return {
    path: form.localPath.trim(),
    writable: form.localWritable,
  };
}

/** Type-aware payload builder used for create/update/test bodies. */
function buildTypedSettings() {
  return {
    type: form.type,
    gitSettings: form.type === 'git' ? buildGitSettings() : null,
    localSettings: form.type === 'local' ? buildLocalSettings() : null,
  };
}

async function onTest() {
  testResult.value = null;
  testing.value = true;
  try {
    const res = await Workspaces.testWorkspaceConnection({
      body: buildTypedSettings(),
    });
    if (res.error) {
      testResult.value = {
        ok: false,
        latencyMs: 0,
        error: problemMessage(res.error) ?? 'Test failed.',
      };
      return;
    }
    testResult.value = res.data?.result ?? null;
  } finally {
    testing.value = false;
  }
}

async function onSubmit() {
  error.value = '';
  submitting.value = true;
  try {
    const typedSettings = buildTypedSettings();

    if (editingSlug.value) {
      const res = await Workspaces.updateWorkspace({
        path: { slug: editingSlug.value },
        body: {
          name: form.name,
          description: form.description || null,
          ...typedSettings,
        },
      });
      if (res.error) {
        error.value = problemMessage(res.error) ?? 'Failed to save workspace.';
        return;
      }
      $q.notify({
        type: 'positive',
        position: 'top',
        message: `Workspace ${form.slug} updated.`,
      });
    } else {
      const res = await Workspaces.createWorkspace({
        body: {
          slug: form.slug,
          name: form.name,
          description: form.description || null,
          ...typedSettings,
        },
      });
      if (res.error) {
        error.value = problemMessage(res.error) ?? 'Failed to create workspace.';
        return;
      }
      $q.notify({
        type: 'positive',
        position: 'top',
        message: `Workspace ${form.slug} connected.`,
      });
    }

    closeDialog();
    void load();
  } finally {
    submitting.value = false;
  }
}

async function onSync(ws: WorkspaceResult, force = false) {
  syncingSlug.value = ws.slug;
  try {
    const res = await Workspaces.syncWorkspace({
      path: { slug: ws.slug },
      query: force ? { force: true } : undefined,
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Sync failed.',
      });
      return;
    }
    const result = res.data?.result;

    // Server refused because the working tree had uncommitted changes.
    // Pop a confirmation dialog and retry with force=true if the admin
    // says yes.
    if (result?.requiresForce) {
      const dirty = Number(result.dirtyCount ?? 0) || 0;
      $q.dialog({
        title: 'Discard local changes?',
        message:
          `<p>The working tree has <strong>${dirty}</strong> uncommitted ` +
          `change${dirty === 1 ? '' : 's'} (modified, added, or untracked files). ` +
          `Sync will reset the directory to match the remote — those changes will be lost.</p>`,
        html: true,
        ok: { label: 'Discard & sync', color: 'negative', unelevated: true, noCaps: true },
        cancel: { flat: true, noCaps: true },
        persistent: true,
        // eslint-disable-next-line @typescript-eslint/no-misused-promises
      }).onOk(async () => {
        await onSync(ws, true);
      });
      return;
    }

    if (result?.ok) {
      const fallback =
        ws.type === 'local' ? `Path verified for ${ws.slug}.` : `Workspace ${ws.slug} synced.`;
      $q.notify({
        type: 'positive',
        position: 'top',
        message: result.message ?? fallback,
      });
    } else {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: result?.error ?? 'Sync failed.',
        timeout: 8000,
      });
    }
    void load();
  } finally {
    syncingSlug.value = null;
  }
}

/**
 * Short relative format for the "Last sync" column. Falls back to the
 * locale date once we're past 24h — "3 days ago" style copy is more noise
 * than signal for ops dashboards.
 */
function formatRelative(when: string | null | undefined): string {
  if (!when) return 'Never';
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

function onDelete(ws: WorkspaceResult) {
  $q.dialog({
    title: 'Delete workspace?',
    message:
      `<p>Disconnect <strong>${ws.slug}</strong>? Removes the platform's reference to this repo. ` +
      `Future feature: prompt before deleting if there are workflows / dashboards bound to it.</p>`,
    html: true,
    ok: { label: 'Delete', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async () => {
    try {
      const res = await Workspaces.deleteWorkspace({ path: { slug: ws.slug } });
      if (res.error) {
        $q.notify({
          type: 'negative',
          position: 'top',
          message: problemMessage(res.error) ?? 'Failed to delete workspace.',
        });
        return;
      }
      $q.notify({ type: 'positive', position: 'top', message: `Workspace ${ws.slug} deleted.` });
      void load();
    } catch (e) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: e instanceof Error ? e.message : 'Failed to delete workspace.',
      });
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

onMounted(() => void load());
</script>

<style lang="scss" scoped>
.cr-ws-header {
  margin-bottom: 16px;
}

.cr-ws-subhead {
  margin: 8px 0 0;
  font-size: 13px;
  color: var(--cr-fg-secondary);
  max-width: 720px;
}

.cr-ws-actions {
  margin-bottom: 16px;
}

.cr-ws-slug {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  background: var(--cr-bg-elevated);
  padding: 1px 6px;
  border-radius: 3px;
}

.cr-ws-repo-cell {
  max-width: 320px;
}

.cr-ws-repo {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  color: var(--cr-fg-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  display: inline-block;
  max-width: 100%;
}

.cr-ws-empty {
  color: var(--cr-fg-tertiary);
}

.cr-ws-sync-cell {
  white-space: nowrap;
}

.cr-ws-sync-stack {
  display: inline-flex;
  align-items: center;
}

.cr-ws-sync-row {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.cr-ws-sync-when {
  font-size: 12px;
  color: var(--cr-fg-secondary);
}

.cr-ws-sync-sha {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  background: var(--cr-bg-elevated);
  padding: 1px 5px;
  border-radius: 3px;
  color: var(--cr-fg-secondary);
}

.cr-ws-dialog {
  min-width: 520px;
}

.cr-ws-section-title {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  margin-top: 8px;
}

.cr-ws-auth-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: -4px;
}

.cr-ws-auth-label {
  font-size: 12px;
  color: var(--cr-fg-secondary);
  font-weight: 500;
}

.cr-ws-auth-chip {
  font-size: 10px;
}

.cr-ws-toggle {
  align-self: flex-start;
}

:deep(.cr-ws-key-input) {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  min-height: 100px;
}

.cr-ws-local-hint {
  font-size: 12px;
  color: var(--cr-fg-secondary);
  margin: 0;
  line-height: 1.5;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}
</style>
