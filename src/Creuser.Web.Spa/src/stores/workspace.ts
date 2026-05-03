import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { Workspaces, type WorkspaceResult } from 'src/api';

/**
 * Active-workspace state keyed by slug. The architecture's no-global-current-
 * workspace rule applies: there is no `activeSlug` ref to set imperatively;
 * the active slug is always derived from the URL via `useActiveWorkspace()`,
 * and this store just caches the loaded workspace records keyed by slug so
 * route entries don't re-fetch.
 *
 * Two browser tabs at /w/compas/... and /w/acme/... each see their own
 * workspace because each tab reads the slug from its own route. The cache
 * is shared across tabs (it's just a normal Pinia store), but lookup is
 * always slug-keyed so there's nothing to fight over.
 */
export const useWorkspaceStore = defineStore('workspace', () => {
  const cache = ref<Map<string, WorkspaceResult>>(new Map());
  // Set of slugs the user has been *unable* to access in this session. Used
  // by the route guard to bounce subsequent attempts without re-pinging the
  // server. Cleared on login.
  const denied = ref<Set<string>>(new Set());
  const loading = ref<Set<string>>(new Set());

  function get(slug: string): WorkspaceResult | null {
    return cache.value.get(slug) ?? null;
  }

  function isDenied(slug: string): boolean {
    return denied.value.has(slug);
  }

  /**
   * Load a workspace by slug. Returns `null` if the workspace doesn't exist
   * or the user can't access it (403/404 are both treated as "no access" —
   * not exposing existence to non-members is the architecture's call).
   */
  async function ensureLoaded(slug: string): Promise<WorkspaceResult | null> {
    if (cache.value.has(slug)) return cache.value.get(slug) ?? null;
    if (denied.value.has(slug)) return null;
    if (loading.value.has(slug)) {
      // Concurrent caller; wait for the first one to settle by polling the
      // cache + denied sets. Tight loop is fine — Pinia state changes
      // synchronously inside the same tick once the fetch resolves.
      await new Promise((r) => setTimeout(r, 16));
      return cache.value.get(slug) ?? null;
    }
    loading.value.add(slug);
    try {
      const res = await Workspaces.getWorkspace({ path: { slug } });
      if (res.error) {
        denied.value.add(slug);
        return null;
      }
      const ws = res.data?.result;
      if (!ws) {
        denied.value.add(slug);
        return null;
      }
      cache.value.set(slug, ws);
      return ws;
    } catch {
      denied.value.add(slug);
      return null;
    } finally {
      loading.value.delete(slug);
    }
  }

  /** Clear all cached + denied state. Call on logout / login transitions. */
  function reset() {
    cache.value.clear();
    denied.value.clear();
    loading.value.clear();
  }

  /** Update the cached record after an in-app mutation (e.g. sync). */
  function upsert(workspace: WorkspaceResult) {
    cache.value.set(workspace.slug, workspace);
  }

  return {
    cache: computed(() => cache.value),
    get,
    isDenied,
    ensureLoaded,
    reset,
    upsert,
  };
});
