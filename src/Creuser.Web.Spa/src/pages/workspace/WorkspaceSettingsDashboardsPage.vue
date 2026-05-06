<template>
  <div class="cr-dash-settings">
    <header class="cr-dash-settings-header">
      <div>
        <h1 class="text-h6 q-ma-none">Dashboards</h1>
        <p class="cr-dash-settings-subhead">
          Manage dashboard groups and the dashboards inside them. Standalone dashboards live at
          the top level of the icon bar; grouped dashboards collapse under a group's icon and
          surface in the sub-sidebar. Reorder with the up/down arrows. Renaming changes the label
          everywhere; the slug stays stable.
        </p>
      </div>
      <q-space />
      <q-btn
        flat
        dense
        no-caps
        icon="refresh"
        size="sm"
        :loading="loading"
        @click="reload"
      >
        <q-tooltip>Reload</q-tooltip>
      </q-btn>
      <q-btn
        unelevated
        dense
        no-caps
        icon="add"
        label="New group"
        size="sm"
        color="primary"
        @click="onCreateGroup"
      />
    </header>

    <div v-if="loading && !navTree" class="cr-dash-settings-loading">Loading…</div>

    <template v-else-if="navTree">
      <!-- Standalone dashboards (no group) -->
      <section class="cr-dash-card">
        <header class="cr-dash-card-header">
          <q-icon name="folder_off" size="18px" class="cr-dash-card-icon" />
          <h2 class="cr-dash-card-title">Standalone dashboards</h2>
          <span class="cr-dash-card-count">{{ navTree.standalones.length }}</span>
          <q-space />
          <q-btn
            flat
            dense
            round
            size="sm"
            icon="add"
            aria-label="Add standalone dashboard"
            @click="onCreateDashboard(null)"
          >
            <q-tooltip>Add dashboard</q-tooltip>
          </q-btn>
        </header>

        <div v-if="navTree.standalones.length === 0" class="cr-dash-card-empty">
          No standalone dashboards. Add one or move dashboards out of a group.
        </div>
        <ul v-else class="cr-dash-row-list">
          <li
            v-for="(dash, i) in navTree.standalones"
            :key="dash.slug"
            class="cr-dash-row"
            :class="{ 'cr-dash-row--default': dash.isDefault }"
          >
            <q-icon
              :name="dash.icon || 'space_dashboard'"
              size="18px"
              class="cr-dash-row-icon"
            />
            <div class="cr-dash-row-name">
              {{ dash.name }}
              <span v-if="dash.isDefault" class="cr-dash-row-pill">default</span>
            </div>
            <code class="cr-dash-row-slug">{{ dash.slug }}</code>
            <q-space />
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="arrow_upward"
              :disable="i === 0"
              :aria-label="`Move ${dash.name} up`"
              @click="moveDashboard(navTree.standalones, i, -1)"
            />
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="arrow_downward"
              :disable="i === navTree.standalones.length - 1"
              :aria-label="`Move ${dash.name} down`"
              @click="moveDashboard(navTree.standalones, i, 1)"
            />
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="edit"
              :aria-label="`Edit ${dash.name}`"
              @click="onRenameDashboard(dash)"
            >
              <q-tooltip>Rename / change icon / move to group</q-tooltip>
            </q-btn>
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="delete_outline"
              color="negative"
              :disable="dash.isDefault"
              :aria-label="`Delete ${dash.name}`"
              @click="onDeleteDashboard(dash)"
            >
              <q-tooltip v-if="dash.isDefault">
                The default dashboard can't be deleted.
              </q-tooltip>
              <q-tooltip v-else>Delete</q-tooltip>
            </q-btn>
          </li>
        </ul>
      </section>

      <!-- One card per group -->
      <section
        v-for="(group, gi) in navTree.groups"
        :key="group.slug"
        class="cr-dash-card"
        :class="{ 'cr-dash-card--default': group.isDefault }"
      >
        <header class="cr-dash-card-header">
          <q-icon
            :name="group.icon || 'folder'"
            size="18px"
            class="cr-dash-card-icon"
          />
          <h2 class="cr-dash-card-title">
            {{ group.name }}
            <span v-if="group.isDefault" class="cr-dash-row-pill">default</span>
          </h2>
          <code class="cr-dash-card-slug">{{ group.slug }}</code>
          <span class="cr-dash-card-count">{{ group.children.length }}</span>
          <q-space />
          <q-btn
            flat
            dense
            round
            size="sm"
            icon="arrow_upward"
            :disable="gi === 0"
            :aria-label="`Move group ${group.name} up`"
            @click="moveGroup(gi, -1)"
          />
          <q-btn
            flat
            dense
            round
            size="sm"
            icon="arrow_downward"
            :disable="gi === navTree.groups.length - 1"
            :aria-label="`Move group ${group.name} down`"
            @click="moveGroup(gi, 1)"
          />
          <q-btn
            flat
            dense
            round
            size="sm"
            icon="edit"
            :aria-label="`Rename group ${group.name}`"
            @click="onRenameGroup(group)"
          >
            <q-tooltip>Rename / change icon</q-tooltip>
          </q-btn>
          <q-btn
            flat
            dense
            round
            size="sm"
            icon="delete_outline"
            color="negative"
            :disable="group.isDefault || group.children.length > 0"
            :aria-label="`Delete group ${group.name}`"
            @click="onDeleteGroup(group)"
          >
            <q-tooltip v-if="group.isDefault">
              The default group can't be deleted.
            </q-tooltip>
            <q-tooltip v-else-if="group.children.length > 0">
              Move or delete the {{ group.children.length }} dashboard{{
                group.children.length === 1 ? '' : 's'
              }}
              inside first.
            </q-tooltip>
            <q-tooltip v-else>Delete group</q-tooltip>
          </q-btn>
          <q-btn
            flat
            dense
            round
            size="sm"
            icon="add"
            :aria-label="`Add dashboard to ${group.name}`"
            @click="onCreateDashboard(group.slug)"
          >
            <q-tooltip>Add dashboard to this group</q-tooltip>
          </q-btn>
        </header>

        <div v-if="group.children.length === 0" class="cr-dash-card-empty">
          No dashboards in this group yet.
        </div>
        <ul v-else class="cr-dash-row-list">
          <li
            v-for="(dash, i) in group.children"
            :key="dash.slug"
            class="cr-dash-row"
            :class="{ 'cr-dash-row--default': dash.isDefault }"
          >
            <q-icon
              :name="dash.icon || 'space_dashboard'"
              size="18px"
              class="cr-dash-row-icon"
            />
            <div class="cr-dash-row-name">
              {{ dash.name }}
              <span v-if="dash.isDefault" class="cr-dash-row-pill">default</span>
            </div>
            <code class="cr-dash-row-slug">{{ dash.slug }}</code>
            <q-space />
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="arrow_upward"
              :disable="i === 0"
              @click="moveDashboard(group.children, i, -1)"
            />
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="arrow_downward"
              :disable="i === group.children.length - 1"
              @click="moveDashboard(group.children, i, 1)"
            />
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="edit"
              @click="onRenameDashboard(dash)"
            />
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="delete_outline"
              color="negative"
              :disable="dash.isDefault"
              @click="onDeleteDashboard(dash)"
            />
          </li>
        </ul>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
/**
 * Workspace Settings → Dashboards. Lists every group + dashboard for the
 * active workspace, with rename / delete / up-down reorder controls.
 * The default Home dashboard + any seeded default group are protected
 * (delete button disabled, tooltip explains why).
 *
 * Reorder uses the existing position field on the update endpoints —
 * up/down arrow swaps with the adjacent sibling and PUTs the new
 * positions for both. No drag-and-drop in this revision (cleaner code,
 * accessible by default; can layer DnD on top later if needed).
 */
import { computed, onMounted, ref } from 'vue';
import { useQuasar } from 'quasar';
import { DashboardGroups, Dashboards } from 'src/api';
import type { DashboardNavGroup, DashboardNavItem } from 'src/api';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';
import { useDashboardsStore } from 'src/stores/dashboards';

const $q = useQuasar();
const { slug: workspaceSlug } = useActiveWorkspace();
const dashboardsStore = useDashboardsStore();

// Read straight from the dashboards store (same cache the icon bar in
// MainLayout consumes) so reorder/rename/delete mutations propagate to
// every surface that renders the nav tree without each surface
// re-fetching independently. `dashboardsStore.nav` is a reactive Map
// keyed by workspace slug; the computed re-evaluates whenever the
// store invalidates or refreshes the entry.
const navTree = computed(() =>
  workspaceSlug.value ? (dashboardsStore.nav.get(workspaceSlug.value) ?? null) : null,
);
const loading = ref(false);

async function reload() {
  if (!workspaceSlug.value) return;
  loading.value = true;
  try {
    await dashboardsStore.ensureNavTree(workspaceSlug.value, true);
  } finally {
    loading.value = false;
  }
}

// ─── Group operations ────────────────────────────────────────────────

function onCreateGroup() {
  $q.dialog({
    title: 'New dashboard group',
    message:
      'A group collapses dashboards under one icon-bar entry and shows them in a sub-sidebar. Slug becomes part of the URL.',
    prompt: {
      model: '',
      type: 'text',
      isValid: (v: string) => /^[a-z0-9](?:[a-z0-9-]{1,62}[a-z0-9])?$/.test(v.trim()),
      autofocus: true,
      placeholder: 'e.g. ops, analytics',
    },
    ok: { label: 'Next', color: 'primary', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
     
  }).onOk((slug: string) => {
    $q.dialog({
      title: `Group "${slug.trim()}"`,
      message: 'Display name + Material icon name (e.g. analytics, business_center, hub).',
      prompt: {
        model: slug.trim(),
        type: 'text',
        isValid: (v: string) => v.trim().length > 0,
        autofocus: true,
        placeholder: 'Display name',
      },
      ok: { label: 'Create', color: 'primary', unelevated: true, noCaps: true },
      cancel: { flat: true, noCaps: true },
      // eslint-disable-next-line @typescript-eslint/no-misused-promises
    }).onOk(async (name: string) => {
      if (!workspaceSlug.value) return;
      const res = await DashboardGroups.createDashboardGroup({
        path: { slug: workspaceSlug.value },
        body: {
          slug: slug.trim(),
          name: name.trim(),
          icon: 'folder',
          position: nextPosition(navTree.value?.groups ?? []),
        },
      });
      if (res.error) {
        $q.notify({
          type: 'negative',
          position: 'top',
          message: problemMessage(res.error) ?? 'Create failed.',
        });
        return;
      }
      $q.notify({ type: 'positive', position: 'top', message: `Created group "${name}".` });
      await reload();
    });
  });
}

function onRenameGroup(group: DashboardNavGroup) {
  $q.dialog({
    title: 'Rename group',
    message: `Slug stays "${group.slug}". Update the display name and/or icon.`,
    prompt: {
      model: group.name,
      type: 'text',
      isValid: (v: string) => v.trim().length > 0,
      autofocus: true,
    },
    ok: { label: 'Save', color: 'primary', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async (name: string) => {
    if (!workspaceSlug.value) return;
    const res = await DashboardGroups.updateDashboardGroup({
      path: { slug: workspaceSlug.value, groupSlug: group.slug },
      body: { name: name.trim(), icon: group.icon, position: Number(group.position) },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Rename failed.',
      });
      return;
    }
    $q.notify({ type: 'positive', position: 'top', message: 'Renamed.' });
    await reload();
  });
}

function onDeleteGroup(group: DashboardNavGroup) {
  if (group.isDefault || group.children.length > 0) return;
  $q.dialog({
    title: 'Delete group?',
    message: `Remove "${group.name}" from the icon bar. The group has no dashboards inside, so nothing else changes.`,
    ok: { label: 'Delete', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async () => {
    if (!workspaceSlug.value) return;
    const res = await DashboardGroups.deleteDashboardGroup({
      path: { slug: workspaceSlug.value, groupSlug: group.slug },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Delete failed.',
      });
      return;
    }
    $q.notify({ type: 'positive', position: 'top', message: `Deleted "${group.name}".` });
    await reload();
  });
}

async function moveGroup(index: number, direction: -1 | 1) {
  if (!workspaceSlug.value || !navTree.value) return;
  const groups = [...navTree.value.groups];
  const a = groups[index];
  const b = groups[index + direction];
  if (!a || !b) return;
  // Swap positions. Calling updateDashboardGroup with the swapped
  // numeric positions is the simplest "move by one" implementation —
  // both endpoints are idempotent and persist immediately.
  const aPos = Number(a.position);
  const bPos = Number(b.position);
  await Promise.all([
    DashboardGroups.updateDashboardGroup({
      path: { slug: workspaceSlug.value, groupSlug: a.slug },
      body: { name: a.name, icon: a.icon, position: bPos },
    }),
    DashboardGroups.updateDashboardGroup({
      path: { slug: workspaceSlug.value, groupSlug: b.slug },
      body: { name: b.name, icon: b.icon, position: aPos },
    }),
  ]);
  await reload();
}

// ─── Dashboard operations ────────────────────────────────────────────

function onCreateDashboard(groupSlug: string | null) {
  $q.dialog({
    title: groupSlug ? `New dashboard in "${groupSlug}"` : 'New standalone dashboard',
    message: 'Slug becomes part of the URL. Display name shows in the icon bar / sub-sidebar.',
    prompt: {
      model: '',
      type: 'text',
      isValid: (v: string) => /^[a-z0-9](?:[a-z0-9-]{1,62}[a-z0-9])?$/.test(v.trim()),
      autofocus: true,
      placeholder: 'e.g. metrics, alerts',
    },
    ok: { label: 'Next', color: 'primary', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
     
  }).onOk((slug: string) => {
    $q.dialog({
      title: `Dashboard "${slug.trim()}"`,
      message: 'Display name (Material icon optional via Edit afterward).',
      prompt: {
        model: slug.trim(),
        type: 'text',
        isValid: (v: string) => v.trim().length > 0,
        autofocus: true,
      },
      ok: { label: 'Create', color: 'primary', unelevated: true, noCaps: true },
      cancel: { flat: true, noCaps: true },
      // eslint-disable-next-line @typescript-eslint/no-misused-promises
    }).onOk(async (name: string) => {
      if (!workspaceSlug.value) return;
      const siblings = groupSlug
        ? (navTree.value?.groups.find((g) => g.slug === groupSlug)?.children ?? [])
        : (navTree.value?.standalones ?? []);
      const res = await Dashboards.createDashboard({
        path: { slug: workspaceSlug.value },
        body: {
          slug: slug.trim(),
          name: name.trim(),
          icon: null,
          groupSlug,
          position: nextPosition(siblings),
        },
      });
      if (res.error) {
        $q.notify({
          type: 'negative',
          position: 'top',
          message: problemMessage(res.error) ?? 'Create failed.',
        });
        return;
      }
      $q.notify({ type: 'positive', position: 'top', message: `Created "${name}".` });
      await reload();
    });
  });
}

function onRenameDashboard(dash: DashboardNavItem) {
  $q.dialog({
    title: 'Rename dashboard',
    message: `Slug stays "${dash.slug}". Update the display name.`,
    prompt: {
      model: dash.name,
      type: 'text',
      isValid: (v: string) => v.trim().length > 0,
      autofocus: true,
    },
    ok: { label: 'Save', color: 'primary', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async (name: string) => {
    if (!workspaceSlug.value) return;
    const res = await Dashboards.updateDashboard({
      path: { slug: workspaceSlug.value, dashSlug: dash.slug },
      body: {
        name: name.trim(),
        icon: dash.icon,
        groupSlug: null,
        position: null,
        layoutJson: null,
        widgetsJson: null,
      },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Rename failed.',
      });
      return;
    }
    $q.notify({ type: 'positive', position: 'top', message: 'Renamed.' });
    await reload();
  });
}

function onDeleteDashboard(dash: DashboardNavItem) {
  if (dash.isDefault) return;
  $q.dialog({
    title: 'Delete dashboard?',
    message: `Remove "${dash.name}" and its widget layout. This can't be undone.`,
    ok: { label: 'Delete', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async () => {
    if (!workspaceSlug.value) return;
    const res = await Dashboards.deleteDashboard({
      path: { slug: workspaceSlug.value, dashSlug: dash.slug },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Delete failed.',
      });
      return;
    }
    $q.notify({ type: 'positive', position: 'top', message: `Deleted "${dash.name}".` });
    await reload();
  });
}

async function moveDashboard(
  list: ReadonlyArray<DashboardNavItem>,
  index: number,
  direction: -1 | 1,
) {
  if (!workspaceSlug.value) return;
  const a = list[index];
  const b = list[index + direction];
  if (!a || !b) return;
  const aPos = Number(a.position);
  const bPos = Number(b.position);
  await Promise.all([
    Dashboards.updateDashboard({
      path: { slug: workspaceSlug.value, dashSlug: a.slug },
      body: {
        name: null,
        icon: null,
        groupSlug: null,
        position: bPos,
        layoutJson: null,
        widgetsJson: null,
      },
    }),
    Dashboards.updateDashboard({
      path: { slug: workspaceSlug.value, dashSlug: b.slug },
      body: {
        name: null,
        icon: null,
        groupSlug: null,
        position: aPos,
        layoutJson: null,
        widgetsJson: null,
      },
    }),
  ]);
  await reload();
}

// ─── Helpers ─────────────────────────────────────────────────────────

function nextPosition(siblings: ReadonlyArray<{ position: number | string }>): number {
  if (siblings.length === 0) return 100;
  const max = Math.max(...siblings.map((s) => Number(s.position)));
  return max + 100;
}

function problemMessage(err: unknown): string | undefined {
  if (err && typeof err === 'object') {
    const e = err as { detail?: unknown; title?: unknown };
    if (typeof e.detail === 'string' && e.detail.length) return e.detail;
    if (typeof e.title === 'string' && e.title.length) return e.title;
  }
  return undefined;
}

onMounted(reload);
</script>

<style lang="scss" scoped>
.cr-dash-settings {
  padding: 24px 32px 32px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.cr-dash-settings-header {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}

.cr-dash-settings-subhead {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--cr-fg-secondary);
  max-width: 760px;
}

.cr-dash-settings-loading {
  font-size: 12px;
  color: var(--cr-fg-tertiary);
  text-align: center;
  padding: 32px 16px;
}

.cr-dash-card {
  background: var(--cr-bg-surface);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 4px;
  overflow: hidden;
}

.cr-dash-card--default {
  border-left: 3px solid var(--q-primary);
}

.cr-dash-card-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  background: var(--cr-bg-elevated);
  border-bottom: 1px solid var(--cr-border-subtle);
  min-height: 0;
}

.cr-dash-card-icon {
  color: var(--cr-fg-secondary);
}

.cr-dash-card-title {
  font-size: 13px;
  font-weight: 600;
  line-height: 1.2;
  margin: 0;
  display: flex;
  align-items: center;
  gap: 6px;
}

.cr-dash-card-slug {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-surface);
  padding: 1px 4px;
  border-radius: 2px;
}

.cr-dash-card-count {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-surface);
  padding: 1px 6px;
  border-radius: 8px;
}

.cr-dash-card-empty {
  padding: 16px;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
  text-align: center;
}

.cr-dash-row-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.cr-dash-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--cr-border-subtle);

  &:last-child {
    border-bottom: none;
  }
}

.cr-dash-row--default {
  background: color-mix(in srgb, var(--q-primary), transparent 96%);
}

.cr-dash-row-icon {
  color: var(--cr-fg-secondary);
}

.cr-dash-row-name {
  font-size: 13px;
  font-weight: 500;
  color: var(--cr-fg-primary);
  display: flex;
  align-items: center;
  gap: 6px;
}

.cr-dash-row-slug {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-elevated);
  padding: 1px 4px;
  border-radius: 2px;
}

.cr-dash-row-pill {
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-elevated);
  padding: 1px 5px;
  border-radius: 2px;
}
</style>
