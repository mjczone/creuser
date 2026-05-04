import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import type {
  DashboardNavGroup,
  DashboardNavItem,
  DashboardNavTree,
  DashboardResult,
} from 'src/api';
import { Dashboards } from 'src/api';

/**
 * Per-workspace cache of the dashboards nav-tree (icon-bar source) and
 * individual dashboard payloads (DashboardPage source). Both cached by
 * workspace slug so re-entering a workspace doesn't re-fetch unless the
 * caller forces a refresh.
 *
 * Mutations (create / update / delete) invalidate the cached nav-tree for
 * the affected slug so subsequent navigations re-query. The store exposes
 * imperative `refresh*` helpers for callers that need the freshest state
 * without waiting for cache invalidation to ripple.
 */
export const useDashboardsStore = defineStore('dashboards', () => {
  const navCache = ref<Map<string, DashboardNavTree>>(new Map());
  const dashboardCache = ref<Map<string, Map<string, DashboardResult>>>(new Map());
  const loading = ref<Set<string>>(new Set());

  function getNavTree(workspaceSlug: string): DashboardNavTree | null {
    return navCache.value.get(workspaceSlug) ?? null;
  }

  async function ensureNavTree(
    workspaceSlug: string,
    force = false,
  ): Promise<DashboardNavTree | null> {
    if (!force && navCache.value.has(workspaceSlug)) {
      return navCache.value.get(workspaceSlug) ?? null;
    }
    const key = `nav:${workspaceSlug}`;
    if (loading.value.has(key)) {
      // Concurrent caller — wait briefly for the in-flight fetch.
      await new Promise((r) => setTimeout(r, 16));
      return navCache.value.get(workspaceSlug) ?? null;
    }
    loading.value.add(key);
    try {
      const res = await Dashboards.listDashboards({ path: { slug: workspaceSlug } });
      const tree = res.data?.result ?? null;
      if (tree) navCache.value.set(workspaceSlug, tree);
      return tree;
    } finally {
      loading.value.delete(key);
    }
  }

  function getDashboard(workspaceSlug: string, dashboardSlug: string): DashboardResult | null {
    return dashboardCache.value.get(workspaceSlug)?.get(dashboardSlug) ?? null;
  }

  async function ensureDashboard(
    workspaceSlug: string,
    dashboardSlug: string,
    force = false,
  ): Promise<DashboardResult | null> {
    const wsCache = dashboardCache.value.get(workspaceSlug);
    if (!force && wsCache?.has(dashboardSlug)) {
      return wsCache.get(dashboardSlug) ?? null;
    }
    const key = `dash:${workspaceSlug}/${dashboardSlug}`;
    if (loading.value.has(key)) {
      await new Promise((r) => setTimeout(r, 16));
      return dashboardCache.value.get(workspaceSlug)?.get(dashboardSlug) ?? null;
    }
    loading.value.add(key);
    try {
      const res = await Dashboards.getDashboard({
        path: { slug: workspaceSlug, dashSlug: dashboardSlug },
      });
      const dash = res.data?.result ?? null;
      if (dash) {
        if (!dashboardCache.value.has(workspaceSlug)) {
          dashboardCache.value.set(workspaceSlug, new Map());
        }
        dashboardCache.value.get(workspaceSlug)!.set(dashboardSlug, dash);
      }
      return dash;
    } finally {
      loading.value.delete(key);
    }
  }

  /**
   * Persist the layout + widgets for one dashboard. The store optimistically
   * caches the new state — the API responds with the updated record which
   * overwrites our optimistic copy.
   */
  async function saveLayout(
    workspaceSlug: string,
    dashboardSlug: string,
    layoutJson: string,
    widgetsJson: string,
  ): Promise<DashboardResult | null> {
    const res = await Dashboards.updateDashboard({
      path: { slug: workspaceSlug, dashSlug: dashboardSlug },
      body: { layoutJson, widgetsJson, name: null, icon: null, groupSlug: null, position: null },
    });
    const updated = res.data?.result ?? null;
    if (updated) {
      if (!dashboardCache.value.has(workspaceSlug)) {
        dashboardCache.value.set(workspaceSlug, new Map());
      }
      dashboardCache.value.get(workspaceSlug)!.set(dashboardSlug, updated);
    }
    return updated;
  }

  function invalidate(workspaceSlug: string): void {
    navCache.value.delete(workspaceSlug);
    dashboardCache.value.delete(workspaceSlug);
  }

  function reset(): void {
    navCache.value.clear();
    dashboardCache.value.clear();
    loading.value.clear();
  }

  return {
    nav: computed(() => navCache.value),
    getNavTree,
    ensureNavTree,
    getDashboard,
    ensureDashboard,
    saveLayout,
    invalidate,
    reset,
  };
});

export type { DashboardNavGroup, DashboardNavItem, DashboardNavTree, DashboardResult };
