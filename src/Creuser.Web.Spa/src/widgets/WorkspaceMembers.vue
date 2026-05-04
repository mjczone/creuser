<template>
  <div class="cr-w-list">
    <div class="cr-w-list-toolbar">
      <span class="cr-w-list-count">{{ members.length }}</span>
      <q-space />
      <q-btn flat dense round icon="refresh" size="sm" :loading="loading" @click="refresh">
        <q-tooltip>Reload</q-tooltip>
      </q-btn>
    </div>

    <div v-if="loading && members.length === 0" class="cr-w-list-state">
      <q-spinner size="24px" color="primary" />
      <span>Loading members…</span>
    </div>
    <div v-else-if="error" class="cr-w-list-state cr-w-list-error">
      <q-icon name="error_outline" size="24px" />
      <span>{{ error }}</span>
    </div>
    <div v-else-if="members.length === 0" class="cr-w-list-state">
      <q-icon name="group" size="24px" />
      <span>
        No explicit members yet. Admins always have implicit access; add a non-admin user from
        Settings → Members to grant Editor or Viewer access.
      </span>
    </div>
    <q-virtual-scroll v-else class="cr-w-list-list" :items="members" separator v-slot="{ item }">
      <q-item :key="item.userId" class="cr-w-list-row">
        <q-item-section avatar>
          <q-icon
            :name="item.isActive ? 'person' : 'person_off'"
            :color="item.isActive ? 'primary' : 'grey-6'"
            size="20px"
          />
        </q-item-section>
        <q-item-section>
          <q-item-label class="cr-w-list-row-name">
            {{ item.displayName }}
            <span v-if="!item.isActive" class="cr-w-list-row-disabled">inactive</span>
          </q-item-label>
          <q-item-label caption class="cr-w-list-row-meta">
            <span>{{ item.email }}</span>
            <span>· granted {{ formatDate(item.grantedAt) }}</span>
          </q-item-label>
        </q-item-section>
        <q-item-section side>
          <span class="cr-w-list-row-kind" :class="`cr-w-list-row-role-${item.role.toLowerCase()}`">
            {{ item.role }}
          </span>
        </q-item-section>
      </q-item>
    </q-virtual-scroll>
  </div>
</template>

<script setup lang="ts">
/**
 * WorkspaceMembers — read-only roster of users with explicit access to
 * this workspace, with their per-workspace role (Editor or Viewer).
 *
 * Admins do NOT appear here — admin-ness implies Editor everywhere
 * per the architecture's auth model. Membership management lives on
 * the Settings → Members page (admin-only); this widget is the
 * dashboard view.
 */
import { onMounted, ref, watch } from 'vue';
import { Members } from 'src/api';
import type { MemberResult } from 'src/api';

const props = defineProps<{
  widgetType: string;
  propsData: Record<string, unknown>;
  workspaceSlug?: string | null;
}>();

void props.propsData;

const members = ref<MemberResult[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);

async function refresh() {
  if (!props.workspaceSlug) return;
  loading.value = true;
  error.value = null;
  try {
    const res = await Members.listWorkspaceMembers({
      path: { slug: props.workspaceSlug },
    });
    members.value = res.data?.result ?? [];
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Failed to load members.';
  } finally {
    loading.value = false;
  }
}

function formatDate(iso: string): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
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

.cr-w-list-row-role-editor {
  background: rgba(96, 165, 250, 0.16);
  color: rgb(147, 197, 253);
}

.cr-w-list-row-role-viewer {
  background: rgba(148, 163, 184, 0.16);
  color: rgb(203, 213, 225);
}
</style>
