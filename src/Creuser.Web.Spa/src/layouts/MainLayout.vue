<template>
  <q-layout view="lHh Lpr lFf">
    <q-header class="cr-header" bordered>
      <q-toolbar class="cr-toolbar">
        <q-btn
          flat
          dense
          round
          icon="menu"
          class="lt-md"
          aria-label="Menu"
          @click="drawer = !drawer"
        />
        <div class="cr-breadcrumb">
          <span class="cr-breadcrumb-label">{{ currentPage?.label }}</span>
        </div>
        <q-space />
        <!-- TODO: global Cmd+K command palette. Lands once /api/search is implemented. -->
        <span class="cr-app-title">CREUSER</span>
      </q-toolbar>
    </q-header>

    <q-drawer
      v-model="drawer"
      :mini="!$q.screen.lt.md"
      :mini-width="60"
      :width="200"
      :overlay="$q.screen.lt.md"
      :show-if-above="!$q.screen.lt.md"
      bordered
      class="cr-drawer"
    >
      <div class="cr-drawer-content">
        <div class="cr-logo-container">
          <!-- Placeholder logo. Branding doc supplies the real SVG/PNG at runtime. -->
          <div class="cr-logo">C</div>
        </div>

        <q-list>
          <q-item
            v-for="nav in topNavItems"
            :key="nav.route"
            clickable
            :to="nav.route"
            :active="isNavActive(nav.route)"
            active-class="cr-nav-active"
            class="cr-nav-item"
            @click="onNavClick"
          >
            <q-item-section avatar>
              <q-icon :name="nav.icon" />
              <q-tooltip
                v-if="!$q.screen.lt.md"
                anchor="center right"
                self="center left"
                :offset="[8, 0]"
              >
                {{ nav.label }}
              </q-tooltip>
            </q-item-section>
            <q-item-section>
              {{ nav.label }}
            </q-item-section>
          </q-item>
        </q-list>

        <q-space />

        <q-list>
          <q-item
            v-for="nav in bottomNavItems"
            :key="nav.route"
            clickable
            :to="nav.route"
            :active="isNavActive(nav.route)"
            active-class="cr-nav-active"
            class="cr-nav-item"
            @click="onNavClick"
          >
            <q-item-section avatar>
              <q-icon :name="nav.icon" />
              <q-tooltip
                v-if="!$q.screen.lt.md"
                anchor="center right"
                self="center left"
                :offset="[8, 0]"
              >
                {{ nav.label }}
              </q-tooltip>
            </q-item-section>
            <q-item-section>
              {{ nav.label }}
            </q-item-section>
          </q-item>

          <q-item v-if="auth.isAuthenticated" clickable class="cr-nav-item" @click="logout">
            <q-item-section avatar>
              <q-icon name="logout" />
              <q-tooltip
                v-if="!$q.screen.lt.md"
                anchor="center right"
                self="center left"
                :offset="[8, 0]"
              >
                Logout
              </q-tooltip>
            </q-item-section>
            <q-item-section> Logout </q-item-section>
          </q-item>
        </q-list>
      </div>
    </q-drawer>

    <q-page-container>
      <router-view />
    </q-page-container>
  </q-layout>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useQuasar, useMeta } from 'quasar';
import { useAuthStore } from 'stores/auth';

const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const drawer = ref(false);

interface NavItem {
  icon: string;
  label: string;
  route: string;
}

const topNavItems: NavItem[] = [
  { icon: 'dashboard', label: 'Dashboard', route: '/' },
  { icon: 'source', label: 'Workspaces', route: '/workspaces' },
  { icon: 'account_tree', label: 'Workflows', route: '/workflows' },
  { icon: 'history', label: 'Runs', route: '/runs' },
  { icon: 'description', label: 'Scripts', route: '/scripts' },
  { icon: 'smart_toy', label: 'Agents', route: '/agents' },
  { icon: 'extension', label: 'Plugins', route: '/plugins' },
];

const bottomNavItems = computed(() => {
  const items: NavItem[] = [];
  if (auth.isAdmin) {
    items.push({ icon: 'admin_panel_settings', label: 'Users', route: '/admin/users' });
  }
  items.push({ icon: 'settings', label: 'Settings', route: '/settings' });
  return items;
});

const allNavItems = computed(() => [...topNavItems, ...bottomNavItems.value]);

function isNavActive(navRoute: string): boolean {
  if (navRoute === '/') return route.path === '/';
  return route.path === navRoute || route.path.startsWith(navRoute + '/');
}

const currentPage = computed(
  () => allNavItems.value.find((n) => isNavActive(n.route)) ?? allNavItems.value[0],
);

useMeta(() => ({
  title: `${currentPage.value?.label ?? 'Creuser'} · Creuser`,
}));

function onNavClick() {
  if ($q.screen.lt.md) drawer.value = false;
}

async function logout() {
  // TODO: POST /api/auth/logout once the endpoint exists.
  auth.clearUser();
  await router.push('/');
}
</script>

<style lang="scss" scoped>
.cr-header {
  background: $dark;
  height: 40px;
}

.cr-toolbar {
  min-height: 0;
  height: 100%;
  padding: 0 12px;
}

.cr-breadcrumb {
  display: flex;
  align-items: center;
  gap: 6px;
}

.cr-breadcrumb-label {
  font-size: 13px;
  font-weight: 500;
  color: rgba(255, 255, 255, 0.7);
}

.cr-app-title {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.15em;
  color: rgba(255, 255, 255, 0.3);
}

.cr-drawer {
  background: $dark !important;
  border-color: rgba(255, 255, 255, 0.08) !important;
}

.cr-drawer-content {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.cr-logo-container {
  height: 40px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.cr-logo {
  width: 24px;
  height: 24px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 50%;
  background: $primary;
  color: white;
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.05em;
}

.cr-nav-item {
  border-radius: 0;
  min-height: 44px;
  color: rgba(255, 255, 255, 0.5);

  .q-icon {
    font-size: 20px;
  }

  &:hover {
    color: rgba(255, 255, 255, 0.8);
    background: rgba(255, 255, 255, 0.04);
  }
}

.cr-nav-active {
  color: $primary !important;
  background: rgba($primary, 0.08) !important;

  .q-icon {
    color: $primary;
  }
}
</style>
