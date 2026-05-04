<template>
  <q-page class="cr-dash-page">
    <header v-if="dashboard" class="cr-dash-header">
      <div class="cr-dash-header-info">
        <q-icon
          v-if="dashboard.icon"
          :name="dashboard.icon"
          size="22px"
          class="cr-dash-header-icon"
        />
        <h1 class="cr-dash-header-title">{{ dashboard.name }}</h1>
        <span v-if="dashboard.isDefault" class="cr-dash-default-chip">default</span>
      </div>
      <div v-if="auth.isAdmin" class="cr-dash-header-actions">
        <span class="cr-dash-coming-soon">Composer designer arrives next pass.</span>
      </div>
    </header>

    <div v-if="loading" class="cr-dash-state">
      <q-spinner size="32px" color="primary" />
      <p>Loading dashboard…</p>
    </div>

    <div v-else-if="!dashboard" class="cr-dash-state">
      <q-icon name="error_outline" size="40px" />
      <p>Dashboard "{{ dashboardSlug }}" not found.</p>
    </div>

    <div v-else class="cr-dash-canvas dockview-theme-dark">
      <DockviewVue @ready="onReady">
        <template #WidgetHost="{ params }">
          <WidgetHost :params="(params as { instanceId: string })" />
        </template>
      </DockviewVue>
    </div>
  </q-page>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, provide, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { DockviewVue } from 'dockview-vue';
import type { DockviewApi, DockviewReadyEvent, SerializedDockview } from 'dockview-core';
import 'dockview-core/dist/styles/dockview.css';
import { useDashboardsStore } from 'src/stores/dashboards';
import { useAuthStore } from 'src/stores/auth';
import WidgetHost from 'src/widgets/WidgetHost.vue';

interface WidgetInstance {
  id: string;
  widgetType: string;
  props: Record<string, unknown>;
}

const route = useRoute();
const dashboardsStore = useDashboardsStore();
const auth = useAuthStore();

const workspaceSlug = computed<string>(() => {
  const v = route.params.workspaceSlug;
  return typeof v === 'string' ? v : '';
});
const dashboardSlug = computed<string>(() => {
  const v = route.params.dashboardSlug;
  return typeof v === 'string' ? v : 'home';
});

const loading = ref(false);
const dashboard = ref<ReturnType<typeof dashboardsStore.getDashboard>>(null);
const widgetInstances = ref<WidgetInstance[]>([]);
let dockviewApi: DockviewApi | null = null;

provide('cr-widget-instances', widgetInstances);
provide('cr-workspace-slug', workspaceSlug);

async function loadDashboard() {
  if (!workspaceSlug.value || !dashboardSlug.value) return;
  loading.value = true;
  try {
    const d = await dashboardsStore.ensureDashboard(workspaceSlug.value, dashboardSlug.value);
    dashboard.value = d;
    widgetInstances.value = parseWidgets(d?.widgetsJson ?? '[]');
    // If dockview is already mounted, re-apply the layout. Otherwise the
    // onReady handler runs next and picks it up.
    if (dockviewApi) applyLayout(dockviewApi, d?.layoutJson ?? '{}', widgetInstances.value);
  } finally {
    loading.value = false;
  }
}

function onReady(event: DockviewReadyEvent) {
  dockviewApi = event.api;
  if (dashboard.value) {
    applyLayout(dockviewApi, dashboard.value.layoutJson, widgetInstances.value);
  }
}

function parseWidgets(json: string): WidgetInstance[] {
  try {
    const parsed = JSON.parse(json);
    if (Array.isArray(parsed)) return parsed as WidgetInstance[];
  } catch {
    /* malformed; fall through to empty */
  }
  return [];
}

/**
 * Apply the dashboard's persisted layout to the dockview instance. If the
 * layout JSON is malformed or empty, fall back to spawning each widget
 * instance into its own pane. The fallback handles fresh dashboards
 * (created via POST without a saved layout) and the "operator hand-edited
 * the JSON to gibberish" recovery case.
 */
function applyLayout(api: DockviewApi, layoutJson: string, instances: WidgetInstance[]) {
  // Always clear before applying — re-entry from a route change leaves
  // panels behind otherwise.
  api.clear();

  let layout: SerializedDockview | null = null;
  try {
    const parsed = JSON.parse(layoutJson);
    if (parsed && typeof parsed === 'object' && parsed.grid) {
      layout = parsed as SerializedDockview;
    }
  } catch {
    /* fall through to fallback */
  }

  if (layout) {
    try {
      api.fromJSON(layout);
      return;
    } catch {
      /* layout incompatible; fall through to fallback */
    }
  }

  // Fallback: spawn one panel per widget instance.
  for (const instance of instances) {
    api.addPanel({
      id: instance.id,
      component: 'WidgetHost',
      title: prettyTitleFor(instance.widgetType),
      params: { instanceId: instance.id },
    });
  }
}

function prettyTitleFor(widgetType: string): string {
  return widgetType.replace(/([a-z])([A-Z])/g, '$1 $2');
}

watch(
  [workspaceSlug, dashboardSlug],
  () => {
    void loadDashboard();
  },
  { immediate: true },
);

onBeforeUnmount(() => {
  dockviewApi = null;
});
</script>

<style lang="scss" scoped>
.cr-dash-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  padding: 0;
}

.cr-dash-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  border-bottom: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
  background: var(--cr-bg-elevated, #1a1a1d);
}

.cr-dash-header-info {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.cr-dash-header-icon {
  color: var(--cr-fg-secondary, #ccc);
}

.cr-dash-header-title {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-dash-default-chip {
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  background: var(--cr-bg-subtle, #262629);
  color: var(--cr-fg-tertiary, #888);
  padding: 2px 6px;
  border-radius: 3px;
}

.cr-dash-header-actions {
  font-size: 12px;
  color: var(--cr-fg-tertiary, #888);
}

.cr-dash-coming-soon {
  font-style: italic;
}

.cr-dash-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 8px;
  align-items: center;
  justify-content: center;
  color: var(--cr-fg-tertiary, #888);
}

.cr-dash-canvas {
  flex: 1;
  min-height: 0;
}
</style>
