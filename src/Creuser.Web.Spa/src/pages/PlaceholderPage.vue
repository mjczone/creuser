<template>
  <q-page class="q-pa-lg">
    <div class="row items-baseline q-gutter-md q-mb-sm">
      <h1 class="text-h5 q-ma-none cr-placeholder-title">{{ title }}</h1>
      <span class="text-caption cr-placeholder-description">{{ description }}</span>
    </div>
    <q-banner class="cr-placeholder-banner" rounded>
      <template #avatar>
        <q-icon name="construction" class="cr-placeholder-icon" />
      </template>
      Page placeholder. Full implementation arrives in v0.1.x.
    </q-banner>
  </q-page>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useRoute } from 'vue-router';

const route = useRoute();
const title = computed(() => (route.meta.title as string | undefined) ?? 'Creuser');
const description = computed(() => (route.meta.description as string | undefined) ?? '');
</script>

<style lang="scss" scoped>
.cr-placeholder-title {
  color: var(--cr-fg-primary);
}

.cr-placeholder-description {
  color: var(--cr-fg-secondary);
}

.cr-placeholder-banner {
  background: var(--cr-bg-elevated);
  color: var(--cr-fg-secondary);
  border: 1px solid var(--cr-border-subtle);
}

.cr-placeholder-icon {
  color: var(--cr-fg-tertiary);
}
</style>
