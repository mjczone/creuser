<template>
  <div class="q-pa-lg">
    <header class="cr-wsgen-header">
      <h1 class="text-h5 q-ma-none">General</h1>
      <p class="cr-wsgen-subhead">
        Workspace name, description, and sync cadence. Editing this from inside the workspace lands
        soon — for now, edit via
        <router-link to="/settings/workspaces" class="cr-wsgen-link">
          Platform Settings → Workspaces </router-link
        >.
      </p>
    </header>

    <div v-if="workspace" class="cr-wsgen-readonly">
      <dl class="cr-wsgen-grid">
        <dt>Slug</dt>
        <dd>
          <code>{{ workspace.slug }}</code>
        </dd>
        <dt>Name</dt>
        <dd>{{ workspace.name }}</dd>
        <dt>Description</dt>
        <dd>{{ workspace.description || '—' }}</dd>
        <dt>Type</dt>
        <dd>{{ workspace.type }}</dd>
        <dt>Created</dt>
        <dd>{{ formatDate(workspace.createdAt) }}</dd>
      </dl>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';

const { workspace } = useActiveWorkspace();

function formatDate(when: string | null | undefined): string {
  if (!when) return '—';
  const d = new Date(when);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleString();
}
</script>

<style lang="scss" scoped>
.cr-wsgen-header {
  margin-bottom: 16px;
}

.cr-wsgen-subhead {
  margin: 8px 0 0;
  font-size: 13px;
  color: var(--cr-fg-secondary);
  max-width: 720px;
  line-height: 1.5;
}

.cr-wsgen-link {
  color: var(--cr-link);
  text-decoration: none;
  font-weight: 500;

  &:hover {
    color: var(--cr-link-hover);
    text-decoration: underline;
  }
}

.cr-wsgen-readonly {
  margin-top: 16px;
  padding: 16px 20px;
  background: var(--cr-bg-surface);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 6px;
  max-width: 640px;
}

.cr-wsgen-grid {
  display: grid;
  grid-template-columns: 140px 1fr;
  gap: 8px 16px;
  margin: 0;
  font-size: 13px;

  dt {
    color: var(--cr-fg-tertiary);
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    padding-top: 2px;
  }

  dd {
    color: var(--cr-fg-primary);
    margin: 0;

    code {
      font-family: var(--cr-font-family-mono);
      font-size: 11px;
      background: var(--cr-bg-elevated);
      padding: 1px 6px;
      border-radius: 3px;
      color: var(--cr-fg-secondary);
    }
  }
}
</style>
