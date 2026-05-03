<template>
  <q-page class="q-pa-lg cr-wshome">
    <header class="cr-wshome-header">
      <div class="cr-wshome-titlerow">
        <q-icon :name="iconForType(workspace?.type ?? '')" size="22px" />
        <h1 class="text-h5 q-ma-none">{{ workspace?.name ?? slug }}</h1>
        <q-chip
          v-if="workspace"
          dense
          outline
          :color="workspace.type === 'git' ? 'primary' : 'grey-7'"
          :text-color="workspace.type === 'git' ? 'primary' : 'grey-7'"
          class="cr-wshome-typechip"
        >
          {{ workspace.type }}
        </q-chip>
      </div>
      <p v-if="workspace?.description" class="cr-wshome-desc">
        {{ workspace.description }}
      </p>
    </header>

    <div class="cr-wshome-cards">
      <section class="cr-wshome-card">
        <h2 class="cr-wshome-card-title">Sync</h2>
        <div v-if="!workspace?.lastSyncAt" class="cr-wshome-meta">
          <q-icon name="schedule" size="14px" />
          <span>Never synced</span>
        </div>
        <div v-else class="cr-wshome-meta">
          <q-icon
            :name="workspace.lastSyncStatus === 'ok' ? 'check_circle' : 'error'"
            :color="workspace.lastSyncStatus === 'ok' ? 'positive' : 'negative'"
            size="14px"
          />
          <span>{{ formatRelative(workspace.lastSyncAt) }}</span>
          <code v-if="workspace.lastSyncSha" class="cr-wshome-sha">
            {{ workspace.lastSyncSha.slice(0, 7) }}
          </code>
        </div>
        <p v-if="workspace?.lastSyncMessage" class="cr-wshome-card-message">
          {{ workspace.lastSyncMessage }}
        </p>
      </section>

      <section class="cr-wshome-card">
        <h2 class="cr-wshome-card-title">Source</h2>
        <code v-if="locationLabel" class="cr-wshome-loc">{{ locationLabel }}</code>
        <p v-else class="cr-wshome-card-message">No source configured.</p>
      </section>

      <section v-if="workspace?.gitSettings" class="cr-wshome-card">
        <h2 class="cr-wshome-card-title">Branches</h2>
        <div class="cr-wshome-branches">
          <span class="cr-wshome-branch-label">Working</span>
          <code class="cr-wshome-branch">{{ workspace.gitSettings.workingBranch }}</code>
          <span class="cr-wshome-branch-label">Source</span>
          <code class="cr-wshome-branch">{{ workspace.gitSettings.sourceBranch }}</code>
        </div>
      </section>
    </div>

    <div class="cr-wshome-coming">
      <q-icon name="construction" size="18px" class="cr-wshome-coming-icon" />
      <span>
        Workspace dashboards land in the next pass — the icon bar will populate with Operations,
        plus any standalone dashboards or groups admins create.
      </span>
    </div>
  </q-page>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';

const { slug, workspace } = useActiveWorkspace();

function iconForType(type: string): string {
  if (type === 'git') return 'cloud';
  if (type === 'local') return 'folder';
  if (type === 's3') return 'cloud_queue';
  return 'source';
}

const locationLabel = computed(() => {
  if (workspace.value?.gitSettings?.repositoryUrl) return workspace.value.gitSettings.repositoryUrl;
  if (workspace.value?.localSettings?.path) return workspace.value.localSettings.path;
  return null;
});

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
</script>

<style lang="scss" scoped>
.cr-wshome {
  max-width: 1100px;
}

.cr-wshome-header {
  margin-bottom: 24px;
}

.cr-wshome-titlerow {
  display: flex;
  align-items: center;
  gap: 10px;
}

.cr-wshome-typechip {
  font-size: 10px;
  letter-spacing: 0.06em;
}

.cr-wshome-desc {
  margin: 6px 0 0;
  font-size: 13px;
  color: var(--cr-fg-secondary);
  max-width: 720px;
  line-height: 1.5;
}

.cr-wshome-cards {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
  gap: 12px;
}

.cr-wshome-card {
  padding: 14px 16px;
  background: var(--cr-bg-surface);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 6px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.cr-wshome-card-title {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  margin: 0;
}

.cr-wshome-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: var(--cr-fg-secondary);
}

.cr-wshome-sha {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  background: var(--cr-bg-elevated);
  padding: 1px 5px;
  border-radius: 3px;
  color: var(--cr-fg-secondary);
}

.cr-wshome-card-message {
  margin: 0;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
}

.cr-wshome-loc {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  color: var(--cr-fg-secondary);
  word-break: break-all;
}

.cr-wshome-branches {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 4px 8px;
  align-items: center;
}

.cr-wshome-branch-label {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.cr-wshome-branch {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  background: var(--cr-bg-elevated);
  padding: 1px 6px;
  border-radius: 3px;
  color: var(--cr-fg-secondary);
}

.cr-wshome-coming {
  margin-top: 32px;
  padding: 14px 16px;
  border: 1px dashed var(--cr-border-subtle);
  border-radius: 6px;
  background: var(--cr-bg-elevated);
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 12px;
  color: var(--cr-fg-secondary);
  line-height: 1.5;
}

.cr-wshome-coming-icon {
  color: var(--cr-fg-tertiary);
  flex-shrink: 0;
}
</style>
