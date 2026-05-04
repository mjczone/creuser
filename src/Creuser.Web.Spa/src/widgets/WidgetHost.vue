<template>
  <div class="cr-widget-host">
    <component
      v-if="widget && instance"
      :is="widget.component"
      :widget-instance-id="instance.id"
      :widget-type="instance.widgetType"
      :props-data="instance.props"
      :workspace-slug="workspaceSlug"
    />
    <div v-else-if="!instance" class="cr-widget-error">
      <q-icon name="error_outline" size="32px" class="cr-widget-error-icon" />
      <p class="cr-widget-error-text">Widget instance "{{ instanceId }}" not found.</p>
    </div>
    <div v-else class="cr-widget-error">
      <q-icon name="extension_off" size="32px" class="cr-widget-error-icon" />
      <p class="cr-widget-error-text">No widget registered for type "{{ instance.widgetType }}".</p>
    </div>
  </div>
</template>

<script setup lang="ts">
/**
 * Universal pane content for the dockview composer. Each panel's `params`
 * carries `{ instanceId }` and the host's parent (DashboardPage) supplies
 * the dashboard's widget instance list. WidgetHost looks up the instance
 * by id, resolves widgetType -> registered component, and renders.
 *
 * Renders an error tile if the instance id is unknown (data drift) or if
 * the widget type isn't registered (a plugin removed without migrating
 * dashboards). Either case is recoverable — the operator deletes or
 * reconfigures the offending panel.
 */
import { computed, inject } from 'vue';
import type { Ref } from 'vue';
import { getWidget } from 'src/widgets/registry';

interface WidgetInstance {
  id: string;
  widgetType: string;
  props: Record<string, unknown>;
}

const props = defineProps<{
  /** Set by dockview from the panel params. */
  params: { instanceId: string };
}>();

const widgetInstances = inject<Ref<WidgetInstance[]>>(
  'cr-widget-instances',
  null as unknown as Ref<WidgetInstance[]>,
);
const workspaceSlug = inject<Ref<string>>('cr-workspace-slug', null as unknown as Ref<string>);

const instanceId = computed(() => props.params?.instanceId ?? '');
const instance = computed(
  () => widgetInstances?.value?.find((w) => w.id === instanceId.value) ?? null,
);
const widget = computed(() => (instance.value ? getWidget(instance.value.widgetType) : null));
</script>

<style lang="scss" scoped>
.cr-widget-host {
  height: 100%;
  width: 100%;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-elevated, #1a1a1d);
}

.cr-widget-error {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--cr-fg-tertiary, #888);
  padding: 16px;
  text-align: center;
}

.cr-widget-error-icon {
  color: var(--cr-fg-tertiary, #888);
}

.cr-widget-error-text {
  margin: 0;
  font-size: 13px;
}
</style>
