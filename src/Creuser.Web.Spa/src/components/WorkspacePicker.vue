<template>
  <q-btn-dropdown
    flat
    no-caps
    dense
    align="left"
    class="cr-wpicker"
    :loading="loading"
    :menu-anchor="'bottom left'"
    :menu-self="'top left'"
  >
    <template #label>
      <span class="cr-wpicker-label">
        <q-icon
          :name="active ? iconForType(active.type) : 'home'"
          size="16px"
          class="cr-wpicker-icon"
        />
        <span class="cr-wpicker-text">
          <span class="cr-wpicker-name">{{ active?.name ?? 'Home' }}</span>
          <code v-if="active" class="cr-wpicker-slug">{{ active.slug }}</code>
        </span>
      </span>
    </template>

    <q-list dense class="cr-wpicker-list">
      <q-item
        clickable
        v-close-popup
        :active="!active"
        active-class="cr-wpicker-active"
        @click="onPickHome"
      >
        <q-item-section avatar>
          <q-icon name="home" size="16px" />
        </q-item-section>
        <q-item-section>
          <q-item-label>Home</q-item-label>
          <q-item-label caption>Workspace picker</q-item-label>
        </q-item-section>
      </q-item>

      <q-separator />

      <q-item v-if="loading" class="cr-wpicker-empty">
        <q-item-section>
          <q-spinner size="14px" />
          <span>Loading…</span>
        </q-item-section>
      </q-item>

      <q-item v-else-if="workspaces.length === 0" class="cr-wpicker-empty">
        <q-item-section>
          <span class="cr-wpicker-empty-text">
            {{
              auth.isAdmin
                ? 'No workspaces yet — create one in Platform Settings.'
                : 'No workspaces accessible to you.'
            }}
          </span>
        </q-item-section>
      </q-item>

      <q-item
        v-for="ws in workspaces"
        v-else
        :key="ws.workspaceId"
        clickable
        v-close-popup
        :active="active?.slug === ws.slug"
        active-class="cr-wpicker-active"
        @click="onPickWorkspace(ws)"
      >
        <q-item-section avatar>
          <q-icon :name="iconForType(ws.type)" size="16px" />
        </q-item-section>
        <q-item-section>
          <q-item-label class="cr-wpicker-item-name">{{ ws.name }}</q-item-label>
          <q-item-label caption>
            <code class="cr-wpicker-item-slug">{{ ws.slug }}</code>
          </q-item-label>
        </q-item-section>
        <q-item-section v-if="ws.lastSyncStatus" side>
          <q-icon
            :name="ws.lastSyncStatus === 'ok' ? 'check_circle' : 'error'"
            :color="ws.lastSyncStatus === 'ok' ? 'positive' : 'negative'"
            size="14px"
          />
        </q-item-section>
      </q-item>

      <template v-if="auth.isAdmin">
        <q-separator />
        <q-item
          clickable
          v-close-popup
          to="/settings/workspaces"
          class="cr-wpicker-manage"
        >
          <q-item-section avatar>
            <q-icon name="settings" size="16px" />
          </q-item-section>
          <q-item-section>
            <q-item-label>Manage workspaces…</q-item-label>
          </q-item-section>
        </q-item>
      </template>
    </q-list>
  </q-btn-dropdown>
</template>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { Workspaces, type WorkspaceResult } from 'src/api';
import { useAuthStore } from 'stores/auth';
import { useWorkspaceStore } from 'stores/workspace';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';

const router = useRouter();
const auth = useAuthStore();
const workspaceStore = useWorkspaceStore();
const { workspace: active } = useActiveWorkspace();

const workspaces = ref<WorkspaceResult[]>([]);
const loading = ref(false);

function iconForType(type: string): string {
  if (type === 'git') return 'cloud';
  if (type === 'local') return 'folder';
  if (type === 's3') return 'cloud_queue';
  return 'source';
}

async function load() {
  loading.value = true;
  try {
    const res = await Workspaces.listWorkspaces();
    if (res.error) {
      // 403 → non-admin without explicit workspace_members rows. Treat as
      // empty list rather than error; the empty-state copy explains.
      workspaces.value = [];
      return;
    }
    workspaces.value = res.data?.result ?? [];
    // Seed the workspace cache so subsequent navigation doesn't re-fetch.
    for (const ws of workspaces.value) workspaceStore.upsert(ws);
  } finally {
    loading.value = false;
  }
}

async function onPickWorkspace(ws: WorkspaceResult) {
  if (active.value?.slug === ws.slug) return;
  workspaceStore.upsert(ws);
  await router.push({ name: 'workspace-home', params: { workspaceSlug: ws.slug } });
}

async function onPickHome() {
  if (!active.value) return;
  await router.push({ name: 'home' });
}

// Re-fetch on auth transitions (e.g. login from another tab pushing into
// here). Cheap — single endpoint hit.
watch(
  () => auth.isAuthenticated,
  (next) => {
    if (next) void load();
    else workspaces.value = [];
  },
);

onMounted(() => {
  if (auth.isAuthenticated) void load();
});
</script>

<style lang="scss" scoped>
.cr-wpicker {
  // Compact button presentation: icon + name in a single horizontal row, slug
  // hidden on small screens. The dropdown carriage is the actual picker UI.
  padding: 0 8px;
  border-radius: 4px;
  min-height: 32px;
  font-weight: 500;
  color: var(--cr-fg-primary);

  &:hover {
    background: var(--cr-bg-hover);
  }
}

.cr-wpicker-label {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
}

.cr-wpicker-icon {
  color: var(--cr-fg-secondary);
}

.cr-wpicker-text {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.cr-wpicker-name {
  font-weight: 500;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 200px;
}

.cr-wpicker-slug {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  background: var(--cr-bg-elevated);
  padding: 1px 5px;
  border-radius: 3px;
  color: var(--cr-fg-secondary);
  font-weight: 400;

  // Hide slug on tight screens so the picker doesn't push the header tools
  // off the right edge.
  @media (max-width: 768px) {
    display: none;
  }
}

.cr-wpicker-list {
  min-width: 280px;
  padding: 4px 0;
}

.cr-wpicker-active {
  color: var(--q-primary);
  background: var(--cr-brand-tint-soft);
  font-weight: 500;
}

.cr-wpicker-empty {
  font-size: 12px;
  color: var(--cr-fg-tertiary);

  .q-item__section {
    flex-direction: row;
    align-items: center;
    gap: 6px;
  }
}

.cr-wpicker-empty-text {
  font-size: 12px;
  line-height: 1.4;
  color: var(--cr-fg-secondary);
}

.cr-wpicker-item-name {
  font-size: 13px;
}

.cr-wpicker-item-slug {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  color: var(--cr-fg-tertiary);
}

.cr-wpicker-manage {
  font-size: 12px;
  color: var(--cr-fg-secondary);
}
</style>
