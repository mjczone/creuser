<template>
  <div class="cr-w-edit">
    <div class="cr-w-edit-toolbar">
      <q-select
        v-model="selectedSlug"
        :options="scriptOptions"
        emit-value
        map-options
        dense
        outlined
        label="Script"
        class="cr-w-edit-picker"
        @update:model-value="loadScript"
      />
      <q-space />
      <q-btn
        flat
        dense
        icon="play_arrow"
        size="sm"
        :loading="firing"
        :disable="!current"
        label="Run"
        @click="runScript"
      />
      <q-btn
        unelevated
        color="primary"
        dense
        icon="save"
        size="sm"
        :loading="saving"
        :disable="!dirty || !current"
        label="Save"
        @click="saveScript"
      />
      <q-btn flat dense round icon="refresh" size="sm" :loading="loading" @click="reload">
        <q-tooltip>Reload from server</q-tooltip>
      </q-btn>
    </div>

    <div v-if="error" class="cr-w-edit-banner cr-w-edit-error">
      <q-icon name="error_outline" size="14px" />
      <span>{{ error }}</span>
    </div>
    <div v-else-if="dirty" class="cr-w-edit-banner cr-w-edit-dirty">
      <q-icon name="edit" size="14px" />
      <span>Unsaved changes — click Save to persist.</span>
    </div>

    <div class="cr-w-edit-body">
      <section class="cr-w-edit-pane">
        <h3 class="cr-w-edit-pane-title">Frontmatter (YAML)</h3>
        <vue-monaco-editor
          v-model:value="frontmatter"
          theme="vs-dark"
          language="yaml"
          :options="editorOptions"
          class="cr-w-edit-monaco"
        >
          <template #default>
            <div class="cr-w-edit-loading">Loading editor…</div>
          </template>
          <template #failure>
            <div class="cr-w-edit-loading cr-w-edit-error">
              Editor failed to load. Check network access to jsdelivr.net.
            </div>
          </template>
        </vue-monaco-editor>
      </section>
      <section class="cr-w-edit-pane">
        <h3 class="cr-w-edit-pane-title">Body (Markdown)</h3>
        <vue-monaco-editor
          v-model:value="body"
          theme="vs-dark"
          language="markdown"
          :options="editorOptions"
          class="cr-w-edit-monaco"
        >
          <template #default>
            <div class="cr-w-edit-loading">Loading editor…</div>
          </template>
        </vue-monaco-editor>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
/**
 * JobScriptEditor — Monaco-backed editor for one job script's frontmatter
 * (YAML) + body (Markdown). Reads `scriptSlug` from `propsData`; if blank,
 * shows a script picker populated from `Jobs.listJobs`.
 *
 * Save calls `Jobs.updateJob` with the in-memory frontmatter + body.
 * Run fires the script via `Jobs.runJob` against the active workspace.
 *
 * Monaco is lazy-loaded from CDN (configured in `boot/monaco.ts`) so
 * first paint happens before the editor downloads. Air-gapped deployments
 * pin a self-hosted path in the boot config.
 */
import { computed, onMounted, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { Jobs } from 'src/api';
import type { JobScriptResult } from 'src/api';

const props = defineProps<{
  widgetType: string;
  propsData: { scriptSlug?: string };
  workspaceSlug?: string | null;
}>();

const $q = useQuasar();

const scripts = ref<JobScriptResult[]>([]);
const current = ref<JobScriptResult | null>(null);
const selectedSlug = ref<string | null>(props.propsData.scriptSlug ?? null);
const frontmatter = ref<string>('');
const body = ref<string>('');
const baseline = ref<{ fm: string; body: string }>({ fm: '', body: '' });
const loading = ref(false);
const saving = ref(false);
const firing = ref(false);
const error = ref<string | null>(null);

const editorOptions = {
  automaticLayout: true,
  minimap: { enabled: false },
  fontSize: 12,
  scrollBeyondLastLine: false,
  tabSize: 2,
  wordWrap: 'on' as const,
};

const scriptOptions = computed(() =>
  scripts.value.map((s) => ({ label: `${s.name} (${s.slug})`, value: s.slug })),
);

const dirty = computed(
  () => frontmatter.value !== baseline.value.fm || body.value !== baseline.value.body,
);

async function loadScripts() {
  if (!props.workspaceSlug) return;
  try {
    const res = await Jobs.listJobs({ path: { slug: props.workspaceSlug } });
    scripts.value = res.data?.result ?? [];
    if (!selectedSlug.value && scripts.value.length > 0) {
      selectedSlug.value = scripts.value[0]!.slug;
    }
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Failed to list scripts.';
  }
}

async function loadScript() {
  if (!props.workspaceSlug || !selectedSlug.value) {
    current.value = null;
    return;
  }
  // Backend uses /jobs/{jobId:guid}; the picker exposes slugs (friendlier
  // than UUIDs in the dropdown). Resolve via the cached scripts list —
  // we already have it from loadScripts().
  const stub = scripts.value.find((s) => s.slug === selectedSlug.value);
  if (!stub) {
    current.value = null;
    error.value = `Script ${selectedSlug.value} not found in this workspace.`;
    return;
  }
  loading.value = true;
  error.value = null;
  try {
    const res = await Jobs.getJob({
      path: { slug: props.workspaceSlug, jobId: stub.jobScriptId },
    });
    const result = res.data?.result;
    if (result) {
      current.value = result;
      frontmatter.value = result.frontmatter ?? '';
      body.value = result.body ?? '';
      baseline.value = { fm: frontmatter.value, body: body.value };
    } else {
      current.value = null;
    }
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Failed to load script.';
  } finally {
    loading.value = false;
  }
}

async function reload() {
  await loadScripts();
  await loadScript();
}

async function saveScript() {
  if (!props.workspaceSlug || !current.value) return;
  saving.value = true;
  error.value = null;
  try {
    const res = await Jobs.updateJob({
      path: { slug: props.workspaceSlug, jobId: current.value.jobScriptId },
      body: {
        name: current.value.name,
        description: current.value.description,
        pattern: current.value.pattern,
        frontmatter: frontmatter.value,
        body: body.value,
        status: current.value.status,
      },
    });
    const updated = res.data?.result;
    if (updated) {
      current.value = updated;
      frontmatter.value = updated.frontmatter ?? '';
      body.value = updated.body ?? '';
      baseline.value = { fm: frontmatter.value, body: body.value };
      $q.notify({ type: 'positive', message: `Saved ${updated.slug}.`, timeout: 2000 });
    }
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Save failed.';
  } finally {
    saving.value = false;
  }
}

async function runScript() {
  if (!props.workspaceSlug || !current.value) return;
  firing.value = true;
  try {
    await Jobs.runJob({
      path: { slug: props.workspaceSlug, jobId: current.value.jobScriptId },
      body: { parameters: {} },
    });
    $q.notify({
      type: 'positive',
      message: `Fired ${current.value.slug}.`,
      timeout: 2000,
    });
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Run failed.';
  } finally {
    firing.value = false;
  }
}

onMounted(async () => {
  await loadScripts();
  await loadScript();
});

watch(
  () => props.workspaceSlug,
  () => {
    void reload();
  },
);

// `scriptSlug` prop change from outside (e.g. via the composer) updates
// the selected script. Local picker changes also drive selectedSlug, so
// we sync from the prop only when it differs from the current selection.
watch(
  () => props.propsData?.scriptSlug,
  (next) => {
    if (next && next !== selectedSlug.value) {
      selectedSlug.value = next;
      void loadScript();
    }
  },
);
</script>

<style lang="scss" scoped>
.cr-w-edit {
  height: 100%;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-elevated, #1a1a1d);
}

.cr-w-edit-toolbar {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  border-bottom: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
  background: var(--cr-bg-subtle, #1f1f22);
}

.cr-w-edit-picker {
  flex: 1;
  max-width: 320px;
}

.cr-w-edit-banner {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 12px;
  font-size: 11px;
  border-bottom: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
}

.cr-w-edit-error {
  background: rgba(239, 68, 68, 0.1);
  color: rgb(248, 113, 113);
}

.cr-w-edit-dirty {
  background: rgba(234, 179, 8, 0.08);
  color: rgb(250, 204, 21);
}

.cr-w-edit-body {
  flex: 1;
  display: grid;
  grid-template-columns: 1fr;
  grid-template-rows: minmax(120px, 30%) 1fr;
  min-height: 0;
}

.cr-w-edit-pane {
  display: flex;
  flex-direction: column;
  min-height: 0;
  border-bottom: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));

  &:last-child {
    border-bottom: none;
  }
}

.cr-w-edit-pane-title {
  margin: 0;
  padding: 4px 12px;
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.16em;
  color: var(--cr-fg-tertiary, #888);
  background: var(--cr-bg-subtle, #1f1f22);
  flex-shrink: 0;
}

.cr-w-edit-monaco {
  flex: 1;
  min-height: 0;
}

.cr-w-edit-loading {
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--cr-fg-tertiary, #888);
  font-size: 12px;
}
</style>
