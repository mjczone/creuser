<template>
  <StatusBanner
    v-if="result"
    :variant="result.ok ? 'success' : 'error'"
    :title="result.ok ? 'Connected' : 'Failed'"
    dismissable
    @dismiss="emit('dismiss')"
  >
    <span v-if="result.ok && result.latencyMs !== null">
      · {{ result.model }} replied in {{ result.latencyMs }}ms
    </span>
    <span v-if="!result.ok && result.error"> · {{ result.error }}</span>
  </StatusBanner>
</template>

<script setup lang="ts">
import type { AgentHealthResult } from 'src/api';
import StatusBanner from 'components/StatusBanner.vue';

defineProps<{
  // Accept undefined too so callers passing a Record-indexed value
  // (`health.anthropic`) don't need to coerce.
  result: AgentHealthResult | null | undefined;
}>();

const emit = defineEmits<{ dismiss: [] }>();
</script>
