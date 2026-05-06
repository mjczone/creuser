<template>
  <div v-if="status.slug" class="cr-ws-actions">
    <q-btn
      v-if="status.canCommit"
      flat
      dense
      round
      icon="check_circle"
      :color="status.uncommittedFileCount > 0 ? 'primary' : undefined"
      :disable="status.uncommittedFileCount === 0 || committing"
      :loading="committing"
      :aria-label="`Commit ${status.uncommittedFileCount} uncommitted change${status.uncommittedFileCount === 1 ? '' : 's'}`"
      class="cr-ws-action-btn"
      @click="onCommitClick"
    >
      <q-badge
        v-if="status.uncommittedFileCount > 0"
        floating
        color="primary"
        :label="String(status.uncommittedFileCount)"
      />
      <q-tooltip anchor="bottom right" self="top right">
        <template v-if="status.uncommittedFileCount > 0">
          Commit {{ status.uncommittedFileCount }} uncommitted change{{
            status.uncommittedFileCount === 1 ? '' : 's'
          }}
        </template>
        <template v-else>No uncommitted changes</template>
      </q-tooltip>
    </q-btn>

    <q-btn
      v-if="status.canPush"
      flat
      dense
      round
      icon="cloud_upload"
      :color="status.unpushedCommitCount > 0 ? 'primary' : undefined"
      :disable="status.unpushedCommitCount === 0 || pushing"
      :loading="pushing"
      :aria-label="`Push ${status.unpushedCommitCount} unpushed commit${status.unpushedCommitCount === 1 ? '' : 's'}`"
      class="cr-ws-action-btn"
      @click="onPushClick"
    >
      <q-badge
        v-if="status.unpushedCommitCount > 0"
        floating
        color="primary"
        :label="String(status.unpushedCommitCount)"
      />
      <q-tooltip anchor="bottom right" self="top right">
        <template v-if="status.unpushedCommitCount > 0">
          Push {{ status.unpushedCommitCount }} unpushed commit{{
            status.unpushedCommitCount === 1 ? '' : 's'
          }}
        </template>
        <template v-else>No unpushed commits</template>
      </q-tooltip>
    </q-btn>
  </div>
</template>

<script setup lang="ts">
/**
 * Header-bar action buttons for the active workspace. Visibility +
 * enablement are driven entirely by the workspace's provider
 * capabilities + live status counts (broadcast over SignalR by every
 * state-mutating verb on the backend). Local workspaces never render
 * either button — they have no commit boundary, no remote.
 *
 * Commit click → small dialog asks for a commit message → POST /commit.
 * Push click → POST /push.
 *
 * Status updates arrive via SignalR after the backend persists, so the
 * badges flip without a refetch.
 */
import { ref } from 'vue';
import { useQuasar } from 'quasar';
import { Workspaces } from 'src/api';
import { useWorkspaceStatusStore } from 'src/stores/workspaceStatus';

const $q = useQuasar();
const status = useWorkspaceStatusStore();

const committing = ref(false);
const pushing = ref(false);

function onCommitClick() {
  if (!status.slug || status.uncommittedFileCount === 0) return;
  $q.dialog({
    title: 'Commit changes',
    message: `Bundle <strong>${status.uncommittedFileCount}</strong> uncommitted file change${status.uncommittedFileCount === 1 ? '' : 's'} into one commit. Enter a commit message:`,
    html: true,
    prompt: {
      model: '',
      type: 'text',
      isValid: (val: string) => val.trim().length > 0,
      autofocus: true,
      placeholder: 'Describe the change',
    },
    ok: { label: 'Commit', color: 'primary', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: false,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async (commitMessage: string) => {
    await runCommit(commitMessage.trim());
  });
}

async function runCommit(commitMessage: string) {
  if (!status.slug) return;
  committing.value = true;
  try {
    const res = await Workspaces.commitWorkspace({
      path: { slug: status.slug },
      body: { commitMessage },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Commit failed.',
      });
      return;
    }
    const result = res.data?.result;
    if (!result?.ok) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: result?.error ?? 'Commit failed.',
        timeout: 8000,
      });
      return;
    }
    $q.notify({
      type: result.nothingToCommit ? 'info' : 'positive',
      position: 'top',
      message: result.message ?? 'Committed.',
    });
    // Status broadcasts via SignalR; no manual refresh needed.
  } finally {
    committing.value = false;
  }
}

async function onPushClick() {
  if (!status.slug || status.unpushedCommitCount === 0) return;
  pushing.value = true;
  try {
    const res = await Workspaces.pushWorkspace({ path: { slug: status.slug } });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Push failed.',
      });
      return;
    }
    const result = res.data?.result;
    if (!result?.ok) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: result?.error ?? 'Push failed.',
        timeout: 8000,
      });
      return;
    }
    $q.notify({
      type: result.nothingToPush ? 'info' : 'positive',
      position: 'top',
      message: result.message ?? 'Pushed.',
    });
  } finally {
    pushing.value = false;
  }
}

function problemMessage(err: unknown): string | undefined {
  if (err && typeof err === 'object') {
    const e = err as { detail?: unknown; title?: unknown };
    if (typeof e.detail === 'string' && e.detail.length) return e.detail;
    if (typeof e.title === 'string' && e.title.length) return e.title;
  }
  return undefined;
}
</script>

<style lang="scss" scoped>
.cr-ws-actions {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.cr-ws-action-btn {
  // Match the visual weight of the AI / theme toggle buttons next to it.
  // The badge is positioned by Quasar's `floating` prop.
}
</style>
