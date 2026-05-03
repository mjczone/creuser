import { computed, watch } from 'vue';
import { useRoute } from 'vue-router';
import { useWorkspaceStore } from 'stores/workspace';

/**
 * Reads the active workspace slug off the current route. The single source
 * of truth for "which workspace are we in" — no global state to fight with,
 * each browser tab carries its own slug in its URL.
 *
 * `slug` is null on platform-level routes (Home, Platform Settings).
 * `workspace` resolves once the store has the record cached; the route guard
 * is responsible for ensuring the workspace exists + is accessible *before*
 * the route activates, so consumers can treat null-while-cache-misses as a
 * transient state during navigation rather than an error case.
 */
export function useActiveWorkspace() {
  const route = useRoute();
  const store = useWorkspaceStore();

  const slug = computed<string | null>(() => {
    const raw = route.params.workspaceSlug;
    if (typeof raw === 'string' && raw.length > 0) return raw;
    return null;
  });

  const workspace = computed(() => (slug.value ? store.get(slug.value) : null));

  // If the slug appears in the route but isn't cached yet (e.g. direct URL
  // hit before the guard ran), trigger the load. The guard does this too;
  // belt-and-suspenders.
  watch(
    slug,
    (next) => {
      if (next && !store.get(next) && !store.isDenied(next)) {
        void store.ensureLoaded(next);
      }
    },
    { immediate: true },
  );

  return { slug, workspace };
}
