<template>
  <q-page class="cr-wssettings-page">
    <aside class="cr-wssettings-nav">
      <div class="cr-wssettings-nav-header">
        WORKSPACE
        <code v-if="slug" class="cr-wssettings-nav-slug">{{ slug }}</code>
      </div>
      <q-list>
        <q-item
          v-for="item in items"
          :key="item.route"
          clickable
          :to="item.route"
          :active="isActive(item.route)"
          active-class="cr-wssettings-nav-active"
          :disable="item.disabled"
          class="cr-wssettings-nav-item"
        >
          <q-item-section avatar>
            <q-icon :name="item.icon" size="18px" />
          </q-item-section>
          <q-item-section>
            <q-item-label class="cr-wssettings-nav-label">
              {{ item.label }}
              <span v-if="item.disabled" class="cr-wssettings-soon">soon</span>
            </q-item-label>
            <q-item-label caption class="cr-wssettings-nav-caption">
              {{ item.caption }}
            </q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
    </aside>
    <section class="cr-wssettings-content">
      <router-view />
    </section>
  </q-page>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useRoute } from 'vue-router';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';

interface NavItem {
  icon: string;
  label: string;
  caption: string;
  route: string;
  disabled?: boolean;
}

const route = useRoute();
const { slug } = useActiveWorkspace();

const items = computed<NavItem[]>(() => {
  const base = `/w/${slug.value ?? ''}/settings`;
  return [
    {
      icon: 'tune',
      label: 'General',
      caption: 'Name, description, sync schedule',
      route: `${base}/general`,
    },
    {
      icon: 'play_circle',
      label: 'Jobs',
      caption: 'Scripted automations + run history',
      route: `${base}/jobs`,
    },
    {
      icon: 'group',
      label: 'Members',
      caption: 'Editor / Viewer access',
      route: `${base}/members`,
      disabled: true,
    },
    {
      icon: 'extension',
      label: 'Plugins',
      caption: 'Enable plugins for this workspace',
      route: `${base}/plugins`,
    },
    {
      icon: 'schedule',
      label: 'Schedules',
      caption: 'Cron triggers, sync cadence',
      route: `${base}/schedules`,
    },
    {
      icon: 'folder',
      label: 'Files',
      caption: 'Browse, edit, create, delete files',
      route: `${base}/files`,
    },
    {
      icon: 'transform',
      label: 'Conventions',
      caption: 'Map files into typed entities',
      route: `${base}/conventions`,
    },
    {
      icon: 'dashboard',
      label: 'Dashboards',
      caption: 'Manage groups, dashboards, ordering',
      route: `${base}/dashboards`,
    },
    {
      icon: 'warning',
      label: 'Danger zone',
      caption: 'Delete this workspace',
      route: `${base}/danger`,
      disabled: true,
    },
  ];
});

function isActive(target: string): boolean {
  return route.path === target || route.path.startsWith(target + '/');
}
</script>

<style lang="scss" scoped>
.cr-wssettings-page {
  display: flex;
  align-items: stretch;
  min-height: calc(100vh - var(--cr-header-height));
}

.cr-wssettings-nav {
  width: 240px;
  flex-shrink: 0;
  background: var(--cr-bg-sidebar);
  border-right: 1px solid var(--cr-border-subtle);
  padding: 16px 0;
}

.cr-wssettings-nav-header {
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.18em;
  color: var(--cr-fg-tertiary);
  padding: 0 16px 12px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.cr-wssettings-nav-slug {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  font-weight: 500;
  letter-spacing: 0;
  text-transform: none;
  color: var(--cr-fg-secondary);
  background: var(--cr-bg-elevated);
  padding: 1px 6px;
  border-radius: 3px;
  width: max-content;
}

.cr-wssettings-nav-item {
  border-radius: 0;
  min-height: 48px;
  color: var(--cr-fg-secondary);

  &:hover {
    color: var(--cr-fg-primary);
    background: var(--cr-bg-hover);
  }
}

.cr-wssettings-nav-active {
  color: var(--q-primary) !important;
  background: var(--cr-brand-tint-soft) !important;

  .q-icon {
    color: var(--q-primary);
  }

  .cr-wssettings-nav-label {
    color: var(--q-primary);
  }
}

.cr-wssettings-nav-label {
  font-size: 13px;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 6px;
}

.cr-wssettings-soon {
  font-size: 9px;
  font-weight: 500;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  background: var(--cr-bg-elevated);
  padding: 1px 5px;
  border-radius: 3px;
}

.cr-wssettings-nav-caption {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
}

.cr-wssettings-content {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
}
</style>
