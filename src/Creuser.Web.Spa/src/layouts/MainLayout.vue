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
        <div class="cr-brand" :title="productName">
          <span class="cr-brand-name">{{ productName }}</span>
        </div>
        <q-space />
        <!-- TODO: global Cmd+K command palette. Lands once /api/search is implemented. -->
        <q-btn
          flat
          dense
          round
          :icon="assistant.isOpen ? 'auto_awesome' : 'auto_awesome'"
          :color="assistant.isOpen ? 'primary' : undefined"
          aria-label="Toggle AI assistant"
          class="cr-assistant-btn"
          @click="assistant.toggle"
        >
          <q-tooltip anchor="bottom right" self="top right">
            {{ assistant.isOpen ? 'Close assistant' : 'Open assistant' }}
          </q-tooltip>
        </q-btn>
        <ThemeModeToggle />
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
        <router-link
          to="/"
          class="cr-logo-container"
          :aria-label="`${productName} home`"
          @click="onNavClick"
        >
          <img v-if="logoUrl" :src="logoUrl" :alt="productName" class="cr-logo-image" />
          <div v-else class="cr-logo">{{ productInitial }}</div>
          <q-tooltip
            v-if="!$q.screen.lt.md"
            anchor="center right"
            self="center left"
            :offset="[8, 0]"
          >
            Home — {{ productName }}
          </q-tooltip>
        </router-link>

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

    <AssistantPanel />

    <q-page-container>
      <router-view />
    </q-page-container>
  </q-layout>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useQuasar, useMeta } from 'quasar';
import { useAssistantStore } from 'stores/assistant';
import { useAuthStore } from 'stores/auth';
import { useBrandingStore } from 'stores/branding';
import AssistantPanel from 'components/AssistantPanel.vue';
import ThemeModeToggle from 'components/ThemeModeToggle.vue';

const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const branding = useBrandingStore();
const assistant = useAssistantStore();
const drawer = ref(false);

const productName = computed(() => branding.productName);
const logoUrl = computed(() => branding.logoUrl);
const productInitial = computed(() => branding.productName.charAt(0).toUpperCase() || 'C');

interface NavItem {
  icon: string;
  label: string;
  route: string;
}

// Top icons today: just Home. The workspace-scoped icon bar (dashboard
// groups + standalone dashboards + workspace settings) lands once
// /w/:slug/... routing exists. See architecture.md "Workspace navigation".
const topNavItems: NavItem[] = [{ icon: 'home', label: 'Home', route: '/' }];

const bottomNavItems = computed(() => {
  const items: NavItem[] = [];
  if (auth.isAdmin) {
    items.push({ icon: 'settings', label: 'Platform settings', route: '/settings' });
  }
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
  title: `${currentPage.value?.label ?? productName.value} · ${productName.value}`,
}));

function onNavClick() {
  if ($q.screen.lt.md) drawer.value = false;
}

async function logout() {
  await auth.logout();
  await router.push({ name: 'login' });
}
</script>

<style lang="scss" scoped>
.cr-header {
  background: var(--cr-bg-header);
  height: var(--cr-header-height);
}

.cr-toolbar {
  min-height: 0;
  height: 100%;
  padding: 0 12px;
  gap: 12px;
}

.cr-brand {
  display: flex;
  align-items: center;
  min-width: 0;
}

.cr-brand-name {
  font-size: 14px;
  font-weight: 700;
  letter-spacing: 0.06em;
  color: var(--cr-fg-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.cr-drawer {
  background: var(--cr-bg-sidebar) !important;
  border-color: var(--cr-border-subtle) !important;
}

.cr-drawer-content {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.cr-logo-container {
  height: var(--cr-header-height);
  display: flex;
  align-items: center;
  justify-content: center;
  // Defaults to --cr-border-default to match the header's bordered bottom
  // (both inherit from the same fallback). Independently overridable via
  // `:root { --cr-border-logo: ...; }` in Custom CSS.
  border-bottom: 1px solid var(--cr-border-logo);
  // Clickable link to /; reset default anchor styling so the logo art is the
  // only thing the user sees.
  text-decoration: none;
  color: inherit;
  transition: background 120ms ease;

  &:hover {
    background: var(--cr-bg-hover);
  }

  &:focus-visible {
    outline: 2px solid var(--q-primary);
    outline-offset: -2px;
  }
}

.cr-logo {
  width: 24px;
  height: 24px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 50%;
  background: var(--q-primary);
  color: var(--cr-fg-on-brand);
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.05em;
}

.cr-logo-image {
  width: 24px;
  height: 24px;
  object-fit: contain;
  border-radius: 4px;
}

.cr-nav-item {
  border-radius: 0;
  min-height: 44px;
  color: var(--cr-fg-secondary);

  .q-icon {
    font-size: 20px;
  }

  &:hover {
    color: var(--cr-fg-primary);
    background: var(--cr-bg-hover);
  }
}

.cr-nav-active {
  color: var(--q-primary) !important;
  background: var(--cr-brand-tint-soft) !important;

  .q-icon {
    color: var(--q-primary);
  }
}
</style>
