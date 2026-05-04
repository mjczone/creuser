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
        <span class="cr-brand-divider" aria-hidden="true">/</span>
        <WorkspacePicker v-if="auth.isAuthenticated" />
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

          <q-item
            v-if="canCreateDashboards"
            clickable
            class="cr-nav-item"
            data-testid="cr-add-dashboard"
          >
            <q-item-section avatar>
              <q-icon name="add" />
              <q-tooltip
                v-if="!$q.screen.lt.md"
                anchor="center right"
                self="center left"
                :offset="[8, 0]"
              >
                New dashboard / group
              </q-tooltip>
            </q-item-section>
            <q-item-section>New…</q-item-section>
            <q-menu anchor="center right" self="center left" :offset="[8, 0]">
              <q-list dense>
                <q-item clickable v-close-popup @click="openCreateDashboard">
                  <q-item-section avatar>
                    <q-icon name="dashboard" />
                  </q-item-section>
                  <q-item-section>
                    <q-item-label>New dashboard</q-item-label>
                    <q-item-label caption>Standalone or in a group</q-item-label>
                  </q-item-section>
                </q-item>
                <q-item clickable v-close-popup @click="openCreateGroup">
                  <q-item-section avatar>
                    <q-icon name="folder" />
                  </q-item-section>
                  <q-item-section>
                    <q-item-label>New group</q-item-label>
                    <q-item-label caption>Folder for related dashboards</q-item-label>
                  </q-item-section>
                </q-item>
              </q-list>
            </q-menu>
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
import { computed, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useQuasar, useMeta } from 'quasar';
import { useAssistantStore } from 'stores/assistant';
import { useAuthStore } from 'stores/auth';
import { useBrandingStore } from 'stores/branding';
import { useDashboardsStore } from 'stores/dashboards';
import AssistantPanel from 'components/AssistantPanel.vue';
import ThemeModeToggle from 'components/ThemeModeToggle.vue';
import WorkspacePicker from 'components/WorkspacePicker.vue';
import CreateDashboardDialog from 'components/CreateDashboardDialog.vue';
import CreateDashboardGroupDialog from 'components/CreateDashboardGroupDialog.vue';

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

// Whether the current route is rooted at /w/:slug/... — drives the icon-bar
// variant. The workspace-scoped variant has Home pointing to the workspace
// home and adds a Workspace Settings icon above Platform Settings.
const isWorkspaceScoped = computed(() =>
  route.matched.some((r) => r.meta.workspaceScoped === true),
);
const activeSlug = computed<string | null>(() =>
  typeof route.params.workspaceSlug === 'string' ? route.params.workspaceSlug : null,
);

// Pull dashboards on workspace entry so the icon bar reflects per-workspace
// state. The store caches per slug, so re-entering the same workspace doesn't
// re-fetch.
const dashboardsStore = useDashboardsStore();
watch(
  activeSlug,
  (slug) => {
    if (slug) void dashboardsStore.ensureNavTree(slug);
  },
  { immediate: true },
);

const navTree = computed(() =>
  activeSlug.value ? dashboardsStore.getNavTree(activeSlug.value) : null,
);

const topNavItems = computed<NavItem[]>(() => {
  if (isWorkspaceScoped.value && activeSlug.value) {
    const items: NavItem[] = [];
    // Home is special — always present, always first. Routes to the seeded
    // Home dashboard (`/w/:slug/d/home`) when one exists in the nav tree;
    // falls back to the workspace metadata page (`/w/:slug`) for workspaces
    // without a Home dashboard yet (e.g. before the backfill service has
    // run on a fresh deployment).
    const hasHomeDashboard = navTree.value?.standalones.some((d) => d.slug === 'home');
    items.push({
      icon: 'home',
      label: 'Home',
      route: hasHomeDashboard ? `/w/${activeSlug.value}/d/home` : `/w/${activeSlug.value}`,
    });

    if (navTree.value) {
      // Standalone dashboards (excluding the Home one we already added).
      for (const dash of navTree.value.standalones) {
        if (dash.slug === 'home') continue;
        items.push({
          icon: dash.icon ?? 'dashboard',
          label: dash.name,
          route: `/w/${activeSlug.value}/d/${dash.slug}`,
        });
      }
      // Group icons — always route to the group landing page
      // (`/w/:slug/g/:groupSlug`). That page redirects to the group's first
      // child when one exists, or renders an empty-state CTA when the group
      // has no dashboards yet. Sub-sidebar UI lands in a follow-up pass.
      for (const grp of navTree.value.groups) {
        items.push({
          icon: grp.icon,
          label: grp.name,
          route: `/w/${activeSlug.value}/g/${grp.slug}`,
        });
      }
    }
    return items;
  }
  return [{ icon: 'home', label: 'Home', route: '/' }];
});

const bottomNavItems = computed(() => {
  const items: NavItem[] = [];
  if (isWorkspaceScoped.value && activeSlug.value && auth.isAdmin) {
    // Workspace Settings sits above Platform Settings — closer to the
    // workspace dashboards, since it's where workspace admins live.
    items.push({
      icon: 'settings_applications',
      label: 'Workspace settings',
      route: `/w/${activeSlug.value}/settings`,
    });
  }
  if (auth.isAdmin) {
    items.push({ icon: 'settings', label: 'Platform settings', route: '/settings' });
  }
  return items;
});

const allNavItems = computed(() => [...topNavItems.value, ...bottomNavItems.value]);

function isNavActive(navRoute: string): boolean {
  // Exact match for top-level Home routes (`/` and `/w/:slug`); prefix match
  // for everything else so settings sub-pages stay highlighted.
  if (navRoute === '/') return route.path === '/';
  if (navRoute.match(/^\/w\/[^/]+$/)) return route.path === navRoute;
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

// "+" affordance is admin-only (mutations are admin-gated server-side)
// and only meaningful inside a workspace context. Hidden everywhere else.
const canCreateDashboards = computed(
  () => isWorkspaceScoped.value && !!activeSlug.value && auth.isAdmin,
);

function openCreateDashboard() {
  if (!activeSlug.value) return;
  const groups = navTree.value?.groups.map((g) => ({ slug: g.slug, name: g.name })) ?? [];
  $q.dialog({
    component: CreateDashboardDialog,
    componentProps: { workspaceSlug: activeSlug.value, groups },
  }).onOk((created: { slug?: string } | null) => {
    void onDashboardCreated(created);
  });
}

async function onDashboardCreated(created: { slug?: string } | null) {
  if (!activeSlug.value) return;
  await dashboardsStore.ensureNavTree(activeSlug.value, true);
  if (created?.slug) {
    await router.push(`/w/${activeSlug.value}/d/${created.slug}`);
  }
}

function openCreateGroup() {
  if (!activeSlug.value) return;
  $q.dialog({
    component: CreateDashboardGroupDialog,
    componentProps: { workspaceSlug: activeSlug.value },
  }).onOk(() => {
    void onGroupCreated();
  });
}

async function onGroupCreated() {
  if (!activeSlug.value) return;
  await dashboardsStore.ensureNavTree(activeSlug.value, true);
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

.cr-brand-divider {
  color: var(--cr-fg-tertiary);
  font-size: 16px;
  font-weight: 300;
  user-select: none;
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
