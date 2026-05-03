<template>
  <q-page class="q-pa-lg cr-home">
    <header class="cr-home-header">
      <h1 class="text-h5 q-ma-none">Welcome{{ greeting }}</h1>
      <p class="cr-home-subhead">
        Pick a workspace to enter. Each workspace is a connected content source —
        a git repository or local filesystem path — that the platform reads from
        and writes back to.
      </p>
    </header>

    <div v-if="loading" class="cr-home-loading">
      <q-spinner size="md" />
      <span>Loading workspaces…</span>
    </div>

    <div v-else-if="error" class="cr-home-error">
      <q-icon name="error_outline" size="20px" />
      <span>{{ error }}</span>
    </div>

    <div v-else-if="workspaces.length === 0" class="cr-home-empty">
      <q-icon name="folder_open" size="48px" class="cr-home-empty-icon" />
      <h2 class="text-h6 q-ma-none">0 workspaces available</h2>
      <p v-if="auth.isAdmin" class="cr-home-empty-copy">
        No workspaces have been created on this Creuser instance yet. Get started in
        <router-link to="/settings/workspaces" class="cr-home-link">
          Platform Settings → Workspaces </router-link
        >.
      </p>
      <p v-else class="cr-home-empty-copy">
        You don't have access to any workspaces yet. Ask your administrator to grant
        you access.
      </p>
    </div>

    <div v-else class="cr-home-grid">
      <button
        v-for="ws in workspaces"
        :key="ws.workspaceId"
        type="button"
        class="cr-ws-card"
        :aria-label="`Open ${ws.name}`"
        @click="onPick(ws)"
      >
        <span class="cr-ws-card-head">
          <q-icon :name="iconForType(ws.type)" size="18px" />
          <span class="cr-ws-card-type">{{ ws.type }}</span>
          <span v-if="ws.lastSyncStatus === 'ok'" class="cr-ws-card-sync cr-ws-card-sync-ok">
            <q-icon name="check_circle" size="12px" />
            synced
          </span>
          <span
            v-else-if="ws.lastSyncStatus === 'failed'"
            class="cr-ws-card-sync cr-ws-card-sync-failed"
          >
            <q-icon name="error" size="12px" />
            sync failed
          </span>
          <span v-else class="cr-ws-card-sync cr-ws-card-sync-never">never synced</span>
        </span>
        <h3 class="cr-ws-card-name">{{ ws.name }}</h3>
        <code class="cr-ws-card-slug">{{ ws.slug }}</code>
        <p v-if="ws.description" class="cr-ws-card-desc">{{ ws.description }}</p>
        <span v-if="locationFor(ws)" class="cr-ws-card-loc">{{ locationFor(ws) }}</span>
      </button>
    </div>

    <p v-if="!loading && workspaces.length > 0" class="cr-home-foot">
      Workspace-scoped navigation
      <code>(/w/:slug/...)</code> lands in the next pass — picking a workspace today
      drops you into a placeholder.
    </p>
  </q-page>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useQuasar } from 'quasar';
import { Workspaces, type WorkspaceResult } from 'src/api';
import { useAuthStore } from 'stores/auth';

const $q = useQuasar();
const auth = useAuthStore();

const workspaces = ref<WorkspaceResult[]>([]);
const loading = ref(false);
const error = ref<string>('');

const greeting = computed(() => {
  const name = auth.user?.displayName?.trim();
  return name ? `, ${name}` : '';
});

function iconForType(type: string): string {
  if (type === 'git') return 'cloud';
  if (type === 'local') return 'folder';
  if (type === 's3') return 'cloud_queue';
  return 'source';
}

function locationFor(ws: WorkspaceResult): string | null {
  if (ws.gitSettings?.repositoryUrl) return ws.gitSettings.repositoryUrl;
  if (ws.localSettings?.path) return ws.localSettings.path;
  return null;
}

async function load() {
  loading.value = true;
  error.value = '';
  try {
    const res = await Workspaces.listWorkspaces();
    if (res.error) {
      // Non-admins can't list workspaces yet (the endpoint is admin-only
      // pending cr.workspace_members). Treat the 403 as "0 visible to you"
      // rather than surfacing an error — the empty state messaging handles it.
      const status = res.response?.status;
      if (status === 403) {
        workspaces.value = [];
        return;
      }
      error.value = problemMessage(res.error) ?? 'Could not load workspaces.';
      return;
    }
    workspaces.value = res.data?.result ?? [];
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Could not load workspaces.';
  } finally {
    loading.value = false;
  }
}

function onPick(ws: WorkspaceResult) {
  // Workspace-scoped routing (`/w/:slug/...`) lands next pass. For now,
  // surface a deliberate notification so the affordance reads as "real
  // button, deferred destination" rather than dead.
  $q.notify({
    type: 'info',
    position: 'top',
    message: `Workspace "${ws.slug}" — workspace dashboards coming soon.`,
    timeout: 4000,
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
.cr-home {
  max-width: 1100px;
}

.cr-home-header {
  margin-bottom: 24px;
}

.cr-home-subhead {
  margin: 8px 0 0;
  font-size: 13px;
  color: var(--cr-fg-secondary);
  max-width: 720px;
  line-height: 1.5;
}

.cr-home-loading,
.cr-home-error {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--cr-fg-secondary);
  font-size: 13px;
}

.cr-home-error {
  color: var(--q-negative);
}

.cr-home-empty {
  margin-top: 32px;
  padding: 32px 24px;
  border: 1px dashed var(--cr-border-default);
  border-radius: 6px;
  background: var(--cr-bg-elevated);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  max-width: 560px;
}

.cr-home-empty-icon {
  color: var(--cr-fg-tertiary);
}

.cr-home-empty-copy {
  margin: 0;
  text-align: center;
  font-size: 13px;
  color: var(--cr-fg-secondary);
  line-height: 1.5;
}

.cr-home-link {
  color: var(--cr-link);
  text-decoration: none;
  font-weight: 500;

  &:hover {
    color: var(--cr-link-hover);
    text-decoration: underline;
  }
}

.cr-home-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 12px;
  margin-top: 8px;
}

.cr-ws-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 16px;
  text-align: left;
  background: var(--cr-bg-surface);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 6px;
  cursor: pointer;
  transition:
    border-color 120ms ease,
    background 120ms ease,
    transform 80ms ease;
  font-family: inherit;
  color: inherit;

  &:hover {
    border-color: var(--cr-border-default);
    background: var(--cr-bg-hover);
  }

  &:focus-visible {
    outline: 2px solid var(--q-primary);
    outline-offset: 2px;
  }

  &:active {
    transform: translateY(1px);
  }
}

.cr-ws-card-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--cr-fg-tertiary);
}

.cr-ws-card-type {
  text-transform: uppercase;
  letter-spacing: 0.08em;
  font-weight: 600;
}

.cr-ws-card-sync {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 10px;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.cr-ws-card-sync-ok {
  color: var(--q-positive);
}

.cr-ws-card-sync-failed {
  color: var(--q-negative);
}

.cr-ws-card-sync-never {
  color: var(--cr-fg-tertiary);
}

.cr-ws-card-name {
  margin: 4px 0 0;
  font-size: 15px;
  font-weight: 600;
  color: var(--cr-fg-primary);
}

.cr-ws-card-slug {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  color: var(--cr-fg-secondary);
  background: var(--cr-bg-elevated);
  padding: 1px 6px;
  border-radius: 3px;
  align-self: flex-start;
}

.cr-ws-card-desc {
  margin: 0;
  font-size: 12px;
  color: var(--cr-fg-secondary);
  line-height: 1.4;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.cr-ws-card-loc {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  color: var(--cr-fg-tertiary);
  margin-top: 4px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.cr-home-foot {
  margin-top: 32px;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
  font-style: italic;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
    font-style: normal;
  }
}
</style>
