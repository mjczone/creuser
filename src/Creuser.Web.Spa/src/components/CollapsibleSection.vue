<template>
  <q-expansion-item
    v-model="open"
    dense
    switch-toggle-side
    expand-icon-toggle
    header-class="cr-collapsible-header"
    class="cr-collapsible"
    :data-section-key="sectionKey"
  >
    <template #header>
      <q-item-section>
        <q-item-label class="cr-collapsible-title">{{ title }}</q-item-label>
        <q-item-label v-if="caption" caption class="cr-collapsible-caption">
          {{ caption }}
        </q-item-label>
      </q-item-section>
      <q-item-section v-if="$slots.action" side @click.stop>
        <slot name="action" />
      </q-item-section>
    </template>
    <div class="cr-collapsible-body">
      <slot />
    </div>
  </q-expansion-item>
</template>

<script setup lang="ts">
/**
 * Compact collapsible section — title + optional caption + an optional
 * action slot rendered on the far right of the header. Designed to nest
 * inside the bigger top-level q-expansion-items used as page sections.
 *
 * Reusable across any Settings page where you want a list of related
 * subsections that can each be collapsed independently. Pair with
 * `useLocalStorage` on the parent to remember which subsections each user
 * left open.
 *
 * Usage:
 *   <CollapsibleSection
 *     v-model="open"
 *     title="Anthropic"
 *     caption="Cloud Claude"
 *   >
 *     <template #action>
 *       <q-btn flat dense no-caps icon="play_arrow" label="Test" @click.stop="run" />
 *     </template>
 *     <!-- body content -->
 *   </CollapsibleSection>
 */
const open = defineModel<boolean>({ default: true });

defineProps<{
  title: string;
  caption?: string;
  /** Optional stable key used by the parent's `?expand=` query handling to scroll this section into view. */
  sectionKey?: string;
}>();
</script>

<style lang="scss" scoped>
.cr-collapsible {
  border: 1px solid var(--cr-border-subtle);
  border-radius: 4px;
  background: var(--cr-bg-elevated);
  overflow: hidden;
}

:deep(.cr-collapsible-header) {
  padding: 6px 10px;
  min-height: 0;

  .q-icon {
    color: var(--cr-fg-tertiary);
  }
}

.cr-collapsible-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--cr-fg-primary);
  letter-spacing: 0.04em;
  line-height: 1.3;
}

.cr-collapsible-caption {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  margin-top: 1px;
}

.cr-collapsible-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 4px 14px 14px;
  border-top: 1px solid var(--cr-border-subtle);
}
</style>
