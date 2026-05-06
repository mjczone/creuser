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
        <!--
          Sub-sidebar collapse toggle. Hidden on mobile (the icon-bar drawer
          itself collapses to an overlay there, making this redundant) and
          when the active route has no sub-sidebar to toggle (standalone
          dashboards, settings pages, etc.). Default is "open"; clicking
          closes the sub-sidebar to give the dock area more horizontal real
          estate. The icon hints at the resulting motion: chevron-left when
          open (click to slide it shut), chevron-right when closed (click to
          bring it back).
        -->
        <q-btn
          v-if="canToggleSubSidebar"
          flat
          dense
          round
          size="sm"
          :icon="subSidebarOpen ? 'chevron_left' : 'chevron_right'"
          :aria-label="subSidebarOpen ? 'Hide sub-sidebar' : 'Show sub-sidebar'"
          class="cr-sub-sidebar-toggle"
          @click="subSidebarOpen = !subSidebarOpen"
        >
          <q-tooltip anchor="bottom left" self="top left">
            {{ subSidebarOpen ? 'Hide sidebar' : 'Show sidebar' }}
          </q-tooltip>
        </q-btn>
        <div class="cr-brand" :title="productName">
          <span class="cr-brand-name">{{ productName }}</span>
        </div>
        <span class="cr-brand-divider" aria-hidden="true">/</span>
        <WorkspacePicker v-if="auth.isAuthenticated" />
        <q-space />
        <!-- TODO: global Cmd+K command palette. Lands once /api/search is implemented. -->
        <WorkspaceActions v-if="auth.isAuthenticated" />
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
          <template v-for="nav in topNavItems" :key="nav.kind === 'group' ? nav.slug : nav.route">
            <!--
              Mobile: render group icons as expansion items. The desktop
              icon-bar is mini-mode (60px, icons only) and pairs with the
              sub-sidebar for sibling navigation; mobile has no sub-sidebar
              (the icon-bar drawer is itself an overlay) so the group's
              children would otherwise be unreachable. Expanding the group
              inline gives the same "icon bar → group → child" flow.
              `default-opened` keeps the active group's children visible
              after navigation so the user sees where they are without
              re-tapping the group.
            -->
            <q-expansion-item
              v-if="nav.kind === 'group' && $q.screen.lt.md && nav.children.length > 0"
              :icon="nav.icon"
              :label="nav.label"
              :default-opened="isNavActive(nav.route)"
              header-class="cr-nav-item cr-nav-group-header"
              expand-icon-class="cr-nav-group-expand"
              :class="{ 'cr-nav-active': isNavActive(nav.route) }"
            >
              <q-item
                v-for="child in nav.children"
                :key="child.slug"
                clickable
                :to="`/w/${activeSlug}/d/${child.slug}`"
                :active="isDashRouteActive(child.slug)"
                active-class="cr-nav-active"
                class="cr-nav-item cr-nav-group-child"
                @click="onNavClick"
              >
                <q-item-section avatar>
                  <q-icon :name="child.icon ?? 'dashboard'" />
                </q-item-section>
                <q-item-section>
                  {{ child.name }}
                </q-item-section>
              </q-item>
            </q-expansion-item>
            <q-item
              v-else
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
          </template>

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

    <!--
      Sub-sidebar — third tier of the workspace shell (icon bar → optional
      sub-sidebar → page content). Renders the children of whichever group
      the active dashboard belongs to (or the group explicitly selected via
      `/w/:slug/g/:groupSlug`). Hidden on narrow viewports where the icon
      bar already collapses to an overlay drawer.
    -->
    <aside
      v-if="showSubSidebar && activeGroup"
      class="cr-sub-sidebar"
      :aria-label="`${activeGroup.name} dashboards`"
    >
      <header class="cr-sub-sidebar-header">
        <q-icon :name="activeGroup.icon ?? 'folder'" size="16px" />
        <span class="cr-sub-sidebar-title">{{ activeGroup.name }}</span>
      </header>
      <q-list class="cr-sub-sidebar-list">
        <q-item
          v-for="child in activeGroup.children"
          :key="child.slug"
          clickable
          :to="`/w/${activeSlug}/d/${child.slug}`"
          :active="isDashRouteActive(child.slug)"
          active-class="cr-sub-sidebar-item-active"
          class="cr-sub-sidebar-item"
        >
          <q-item-section avatar class="cr-sub-sidebar-icon-section">
            <q-icon :name="child.icon ?? 'dashboard'" size="16px" />
          </q-item-section>
          <q-item-section>
            <q-item-label class="cr-sub-sidebar-item-label">{{ child.name }}</q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
    </aside>

    <q-page-container :class="{ 'cr-page-container--with-sub': showSubSidebar }">
      <router-view />
    </q-page-container>
  </q-layout>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useQuasar, useMeta } from 'quasar';
import { useLocalStorage } from '@vueuse/core';
import { useAssistantStore } from 'stores/assistant';
import { useAuthStore } from 'stores/auth';
import { useBrandingStore } from 'stores/branding';
import { useDashboardsStore } from 'stores/dashboards';
import { useWorkspaceStatusStore } from 'stores/workspaceStatus';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';
import AssistantPanel from 'components/AssistantPanel.vue';
import ThemeModeToggle from 'components/ThemeModeToggle.vue';
import WorkspacePicker from 'components/WorkspacePicker.vue';
import WorkspaceActions from 'components/workspace/WorkspaceActions.vue';
import CreateDashboardDialog from 'components/CreateDashboardDialog.vue';
import CreateDashboardGroupDialog from 'components/CreateDashboardGroupDialog.vue';

const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const branding = useBrandingStore();
const assistant = useAssistantStore();
const workspaceStatus = useWorkspaceStatusStore();
const { slug: activeWorkspaceSlug } = useActiveWorkspace();
const drawer = ref(false);

// Keep the workspace-status store synced to whichever workspace the
// route is currently scoped to. Drives the header's Commit/Push buttons
// (visibility + badges) and is fed live updates via SignalR by the
// store itself. When the user leaves a workspace-scoped route, slug
// becomes null and the store tears down its subscription.
watch(
  activeWorkspaceSlug,
  (next) => {
    void workspaceStatus.setActive(next);
  },
  { immediate: true },
);

const productName = computed(() => branding.productName);
const logoUrl = computed(() => branding.effectiveLogoUrl);
const productInitial = computed(() => branding.productName.charAt(0).toUpperCase() || 'C');

interface NavLeaf {
  kind: 'leaf';
  icon: string;
  label: string;
  route: string;
}

interface NavGroup {
  kind: 'group';
  icon: string;
  label: string;
  slug: string;
  // Route to the group landing page — used for active-state matching and
  // as the desktop icon-bar's click target. On mobile (expansion mode) we
  // navigate via the children directly, so this route isn't clicked.
  route: string;
  children: Array<{ slug: string; name: string; icon: string | null }>;
}

type NavItem = NavLeaf | NavGroup;

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

// Sub-sidebar — the optional third tier of the workspace shell. Visible
// when the active route is either (a) a group landing page
// (`/w/:slug/g/:groupSlug`), or (b) a dashboard that belongs to a group
// (so siblings within the group are reachable). Returns `null` for
// standalone dashboards and non-workspace routes.
const activeGroup = computed(() => {
  if (!isWorkspaceScoped.value || !navTree.value) return null;
  const groupSlugParam = typeof route.params.groupSlug === 'string' ? route.params.groupSlug : null;
  if (groupSlugParam) {
    return navTree.value.groups.find((g) => g.slug === groupSlugParam) ?? null;
  }
  const dashSlugParam =
    typeof route.params.dashboardSlug === 'string' ? route.params.dashboardSlug : null;
  if (dashSlugParam) {
    for (const grp of navTree.value.groups) {
      if (grp.children.some((c) => c.slug === dashSlugParam)) return grp;
    }
  }
  return null;
});
// User-toggleable visibility of the sub-sidebar. Default open; persisted
// in localStorage so the choice survives reloads. Independent of whether
// the current route has a sub-sidebar to show — when `activeGroup` is null
// the sub-sidebar is hidden regardless.
const subSidebarOpen = useLocalStorage<boolean>('creuser.layout.subSidebarOpen', true);
const canToggleSubSidebar = computed(() => !!activeGroup.value && !$q.screen.lt.md);
const showSubSidebar = computed(
  () => !!activeGroup.value && !$q.screen.lt.md && subSidebarOpen.value,
);
function isDashRouteActive(dashSlug: string): boolean {
  return route.params.dashboardSlug === dashSlug;
}

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
      kind: 'leaf',
      icon: 'home',
      label: 'Home',
      route: hasHomeDashboard ? `/w/${activeSlug.value}/d/home` : `/w/${activeSlug.value}`,
    });

    if (navTree.value) {
      // Standalone dashboards (excluding the Home one we already added).
      for (const dash of navTree.value.standalones) {
        if (dash.slug === 'home') continue;
        items.push({
          kind: 'leaf',
          icon: dash.icon ?? 'dashboard',
          label: dash.name,
          route: `/w/${activeSlug.value}/d/${dash.slug}`,
        });
      }
      // Group entries — desktop renders these as plain icons that route to
      // `/w/:slug/g/:groupSlug` (which redirects to the group's first child
      // and triggers the sub-sidebar). Mobile renders them as
      // `q-expansion-item`s with their children inline, since there's no
      // sub-sidebar in the mobile shell.
      for (const grp of navTree.value.groups) {
        items.push({
          kind: 'group',
          icon: grp.icon,
          label: grp.name,
          slug: grp.slug,
          route: `/w/${activeSlug.value}/g/${grp.slug}`,
          children: grp.children.map((c) => ({
            slug: c.slug,
            name: c.name,
            icon: c.icon,
          })),
        });
      }
    }
    return items;
  }
  return [{ kind: 'leaf', icon: 'home', label: 'Home', route: '/' }];
});

const bottomNavItems = computed<NavLeaf[]>(() => {
  const items: NavLeaf[] = [];
  if (isWorkspaceScoped.value && activeSlug.value && auth.isAdmin) {
    // Workspace Settings sits above Platform Settings — closer to the
    // workspace dashboards, since it's where workspace admins live.
    items.push({
      kind: 'leaf',
      icon: 'settings_applications',
      label: 'Workspace settings',
      route: `/w/${activeSlug.value}/settings`,
    });
  }
  if (auth.isAdmin) {
    items.push({
      kind: 'leaf',
      icon: 'settings',
      label: 'Platform settings',
      route: '/settings',
    });
  }
  return items;
});

const allNavItems = computed(() => [...topNavItems.value, ...bottomNavItems.value]);

function isNavActive(navRoute: string): boolean {
  // Exact match for top-level Home routes (`/` and `/w/:slug`); prefix match
  // for everything else so settings sub-pages stay highlighted.
  if (navRoute === '/') return route.path === '/';
  if (navRoute.match(/^\/w\/[^/]+$/)) return route.path === navRoute;
  // Group icons highlight when their landing page is the active route AND
  // when any child dashboard of the group is the active route. Without
  // this, navigating to a sibling via the sub-sidebar drops the group
  // icon's selected state since the URL flips to /d/:childSlug.
  const groupMatch = navRoute.match(/^\/w\/[^/]+\/g\/([^/]+)$/);
  if (groupMatch) {
    if (route.path === navRoute) return true;
    return activeGroup.value?.slug === groupMatch[1];
  }
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

// Sub-sidebar — third tier of the workspace shell. Sits adjacent to the
// 60px icon-bar drawer, extending right by 200px. Position-fixed so it
// stays out of Quasar's layout calculations (q-layout's drawer slots
// only support a single left drawer; we add this as a sibling instead).
// Padding for q-page-container is bumped via `cr-page-container--with-sub`
// — using `padding-left` (not `margin-left`) because Quasar already drives
// q-page-container's padding-left from the drawer width via inline style,
// so we pile our additional offset on top via a CSS rule.
.cr-sub-sidebar {
  position: fixed;
  top: var(--cr-header-height);
  bottom: 0;
  left: var(--cr-icon-bar-width);
  width: var(--cr-drawer-width);
  z-index: 1000;
  background: var(--cr-bg-sidebar);
  border-right: 1px solid var(--cr-border-subtle);
  display: flex;
  flex-direction: column;
  overflow-y: auto;
}

.cr-sub-sidebar-header {
  display: flex;
  align-items: center;
  gap: 8px;
  // Same fixed height as `.cr-dash-header` (DashboardPage.vue) so the two
  // bottom borders sit at the same Y and read as one continuous horizontal
  // divider across the workspace shell.
  min-height: 44px;
  padding: 0 14px;
  font-size: 12px;
  font-weight: 600;
  line-height: 1.3;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  border-bottom: 1px solid var(--cr-border-subtle);
}

.cr-sub-sidebar-title {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.cr-sub-sidebar-list {
  padding: 4px 0;
}

.cr-sub-sidebar-item {
  min-height: 32px;
  padding: 4px 10px 4px 14px;
  color: var(--cr-fg-secondary);

  .q-icon {
    color: var(--cr-fg-tertiary);
  }

  &:hover {
    background: var(--cr-bg-hover);
    color: var(--cr-fg-primary);
  }
}

.cr-sub-sidebar-icon-section {
  min-width: 24px;
  padding-right: 8px;
}

.cr-sub-sidebar-item-label {
  font-size: 13px;
  line-height: 1.3;
}

.cr-sub-sidebar-item-active {
  background: var(--cr-brand-tint-soft);
  color: var(--cr-fg-primary);

  .q-icon {
    color: var(--q-primary);
  }
}

.cr-page-container--with-sub {
  // Quasar sets padding-left from the drawer width inline; we add the
  // sub-sidebar's width on top via a CSS rule. The `+ var(--cr-icon-bar-width)`
  // mirrors what Quasar already added so the total left offset is
  // (icon bar) + (sub-sidebar) regardless of which one Quasar's offset accounts for.
  padding-left: calc(var(--cr-icon-bar-width) + var(--cr-drawer-width)) !important;
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

// Mobile: groups render as q-expansion-item. Style the chevron + indent
// the children so the structure reads like a tree.
.cr-nav-group-expand {
  color: var(--cr-fg-tertiary);
}

.cr-nav-group-child {
  // Indent enough to align the child icon with the group icon's right
  // edge, hinting parent → child relationship.
  padding-left: 32px;
  min-height: 36px;

  .q-icon {
    font-size: 16px;
  }
}

// Mobile-only density bump. Desktop keeps the icon-bar drawer in mini
// mode (60px, icon-only) where the 20px icons read at a comfortable size.
// Mobile shows the full drawer at 200px+ with labels — at that scale the
// 44px row + 20px icon eats too much vertical space for an operational
// dashboard, so shrink both. `:deep(.q-drawer:not(.q-drawer--mini))`
// reaches across the scoped boundary to target the drawer in its full
// (non-mini) state, which only happens on mobile per the
// `:mini="!$q.screen.lt.md"` binding above.
:deep(.q-drawer:not(.q-drawer--mini)) {
  .cr-nav-item {
    min-height: 36px;
    font-size: 13px;

    .q-icon {
      font-size: 16px;
    }
  }

  .cr-nav-group-child {
    min-height: 32px;
    padding-left: 28px;

    .q-icon {
      font-size: 14px;
    }
  }

  // Quasar's avatar slot defaults to ~56px wide to fit user-photo
  // avatars; we render 14–16px icons there, so that whole slot is wasted
  // space. Shrink to 32px so labels start closer to the icon and rows
  // breathe at the new compact density.
  .q-item__section--avatar {
    min-width: 32px;
    padding-right: 8px;
  }
}
</style>
