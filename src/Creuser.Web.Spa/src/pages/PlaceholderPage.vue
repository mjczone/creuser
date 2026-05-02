<template>
  <q-page class="q-pa-lg">
    <div class="row items-baseline q-gutter-md q-mb-sm">
      <h1 class="text-h5 q-ma-none">{{ title }}</h1>
      <span class="text-caption text-grey-6">{{ description }}</span>
    </div>
    <q-banner class="bg-grey-2 q-pa-md" rounded>
      <template #avatar>
        <q-icon name="construction" color="grey-7" />
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
