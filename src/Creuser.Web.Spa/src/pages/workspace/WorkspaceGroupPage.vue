<template>
  <q-page class="cr-group-page">
    <div v-if="loading" class="cr-group-state">
      <q-spinner size="32px" color="primary" />
      <p>Loading group…</p>
    </div>
    <div v-else-if="!group" class="cr-group-state">
      <q-icon name="folder_off" size="40px" />
      <h1>Group "{{ groupSlug }}" not found</h1>
      <p>It may have been deleted or renamed.</p>
    </div>
    <div v-else-if="group.children.length > 0" class="cr-group-state">
      <q-spinner size="32px" color="primary" />
      <p>Opening {{ group.children[0]?.name }}…</p>
    </div>
    <div v-else class="cr-group-empty">
      <q-icon :name="group.icon" size="48px" class="cr-group-empty-icon" />
      <h1 class="cr-group-empty-title">{{ group.name }}</h1>
      <p class="cr-group-empty-sub">
        This group has no dashboards yet. Add one to populate it.
      </p>
      <q-btn
        v-if="auth.isAdmin"
        unelevated
        color="primary"
        icon="add"
        label="Add dashboard"
        size="md"
        class="cr-group-empty-cta"
        @click="openCreate"
      />
      <p v-else class="cr-group-empty-hint">
        Workspace admins can add dashboards to this group from the icon bar's "+" menu.
      </p>
    </div>
  </q-page>
</template>

<script setup lang="ts">
/**
 * Landing for /w/:slug/g/:groupSlug. When the group has children we
 * router-replace to the first child (so the icon-bar click feels
 * instantaneous). When empty, we render an empty-state with an
 * "Add dashboard" CTA pre-filling the group slug.
 *
 * The empty state matters for the create-flow: an admin who creates a
 * group with no dashboards yet shouldn't land on a stale page — they
 * should see exactly how to populate it.
 */
import { computed, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useQuasar } from 'quasar';
import { useDashboardsStore } from 'src/stores/dashboards';
import { useAuthStore } from 'src/stores/auth';
import CreateDashboardDialog from 'src/components/CreateDashboardDialog.vue';

const route = useRoute();
const router = useRouter();
const $q = useQuasar();
const dashboardsStore = useDashboardsStore();
const auth = useAuthStore();

const workspaceSlug = computed(() =>
  typeof route.params.workspaceSlug === 'string' ? route.params.workspaceSlug : '',
);
const groupSlug = computed(() =>
  typeof route.params.groupSlug === 'string' ? route.params.groupSlug : '',
);

const loading = ref(true);

const group = computed(() => {
  const tree = dashboardsStore.getNavTree(workspaceSlug.value);
  return tree?.groups.find((g) => g.slug === groupSlug.value) ?? null;
});

async function load() {
  if (!workspaceSlug.value) return;
  loading.value = true;
  try {
    await dashboardsStore.ensureNavTree(workspaceSlug.value);
    // If the group has children, redirect to the first child immediately.
    // Use replace so back-button skips this page.
    const first = group.value?.children[0];
    if (first) {
      await router.replace(`/w/${workspaceSlug.value}/d/${first.slug}`);
      return;
    }
  } finally {
    loading.value = false;
  }
}

function openCreate() {
  if (!workspaceSlug.value || !group.value) return;
  const groups = dashboardsStore
    .getNavTree(workspaceSlug.value)
    ?.groups.map((g) => ({ slug: g.slug, name: g.name })) ?? [];
  $q.dialog({
    component: CreateDashboardDialog,
    componentProps: {
      workspaceSlug: workspaceSlug.value,
      groups,
      // CreateDashboardDialog already supports an optional preselectedGroup;
      // if not, the user can pick the group from the dropdown.
      preselectedGroup: groupSlug.value,
    },
  }).onOk((created: { slug?: string } | null) => {
    void onCreated(created);
  });
}

async function onCreated(created: { slug?: string } | null) {
  if (!workspaceSlug.value) return;
  await dashboardsStore.ensureNavTree(workspaceSlug.value, true);
  if (created?.slug) {
    await router.push(`/w/${workspaceSlug.value}/d/${created.slug}`);
  }
}

onMounted(() => {
  void load();
});
watch([workspaceSlug, groupSlug], () => {
  void load();
});
</script>

<style lang="scss" scoped>
.cr-group-page {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  padding: 0 24px;
}

.cr-group-state,
.cr-group-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  text-align: center;
  max-width: 480px;
  color: var(--cr-fg-secondary, #ccc);
}

.cr-group-state p {
  margin: 0;
  font-size: 13px;
  color: var(--cr-fg-tertiary, #888);
}

.cr-group-state h1 {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-group-empty-icon {
  color: var(--cr-fg-tertiary, #888);
}

.cr-group-empty-title {
  margin: 0;
  font-size: 22px;
  font-weight: 600;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-group-empty-sub {
  margin: 0;
  font-size: 14px;
  color: var(--cr-fg-secondary, #ccc);
  line-height: 1.5;
}

.cr-group-empty-cta {
  margin-top: 12px;
}

.cr-group-empty-hint {
  margin: 0;
  font-size: 12px;
  color: var(--cr-fg-tertiary, #888);
  font-style: italic;
}
</style>
