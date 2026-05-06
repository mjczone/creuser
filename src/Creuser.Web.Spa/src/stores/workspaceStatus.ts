import { defineStore, acceptHMRUpdate } from 'pinia';
import { computed, ref } from 'vue';
import { Workspaces } from 'src/api';
import type { WorkspaceCapabilitiesDto } from 'src/api';
import { notifications } from 'src/services/notifications';

/**
 * Live status snapshot for the active workspace. Drives the header's
 * Commit / Push buttons (visibility, badge counts) and any other
 * surface that wants to reflect "is there pending work."
 *
 * State source of truth flow:
 *   1. `setActive(slug)` — fetches an initial status, subscribes to the
 *      workspace's SignalR channel, and wires the listener to update
 *      this store's state on every server-pushed change.
 *   2. Server-side: every state-mutating verb (write/commit/push/sync)
 *      broadcasts a fresh `WorkspaceProviderStatus` to
 *      `workspace:<slug>:status`.
 *   3. Client receives the broadcast → store updates → UI reactively
 *      re-renders. No polling.
 */
export const useWorkspaceStatusStore = defineStore('workspaceStatus', () => {
  const slug = ref<string | null>(null);
  const type = ref<string | null>(null);
  const capabilities = ref<WorkspaceCapabilitiesDto | null>(null);
  const uncommittedFileCount = ref(0);
  const unpushedCommitCount = ref(0);
  const workingRootExists = ref(false);
  const loading = ref(false);
  const error = ref<string | null>(null);

  // The unsubscribe handle for the current SignalR subscription.
  // Captured so `setActive` can tear down the previous subscription
  // before subscribing to the new one (and so we don't leak handlers
  // across workspace switches).
  let unsubscribe: (() => Promise<void>) | null = null;

  const canCommit = computed(() => capabilities.value?.canCommit ?? false);
  const canPush = computed(() => capabilities.value?.canPush ?? false);
  const canWrite = computed(() => capabilities.value?.canWrite ?? false);
  const canSync = computed(() => capabilities.value?.canSync ?? false);

  // Accepts any shape — the narrowing happens per property. The hub
  // broadcasts WorkspaceProviderStatus payloads directly (camelCase
  // matching the wire result), but receiving an `unknown` and shaping
  // here means a malformed broadcast can't crash the listener.
  function applyStatus(payload: unknown) {
    if (!payload || typeof payload !== 'object') return;
    const p = payload as Record<string, unknown>;
    if (typeof p.uncommittedFileCount === 'number')
      uncommittedFileCount.value = p.uncommittedFileCount;
    if (typeof p.unpushedCommitCount === 'number')
      unpushedCommitCount.value = p.unpushedCommitCount;
    if (typeof p.workingRootExists === 'boolean') workingRootExists.value = p.workingRootExists;
    if (p.capabilities && typeof p.capabilities === 'object')
      capabilities.value = p.capabilities as WorkspaceCapabilitiesDto;
    if (typeof p.type === 'string') type.value = p.type;
  }

  /**
   * Switch the store to track a new workspace. Tears down any prior
   * SignalR subscription, fetches an initial status snapshot, and
   * subscribes to the new workspace's status channel for live updates.
   * Pass `null` to clear (e.g., when leaving a workspace-scoped route).
   */
  async function setActive(nextSlug: string | null) {
    if (slug.value === nextSlug) return;
    if (unsubscribe) {
      const prev = unsubscribe;
      unsubscribe = null;
      void prev().catch(() => {
        // best-effort cleanup
      });
    }
    slug.value = nextSlug;
    type.value = null;
    capabilities.value = null;
    uncommittedFileCount.value = 0;
    unpushedCommitCount.value = 0;
    workingRootExists.value = false;
    error.value = null;
    if (!nextSlug) return;

    loading.value = true;
    try {
      const res = await Workspaces.getWorkspaceStatus({ path: { slug: nextSlug } });
      if (res.error) {
        error.value =
          (typeof res.error === 'object' && 'detail' in res.error
            ? String((res.error as { detail?: unknown }).detail)
            : null) ?? 'Failed to load workspace status.';
        return;
      }
      const status = res.data?.result;
      if (status) applyStatus(status);
    } finally {
      loading.value = false;
    }

    // Subscribe even when the initial fetch failed — a transient API
    // error shouldn't disable real-time updates for the rest of the
    // session. The hub will deliver the next state-mutation broadcast.
    try {
      const channel = `workspace:${nextSlug}:status`;
      unsubscribe = await notifications.subscribe(channel, (payload: unknown) => {
        applyStatus(payload);
      });
    } catch (err) {
      console.warn('Failed to subscribe to workspace status channel:', err);
    }
  }

  /** Force-refresh the snapshot. Useful after a no-broadcast operation (rare) or on focus. */
  async function refresh() {
    if (!slug.value) return;
    const res = await Workspaces.getWorkspaceStatus({ path: { slug: slug.value } });
    const status = res.data?.result;
    if (status) applyStatus(status);
  }

  return {
    slug,
    type,
    capabilities,
    uncommittedFileCount,
    unpushedCommitCount,
    workingRootExists,
    loading,
    error,
    canCommit,
    canPush,
    canWrite,
    canSync,
    setActive,
    refresh,
  };
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useWorkspaceStatusStore, import.meta.hot));
}
