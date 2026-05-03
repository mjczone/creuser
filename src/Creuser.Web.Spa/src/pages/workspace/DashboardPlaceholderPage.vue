<template>
  <q-page class="q-pa-lg cr-dphold">
    <header class="cr-dphold-header">
      <h1 class="text-h5 q-ma-none">{{ dashboardSlug }}</h1>
      <p class="cr-dphold-sub">Dashboard preview — composer arrives next pass.</p>
    </header>
    <q-banner class="cr-dphold-banner" rounded>
      <template #avatar>
        <q-icon name="construction" class="cr-dphold-icon" />
      </template>
      Dashboards become composable surfaces in the next pass — dockview-vue layout,
      widget palette, drag-and-drop. This is a placeholder to exercise the
      workspace-scoped routing.
    </q-banner>
  </q-page>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useRoute } from 'vue-router';

const route = useRoute();
const dashboardSlug = computed(() => {
  const v = route.params.dashboardSlug;
  return typeof v === 'string' ? v : 'home';
});
</script>

<style lang="scss" scoped>
.cr-dphold-header {
  margin-bottom: 16px;
}

.cr-dphold-sub {
  margin: 6px 0 0;
  font-size: 13px;
  color: var(--cr-fg-secondary);
}

.cr-dphold-banner {
  background: var(--cr-bg-elevated);
  color: var(--cr-fg-secondary);
  border: 1px solid var(--cr-border-subtle);
}

.cr-dphold-icon {
  color: var(--cr-fg-tertiary);
}
</style>
