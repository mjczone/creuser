<template>
  <div class="cr-widget-placeholder">
    <q-icon :name="icon" size="40px" class="cr-widget-placeholder-icon" />
    <h2 class="cr-widget-placeholder-title">{{ title }}</h2>
    <p class="cr-widget-placeholder-sub">
      Widget type:
      <code>{{ widgetType }}</code>
    </p>
    <p class="cr-widget-placeholder-text">
      The real {{ title.toLowerCase() }} widget arrives in a follow-up commit. This stub proves the
      dockview-vue → WidgetHost → registry loop is wired correctly.
    </p>
    <p v-if="propsSummary" class="cr-widget-placeholder-props">{{ propsSummary }}</p>
  </div>
</template>

<script setup lang="ts">
/**
 * Single placeholder component registered against multiple widget types
 * during the dockview-vue scaffold milestone. Each registered type passes
 * `propsData` (e.g. `{ limit: 10 }`); the placeholder echoes the props so
 * we can verify the dashboard's seeded widget instances flow through.
 *
 * Replaced one-by-one as the real widgets land. Once all v1 widgets are
 * implemented this file deletes.
 */
import { computed } from 'vue';

const props = defineProps<{
  widgetType: string;
  propsData: Record<string, unknown>;
}>();

const meta: Record<string, { title: string; icon: string }> = {
  RunsList: { title: 'Runs', icon: 'play_circle' },
  RunInspector: { title: 'Run Inspector', icon: 'troubleshoot' },
  JobScriptList: { title: 'Scripts', icon: 'description' },
  JobScriptEditor: { title: 'Script Editor', icon: 'edit_note' },
  ScheduleList: { title: 'Schedules', icon: 'schedule' },
  ProjectionReport: { title: 'Projection Report', icon: 'account_tree' },
  Markdown: { title: 'Notes', icon: 'sticky_note_2' },
  WorkspaceMembers: { title: 'Members', icon: 'group' },
};

const title = computed(() => meta[props.widgetType]?.title ?? props.widgetType);
const icon = computed(() => meta[props.widgetType]?.icon ?? 'extension');
const propsSummary = computed(() => {
  const entries = Object.entries(props.propsData ?? {});
  if (entries.length === 0) return null;
  return entries.map(([k, v]) => `${k}: ${JSON.stringify(v)}`).join(', ');
});
</script>

<style lang="scss" scoped>
.cr-widget-placeholder {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  text-align: center;
  padding: 24px;
  gap: 12px;
  color: var(--cr-fg-secondary, #ccc);
}

.cr-widget-placeholder-icon {
  color: var(--cr-fg-tertiary, #888);
}

.cr-widget-placeholder-title {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-widget-placeholder-sub {
  margin: 0;
  font-size: 12px;
  color: var(--cr-fg-tertiary, #888);

  code {
    background: var(--cr-bg-subtle, #262629);
    padding: 1px 6px;
    border-radius: 3px;
  }
}

.cr-widget-placeholder-text {
  margin: 0;
  font-size: 13px;
  max-width: 360px;
  line-height: 1.5;
}

.cr-widget-placeholder-props {
  margin: 0;
  font-size: 11px;
  color: var(--cr-fg-tertiary, #888);
  font-family: var(--cr-font-mono, ui-monospace, monospace);
}
</style>
