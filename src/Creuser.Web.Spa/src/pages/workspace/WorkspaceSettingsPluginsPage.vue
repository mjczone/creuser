<template>
  <div class="q-pa-lg cr-wsplugins">
    <header class="cr-wsplugins-header">
      <h1 class="text-h5 q-ma-none">Plugins</h1>
      <p class="cr-wsplugins-subhead">
        Plugins are .NET assemblies dropped into <code>/data/plugins/</code>. They're
        loaded once per Creuser instance and contribute job runners, widgets, agent
        providers, parsers, and capabilities. This page chooses which loaded plugins
        are <strong>enabled for this workspace</strong> — disabled plugins are
        invisible to the workspace's job runner picker and widget palette.
      </p>
    </header>

    <div v-if="loading" class="cr-wsplugins-loading">
      <q-spinner size="md" />
      <span>Loading plugins…</span>
    </div>

    <div v-else-if="error" class="cr-wsplugins-error">
      <q-icon name="error_outline" size="20px" />
      <span>{{ error }}</span>
    </div>

    <template v-else>
      <q-banner v-if="note" rounded class="cr-wsplugins-note">
        <template #avatar>
          <q-icon name="info" class="cr-wsplugins-note-icon" />
        </template>
        {{ note }}
      </q-banner>

      <div v-if="plugins.length === 0" class="cr-wsplugins-empty">
        <q-icon name="extension_off" size="48px" class="cr-wsplugins-empty-icon" />
        <h2 class="text-h6 q-ma-none">No plugins loaded</h2>
        <p class="cr-wsplugins-empty-copy">
          Once a plugin assembly is in <code>/data/plugins/</code> and the platform has
          restarted, it will appear here. Each row will offer an enable / disable toggle
          and surface what the plugin contributes (job runners, widgets, agent providers).
        </p>
      </div>

      <q-table
        v-else
        :rows="plugins"
        :columns="cols"
        row-key="pluginId"
        flat
        bordered
        dense
      >
        <template #body-cell-status="props">
          <q-td :props="props">
            <q-chip
              dense
              outline
              :color="statusColor(props.row.status)"
              :text-color="statusColor(props.row.status)"
            >
              {{ props.row.status }}
            </q-chip>
          </q-td>
        </template>
        <template #body-cell-provides="props">
          <q-td :props="props">
            <span v-for="p in props.row.provides" :key="p" class="cr-wsplugins-tag">
              {{ p }}
            </span>
          </q-td>
        </template>
        <template #body-cell-enabled="props">
          <q-td :props="props" auto-width>
            <q-toggle
              :model-value="props.row.enabled"
              color="primary"
              :disable="props.row.status !== 'loaded'"
              :aria-label="`${props.row.enabled ? 'Disable' : 'Enable'} ${props.row.name}`"
              @update:model-value="onToggle(props.row)"
            />
          </q-td>
        </template>
      </q-table>
    </template>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue';
import { useQuasar, type QTableColumn } from 'quasar';
import { Workspaces, type WorkspacePluginInfo } from 'src/api';
import { useActiveWorkspace } from 'src/composables/useActiveWorkspace';

const $q = useQuasar();
const { slug } = useActiveWorkspace();

const plugins = ref<WorkspacePluginInfo[]>([]);
const note = ref<string | null>(null);
const loading = ref(false);
const error = ref('');

const cols: QTableColumn<WorkspacePluginInfo>[] = [
  { name: 'name', label: 'Name', field: 'name', align: 'left' },
  { name: 'version', label: 'Version', field: 'version', align: 'left' },
  { name: 'author', label: 'Author', field: 'author', align: 'left' },
  { name: 'status', label: 'Status', field: 'status', align: 'left' },
  { name: 'provides', label: 'Provides', field: 'provides', align: 'left' },
  { name: 'enabled', label: 'Enabled', field: 'enabled', align: 'right' },
];

function statusColor(status: string): string {
  if (status === 'loaded') return 'positive';
  if (status === 'failed') return 'negative';
  if (status === 'incompatible') return 'warning';
  return 'grey-7';
}

async function load() {
  if (!slug.value) return;
  loading.value = true;
  error.value = '';
  try {
    const res = await Workspaces.listWorkspacePlugins({ path: { slug: slug.value } });
    if (res.error) {
      error.value = problemMessage(res.error) ?? 'Could not load plugins.';
      return;
    }
    plugins.value = res.data?.result?.plugins ?? [];
    note.value = res.data?.result?.note ?? null;
  } finally {
    loading.value = false;
  }
}

function onToggle(plugin: WorkspacePluginInfo) {
  // Persistence endpoint lands with the plugin loader. Until then the
  // toggle is informational — surface that explicitly so admins don't think
  // their click was lost.
  $q.notify({
    type: 'info',
    position: 'top',
    message: `${plugin.name}: enable/disable persists once the plugin loader lands.`,
    timeout: 4000,
  });
}

function problemMessage(err: unknown): string | undefined {
  if (err && typeof err === 'object') {
    const e = err as { detail?: unknown; title?: unknown };
    if (typeof e.detail === 'string' && e.detail.length) return e.detail;
    if (typeof e.title === 'string' && e.title.length) return e.title;
  }
  return undefined;
}

watch(slug, () => void load());
onMounted(() => void load());
</script>

<style lang="scss" scoped>
.cr-wsplugins-header {
  margin-bottom: 16px;
}

.cr-wsplugins-subhead {
  margin: 8px 0 0;
  font-size: 13px;
  color: var(--cr-fg-secondary);
  max-width: 760px;
  line-height: 1.5;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}

.cr-wsplugins-loading,
.cr-wsplugins-error {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--cr-fg-secondary);
  font-size: 13px;
}

.cr-wsplugins-error {
  color: var(--q-negative);
}

.cr-wsplugins-note {
  margin: 8px 0 16px;
  background: var(--cr-bg-elevated);
  color: var(--cr-fg-secondary);
  border: 1px solid var(--cr-border-subtle);
  font-size: 12px;
  line-height: 1.5;
}

.cr-wsplugins-note-icon {
  color: var(--cr-fg-tertiary);
}

.cr-wsplugins-empty {
  margin-top: 16px;
  padding: 32px 24px;
  border: 1px dashed var(--cr-border-default);
  border-radius: 6px;
  background: var(--cr-bg-elevated);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  max-width: 640px;
}

.cr-wsplugins-empty-icon {
  color: var(--cr-fg-tertiary);
}

.cr-wsplugins-empty-copy {
  margin: 0;
  text-align: center;
  font-size: 13px;
  color: var(--cr-fg-secondary);
  line-height: 1.5;
  max-width: 480px;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-surface);
    padding: 1px 6px;
    border-radius: 3px;
  }
}

.cr-wsplugins-tag {
  display: inline-block;
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  background: var(--cr-bg-elevated);
  color: var(--cr-fg-secondary);
  padding: 1px 6px;
  border-radius: 3px;
  margin-right: 4px;
  margin-bottom: 2px;
}
</style>
