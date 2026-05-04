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
        <span v-if="editMode" class="cr-dash-edit-chip">editing</span>
      </div>
      <div v-if="auth.isAdmin" class="cr-dash-header-actions">
        <q-btn
          v-if="editMode"
          flat
          dense
          icon="add"
          label="Add widget"
          size="sm"
          @click="openAddWidget"
        />
        <q-btn
          v-if="editMode"
          unelevated
          color="primary"
          icon="check"
          label="Done"
          size="sm"
          :loading="saving"
          @click="exitEditMode"
        />
        <q-btn v-else flat dense icon="edit" label="Edit" size="sm" @click="enterEditMode" />
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

    <div
      v-else
      ref="canvasEl"
      class="cr-dash-canvas"
      :class="{ 'is-edit': editMode, 'cr-dock-creuser': useCreuserDockMapping }"
    >
      <!--
        WidgetHost is registered globally in `boot/widgets.ts` (via
        `app.component('WidgetHost', WidgetHost)`). dockview-vue's
        `createComponent` callback resolves panel `component:` strings
        through Vue's component registry, so no template slot is needed.

        `:key` is the dashboard slug so each dashboard navigation forces
        Vue to remount DockviewVue with a fresh dockview-core instance.
        Without this, a previous dashboard's failed fromJSON leaves
        dockview's internal Tabs collection in a half-state that crashes
        the next applyLayout's `api.clear()` call.
      -->
      <DockviewVue
        :key="dashboardSlug"
        :disableDnd="!editMode"
        :singleTabMode="'fullwidth'"
        :theme="dockviewTheme"
        @ready="onReady"
      />
    </div>
  </q-page>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, provide, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useQuasar } from 'quasar';
import { DockviewVue } from 'dockview-vue';
import { themeAbyss } from 'dockview-core';
import type {
  DockviewApi,
  DockviewReadyEvent,
  DockviewTheme,
  SerializedDockview,
} from 'dockview-core';
import 'dockview-core/dist/styles/dockview.css';
import { useDashboardsStore } from 'src/stores/dashboards';
import { useAuthStore } from 'src/stores/auth';
import { useBrandingStore } from 'src/stores/branding';
import { detectActivePreset } from 'src/css/palettes/registry';
import AddWidgetDialog from 'src/components/AddWidgetDialog.vue';

interface WidgetInstance {
  id: string;
  widgetType: string;
  props: Record<string, unknown>;
}

interface AddWidgetPayload {
  widgetType: string;
  props: Record<string, unknown>;
  defaultDockview?: { preferredPosition?: 'right' | 'below' | 'tab' };
}

const route = useRoute();
const router = useRouter();
const $q = useQuasar();
const dashboardsStore = useDashboardsStore();
const auth = useAuthStore();
const branding = useBrandingStore();

/**
 * Resolve the dockview theme to apply, plus whether the canvas should
 * carry the `cr-dock-creuser` marker class that activates our `--cr-*` →
 * `--dv-*` mapping in `theme.scss`.
 *
 * Pairing logic:
 *   - Named-theme presets (Standard Dark, Abyss, Dracula, …) carry their
 *     own `dockviewTheme` and the dock takes dockview's bundled colors.
 *   - Creuser presets (Creuser Dark, Creuser Light) opt into the marker
 *     class via `useCreuserDockMapping: true`, so the dock chrome follows
 *     the surrounding `--cr-*` chrome tokens instead.
 *   - Custom configs (admin tweaked colors away from any preset) fall
 *     back to the Creuser-mapped path on `themeAbyss`.
 */
const activePreset = computed(() =>
  detectActivePreset(
    branding.liveConfig.palette,
    branding.liveConfig.chrome,
    branding.liveConfig.chromeLight,
    branding.liveConfig.mode === 'light' ? 'light' : 'dark',
  ),
);
const dockviewTheme = computed<DockviewTheme>(
  () => activePreset.value?.dockviewTheme ?? themeAbyss,
);
const useCreuserDockMapping = computed<boolean>(
  () => activePreset.value?.useCreuserDockMapping ?? !activePreset.value,
);

const workspaceSlug = computed<string>(() => {
  const v = route.params.workspaceSlug;
  return typeof v === 'string' ? v : '';
});
const dashboardSlug = computed<string>(() => {
  const v = route.params.dashboardSlug;
  return typeof v === 'string' ? v : 'home';
});
// Edit mode is route-driven so deep-links and browser back/forward toggle
// it cleanly. /w/:slug/d/:dashSlug → view mode; /w/:slug/d/:dashSlug/edit
// → edit mode.
const editMode = computed<boolean>(() => route.path.endsWith('/edit'));

const loading = ref(false);
const saving = ref(false);
const dashboard = ref<ReturnType<typeof dashboardsStore.getDashboard>>(null);
const widgetInstances = ref<WidgetInstance[]>([]);
const canvasEl = ref<HTMLElement | null>(null);
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
    // No applyLayout call here — the DockviewVue's :key="dashboardSlug"
    // forces a remount on every dashboard switch, so onReady fires for
    // the fresh instance and runs applyLayout from there. Calling it here
    // races against that remount: dockviewApi may still point at the
    // about-to-be-disposed instance, and api.clear() blows up on the
    // dead Tabs collection.
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
  api.clear();

  let layout: SerializedDockview | null = null;
  try {
    const parsed = JSON.parse(layoutJson);
    if (parsed && typeof parsed === 'object' && parsed.grid) {
      layout = parsed as SerializedDockview;
    }
  } catch {
    /* fall through */
  }

  let layoutWasRejected = false;
  if (layout) {
    try {
      api.fromJSON(layout);
      // dockview catches deserialization errors internally and reverts —
      // it doesn't always re-throw, so we can't trust a missing exception
      // to mean success. And dockview will happily deserialize "hollow"
      // panels (no contentComponent, or params.instanceId pointing at a
      // widget we don't know about) — those render as blank panes because
      // WidgetHost has nothing to look up. Treat those as rejection too,
      // so the fallback below builds a clean layout from `instances`.
      const knownIds = new Set(instances.map((i) => i.id));
      const allPanelsValid =
        api.panels.length > 0 &&
        api.panels.every((p) => {
          const id = (p as unknown as { params?: { instanceId?: string } }).params?.instanceId;
          return !!id && knownIds.has(id);
        });
      if (allPanelsValid) {
        // dockview persists the gridview's `width`/`height` in the
        // serialized layout, and `fromJSON` applies them verbatim — so a
        // dashboard saved while the canvas was briefly small (e.g. before
        // q-page-container expanded) keeps that small size on every reload,
        // ignoring its actual container. Force a re-layout against the
        // real canvas dimensions so panels fill the space.
        resizeToContainer(api);
        return;
      }
      layoutWasRejected = true;
    } catch {
      /* layout incompatible; fall through */
      layoutWasRejected = true;
    }
  }

  // Fallback: spawn one panel per widget instance via dockview's addPanel,
  // which sets `contentComponent` + `params` correctly. This is the path
  // taken on (a) seeded dashboards (empty layout, populated widgets array),
  // (b) layouts dockview rejected, and (c) hand-edited DB rows that broke
  // the schema. The first edit-mode "Done" save overwrites the layout
  // with dockview's canonical toJSON output.
  for (const instance of instances) {
    api.addPanel({
      id: instance.id,
      component: 'WidgetHost',
      title: prettyTitleFor(instance.widgetType),
      params: { instanceId: instance.id },
    });
  }
  resizeToContainer(api);

  // If the saved layout JSON was non-empty but dockview rejected it,
  // proactively repair the row so subsequent loads don't keep tripping
  // dockview's deserializer (which logs a noisy console error each time).
  // Admin-only — non-admins land on the fallback path indefinitely until
  // an admin edits the dashboard.
  if (layoutWasRejected && auth.isAdmin && workspaceSlug.value) {
    void repairLayout();
  }
}

function resizeToContainer(api: DockviewApi) {
  const el = canvasEl.value;
  if (!el) return;
  const w = el.clientWidth;
  const h = el.clientHeight;
  if (w > 0 && h > 0) api.layout(w, h);
}

async function repairLayout() {
  if (!dockviewApi || !workspaceSlug.value) return;
  try {
    const layoutJson = JSON.stringify(dockviewApi.toJSON());
    const widgetsJson = JSON.stringify(widgetInstances.value);
    await dashboardsStore.saveLayout(
      workspaceSlug.value,
      dashboardSlug.value,
      layoutJson,
      widgetsJson,
    );
  } catch {
    // Silent — the user can still recover by clicking Edit → Done.
    // No notify(); this is a quiet self-heal not user-facing action.
  }
}

function prettyTitleFor(widgetType: string): string {
  return widgetType.replace(/([a-z])([A-Z])/g, '$1 $2');
}

function enterEditMode() {
  void router.push(`/w/${workspaceSlug.value}/d/${dashboardSlug.value}/edit`);
}

async function exitEditMode() {
  await saveLayout();
  await router.push(`/w/${workspaceSlug.value}/d/${dashboardSlug.value}`);
}

async function saveLayout() {
  if (!dockviewApi || !workspaceSlug.value) return;
  saving.value = true;
  try {
    const layoutJson = JSON.stringify(dockviewApi.toJSON());
    const widgetsJson = JSON.stringify(widgetInstances.value);
    const updated = await dashboardsStore.saveLayout(
      workspaceSlug.value,
      dashboardSlug.value,
      layoutJson,
      widgetsJson,
    );
    if (updated) dashboard.value = updated;
  } catch (ex: unknown) {
    $q.notify({
      type: 'negative',
      message: ex instanceof Error ? ex.message : 'Failed to save dashboard.',
    });
  } finally {
    saving.value = false;
  }
}

function openAddWidget() {
  $q.dialog({
    component: AddWidgetDialog,
  }).onOk((payload: AddWidgetPayload) => {
    addWidgetInstance(payload);
  });
}

function addWidgetInstance(payload: AddWidgetPayload) {
  const instance: WidgetInstance = {
    id:
      'w-' +
      (globalThis.crypto?.randomUUID().slice(0, 8) ?? Math.random().toString(36).slice(2, 10)),
    widgetType: payload.widgetType,
    props: payload.props,
  };
  // Always update the widgets array first — that way the instance survives
  // even if dockview is mid-remount and the api isn't reachable. The next
  // onReady's applyLayout fallback path picks it up.
  widgetInstances.value = [...widgetInstances.value, instance];

  if (!dockviewApi) {
    $q.notify({
      type: 'warning',
      message: 'Dashboard not ready yet — widget queued; saving the dashboard will persist it.',
      timeout: 3000,
    });
    return;
  }

  // Default placement: tab into the active group, mirroring the design
  // doc's note that v1 ships the simplest case. The user drags from
  // there if they want a split.
  try {
    dockviewApi.addPanel({
      id: instance.id,
      component: 'WidgetHost',
      title: prettyTitleFor(instance.widgetType),
      params: { instanceId: instance.id },
    });
  } catch (ex: unknown) {
    $q.notify({
      type: 'negative',
      message:
        ex instanceof Error ? `Failed to add widget: ${ex.message}` : 'Failed to add widget.',
      timeout: 5000,
    });
  }
}

watch(
  [workspaceSlug, dashboardSlug],
  () => {
    // Drop the stale api reference — :key on DockviewVue forces a
    // remount on dashboardSlug change, and the previous api is about to
    // be disposed. The new instance assigns dockviewApi from onReady.
    dockviewApi = null;
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
  // Fixed height (48px = `--cr-row-header`-like) keeps the header aligned
  // with `.cr-sub-sidebar-header` so their bottom borders read as one
  // continuous horizontal line. Padding inside the row stays for left/right
  // breathing room only; vertical centering comes from `align-items`.
  min-height: 44px;
  padding: 0 16px;
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
  // Without this, the h1 inherits a tall line-height from somewhere up
  // the cascade (turning the dashboard header into a ~120px slab). Force
  // a tight line-height so the header reads at its intended ~40px.
  line-height: 1.3;
  font-size: 14px;
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

.cr-dash-edit-chip {
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  background: rgba(96, 165, 250, 0.16);
  color: rgb(147, 197, 253);
  padding: 2px 6px;
  border-radius: 3px;
}

.cr-dash-header-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
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
  position: relative;
  // dockview-vue's mount element does not fill its parent on its own, and
  // its inner `.dv-shell` uses `style="height:100%"` — which collapses to
  // 0 if its containing block has only a flex-resolved height (Chrome
  // treats `flex: 1 1 0%`-derived sizes as indefinite for percentage
  // children). Position the wrapper absolutely against the canvas so it
  // gets an unambiguously definite size that `.dv-shell`'s 100% can
  // resolve against.
  > * {
    position: absolute;
    inset: 0;
  }

  &.is-edit {
    box-shadow: inset 0 0 0 1px rgba(96, 165, 250, 0.2);
  }

  // Hide tab close buttons in view mode. Drag-and-drop is already gated
  // via `:disableDnd="!editMode"` on DockviewVue, so leaving the close
  // affordance visible is inconsistent — and an accidental click on the X
  // is destructive (the widget instance disappears from the layout). The
  // BrandingPage's Edit → Add/Remove flow is the canonical destructive
  // entry point.
  &:not(.is-edit) :deep(.dv-default-tab-action) {
    display: none;
  }

  // Single-tab fullwidth mode (`:singleTabMode="'fullwidth'"`) — dockview
  // resets `.dv-tab { padding: 0 }` in this mode, so the title text and
  // close button get flush against the tab strip's edges. Restore the
  // same padding the multi-tab base uses (`0.25rem 0.5rem`) so single-
  // and multi-tab groups have visually consistent breathing room.
  :deep(
    .dv-tabs-and-actions-container.dv-single-tab.dv-full-width-single-tab .dv-tabs-container .dv-tab
  ) {
    padding: 0.25rem 0.5rem;
  }
}
</style>
