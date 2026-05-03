<template>
  <q-page class="cr-settings-page">
    <aside class="cr-settings-nav">
      <div class="cr-settings-nav-header">SETTINGS</div>
      <q-list>
        <q-item
          v-for="item in items"
          :key="item.route"
          clickable
          :to="item.route"
          :active="isActive(item.route)"
          active-class="cr-settings-nav-active"
          class="cr-settings-nav-item"
        >
          <q-item-section avatar>
            <q-icon :name="item.icon" size="18px" />
          </q-item-section>
          <q-item-section>
            <q-item-label class="cr-settings-nav-label">{{ item.label }}</q-item-label>
            <q-item-label caption class="cr-settings-nav-caption">
              {{ item.caption }}
            </q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
    </aside>
    <section class="cr-settings-content">
      <router-view />
    </section>
  </q-page>
</template>

<script setup lang="ts">
import { useRoute } from 'vue-router';

interface SettingsNavItem {
  icon: string;
  label: string;
  caption: string;
  route: string;
}

const route = useRoute();

const items: SettingsNavItem[] = [
  {
    icon: 'palette',
    label: 'Branding',
    caption: 'Logo, name, colors',
    route: '/settings/branding',
  },
  {
    icon: 'group',
    label: 'Users',
    caption: 'Accounts and roles',
    route: '/settings/users',
  },
  {
    icon: 'tune',
    label: 'Environment',
    caption: 'SMTP, API keys, base URL',
    route: '/settings/environment',
  },
  {
    icon: 'source',
    label: 'Workspaces',
    caption: 'Connected git / S3 sources',
    route: '/settings/workspaces',
  },
];

function isActive(target: string): boolean {
  return route.path === target || route.path.startsWith(target + '/');
}
</script>

<style lang="scss" scoped>
.cr-settings-page {
  display: flex;
  align-items: stretch;
  min-height: calc(100vh - var(--cr-header-height));
}

.cr-settings-nav {
  width: 240px;
  flex-shrink: 0;
  background: var(--cr-bg-sidebar);
  border-right: 1px solid var(--cr-border-subtle);
  padding: 16px 0;
}

.cr-settings-nav-header {
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.18em;
  color: var(--cr-fg-tertiary);
  padding: 0 16px 12px;
}

.cr-settings-nav-item {
  border-radius: 0;
  min-height: 48px;
  color: var(--cr-fg-secondary);

  &:hover {
    color: var(--cr-fg-primary);
    background: var(--cr-bg-hover);
  }
}

.cr-settings-nav-active {
  color: var(--q-primary) !important;
  background: var(--cr-brand-tint-soft) !important;

  .q-icon {
    color: var(--q-primary);
  }

  .cr-settings-nav-label {
    color: var(--q-primary);
  }
}

.cr-settings-nav-label {
  font-size: 13px;
  font-weight: 500;
}

.cr-settings-nav-caption {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
}

.cr-settings-content {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
}
</style>
