<template>
  <div class="cr-fm">
    <header class="cr-fm-header">
      <q-breadcrumbs class="cr-fm-crumbs" separator="/">
        <q-breadcrumbs-el
          label="root"
          icon="home"
          class="cr-fm-crumb"
          :class="{ 'cr-fm-crumb--last': pathSegments.length === 0 }"
          @click="navigate('')"
        />
        <q-breadcrumbs-el
          v-for="(seg, i) in pathSegments"
          :key="seg.path"
          :label="seg.name"
          class="cr-fm-crumb"
          :class="{ 'cr-fm-crumb--last': i === pathSegments.length - 1 }"
          @click="navigate(seg.path)"
        />
      </q-breadcrumbs>
      <q-space />
      <q-btn
        flat
        dense
        no-caps
        icon="note_add"
        size="sm"
        :disable="loading"
        aria-label="New file in this folder"
        @click="onNewFile"
      >
        <q-tooltip>New file in this folder</q-tooltip>
      </q-btn>
      <q-btn
        flat
        dense
        round
        size="sm"
        icon="refresh"
        :loading="loading"
        aria-label="Refresh"
        @click="reload"
      >
        <q-tooltip>Refresh</q-tooltip>
      </q-btn>
    </header>

    <div class="cr-fm-body">
      <aside class="cr-fm-list">
        <div v-if="loading && !listing" class="cr-fm-empty">Loading…</div>
        <div v-else-if="listing && listing.folders.length === 0 && listing.files.length === 0" class="cr-fm-empty">
          Empty folder.
        </div>
        <ul v-else-if="listing" class="cr-fm-rows">
          <li
            v-for="folder in listing.folders"
            :key="`f:${folder.path}`"
            class="cr-fm-row cr-fm-row--folder"
            @click="navigate(folder.path)"
          >
            <q-icon name="folder" size="18px" class="cr-fm-row-icon" />
            <span class="cr-fm-row-name">{{ folder.name }}</span>
            <q-icon name="chevron_right" size="14px" class="cr-fm-row-chev" />
            <q-menu touch-position context-menu auto-close>
              <q-list dense style="min-width: 180px">
                <q-item clickable @click="navigate(folder.path)">
                  <q-item-section>Open folder</q-item-section>
                </q-item>
                <q-separator />
                <q-item clickable class="text-negative" @click="onDeleteFolder(folder.path)">
                  <q-item-section>Delete folder</q-item-section>
                </q-item>
              </q-list>
            </q-menu>
          </li>
          <li
            v-for="file in listing.files"
            :key="`x:${file.path}`"
            class="cr-fm-row cr-fm-row--file"
            :class="{ 'cr-fm-row--active': selected?.path === file.path }"
            @click="openFile(file)"
          >
            <q-icon :name="iconForKind(file.contentKind)" size="18px" class="cr-fm-row-icon" />
            <span class="cr-fm-row-name">{{ file.name }}</span>
            <span class="cr-fm-row-size">{{ formatSize(Number(file.sizeBytes)) }}</span>
            <q-menu touch-position context-menu auto-close>
              <q-list dense style="min-width: 180px">
                <q-item clickable @click="openFile(file)">
                  <q-item-section>Open</q-item-section>
                </q-item>
                <q-separator />
                <q-item clickable class="text-negative" @click="onDeleteFile(file)">
                  <q-item-section>Delete</q-item-section>
                </q-item>
              </q-list>
            </q-menu>
          </li>
        </ul>

        <div v-if="listing?.truncated" class="cr-fm-truncated">
          <q-icon name="info" size="14px" />
          More than {{ entryCap }} entries — narrow your path or open a subfolder.
        </div>
      </aside>

      <main class="cr-fm-pane">
        <div v-if="!selected" class="cr-fm-pane-empty">
          <q-icon name="article" size="40px" />
          <p>Select a file to preview.</p>
        </div>

        <template v-else>
          <div class="cr-fm-pane-header">
            <q-icon :name="iconForKind(selected.contentKind)" size="18px" />
            <code class="cr-fm-pane-path">{{ selected.path }}</code>
            <q-space />
            <q-btn
              v-if="canEditSelected && !editing"
              flat
              dense
              no-caps
              icon="edit"
              label="Edit"
              size="sm"
              :disable="contentLoading"
              @click="editing = true"
            />
            <q-btn
              v-if="editing"
              flat
              dense
              no-caps
              label="Discard"
              size="sm"
              :disable="!dirty || saving"
              @click="discardChanges"
            />
            <q-btn
              v-if="editing"
              unelevated
              dense
              no-caps
              label="Save"
              color="primary"
              size="sm"
              :loading="saving"
              :disable="!dirty"
              @click="onSave"
            />
            <q-btn
              flat
              dense
              round
              size="sm"
              icon="delete_outline"
              color="negative"
              :disable="saving || deleting"
              :loading="deleting"
              aria-label="Delete file"
              @click="onDeleteFile(selected)"
            >
              <q-tooltip>Delete</q-tooltip>
            </q-btn>
          </div>

          <div class="cr-fm-pane-body">
            <div v-if="contentLoading" class="cr-fm-pane-loading">Loading…</div>
            <vue-monaco-editor
              v-else-if="canEditSelected"
              v-model:value="editingContent"
              theme="vs-dark"
              :language="languageForFile(selected.name)"
              :options="{ ...editorOptions, readOnly: !editing }"
              class="cr-fm-monaco"
            >
              <template #default>
                <div class="cr-fm-pane-loading">Loading editor…</div>
              </template>
            </vue-monaco-editor>
            <div v-else-if="selected.contentKind === 'image'" class="cr-fm-image-frame">
              <img
                v-if="imageDataUrl"
                :src="imageDataUrl"
                :alt="selected.name"
                class="cr-fm-image"
              />
              <div v-else class="cr-fm-pane-loading">Loading image…</div>
            </div>
            <div v-else class="cr-fm-binary">
              <q-icon name="description" size="32px" />
              <p>{{ selected.name }}</p>
              <p class="cr-fm-binary-meta">
                {{ formatSize(Number(selected.sizeBytes)) }} · binary file
              </p>
              <q-btn
                v-if="selected.contentKind === 'unknown'"
                flat
                dense
                no-caps
                size="sm"
                label="View as text anyway"
                @click="forceTextView"
              />
            </div>
          </div>
        </template>
      </main>
    </div>

  </div>
</template>

<script setup lang="ts">
/**
 * Read + text-CRUD file manager. Browses the workspace working surface
 * via `Workspaces.listWorkspaceFolder`, opens text files in Monaco,
 * persists edits / deletes via the existing
 * `Workspaces.applyWorkspaceChanges` pipeline (no new write paths).
 *
 * Used as a settings-tab page (full pane) AND as a dashboard widget
 * (props passed by WidgetHost). Workspace slug comes from the prop
 * when present, otherwise the active route — so the widget works in
 * both contexts without per-mount wiring.
 */
import { computed, onMounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { useQuasar } from 'quasar';
import { Workspaces } from 'src/api';
import type { WorkspaceFileEntry, WorkspaceFolderListing } from 'src/api';

interface Props {
  /** Optional override; widget host passes this. Falls back to active route. */
  workspaceSlug?: string | null;
  /** Initial path to open. Empty string = workspace root. */
  initialPath?: string;
}

const props = withDefaults(defineProps<Props>(), {
  workspaceSlug: null,
  initialPath: '',
});

const $q = useQuasar();
const route = useRoute();

const slug = computed<string | null>(() => {
  if (props.workspaceSlug) return props.workspaceSlug;
  const v = route.params.workspaceSlug;
  return typeof v === 'string' && v.length > 0 ? v : null;
});

const entryCap = 500;

const currentPath = ref(props.initialPath);
const listing = ref<WorkspaceFolderListing | null>(null);
const loading = ref(false);

const selected = ref<WorkspaceFileEntry | null>(null);
const editingContent = ref('');
const baseline = ref('');
const editing = ref(false);
const contentLoading = ref(false);
const saving = ref(false);
const deleting = ref(false);
const imageDataUrl = ref<string | null>(null);
const forceText = ref(false);

const editorOptions = {
  automaticLayout: true,
  minimap: { enabled: false },
  fontSize: 12,
  scrollBeyondLastLine: false,
  tabSize: 2,
  wordWrap: 'on' as const,
};

const dirty = computed(() => editingContent.value !== baseline.value);

const pathSegments = computed(() => {
  if (!currentPath.value) return [];
  const segs: { name: string; path: string }[] = [];
  const parts = currentPath.value.split('/');
  let acc = '';
  for (const p of parts) {
    acc = acc.length === 0 ? p : `${acc}/${p}`;
    segs.push({ name: p, path: acc });
  }
  return segs;
});

const canEditSelected = computed(() => {
  if (!selected.value) return false;
  return selected.value.contentKind === 'text' || forceText.value;
});

watch(
  slug,
  (next) => {
    if (next) void reload();
    else listing.value = null;
  },
  { immediate: true },
);

async function reload() {
  if (!slug.value) return;
  loading.value = true;
  try {
    const res = await Workspaces.listWorkspaceFolder({
      path: { slug: slug.value },
      query: currentPath.value ? { path: currentPath.value } : {},
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Failed to list folder.',
      });
      return;
    }
    listing.value = res.data?.result ?? null;
  } finally {
    loading.value = false;
  }
}

function navigate(path: string) {
  currentPath.value = path;
  selected.value = null;
  editing.value = false;
  forceText.value = false;
  imageDataUrl.value = null;
  void reload();
}

async function openFile(file: WorkspaceFileEntry) {
  selected.value = file;
  editing.value = false;
  forceText.value = false;
  imageDataUrl.value = null;
  editingContent.value = '';
  baseline.value = '';
  await loadSelectedContent();
}

async function loadSelectedContent() {
  if (!slug.value || !selected.value) return;
  if (selected.value.contentKind === 'binary' && !forceText.value) {
    return;
  }
  contentLoading.value = true;
  try {
    if (selected.value.contentKind === 'image') {
      // Build an authenticated content URL the browser can fetch into
      // an <img>. Same /files endpoint, but we read the response as
      // bytes ourselves and turn it into a data URL — the existing
      // SDK returns string content, not bytes, so an image renders
      // garbled if we just stuff it into <img src=>. Use fetch
      // directly so cookies flow.
      const url =
        `/api/workspaces/${encodeURIComponent(slug.value)}/files` +
        `?path=${encodeURIComponent(selected.value.path)}`;
      const resp = await fetch(url, { credentials: 'include' });
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const json = (await resp.json()) as { result?: { content?: string } };
      const text = json.result?.content ?? '';
      // The /files endpoint returns UTF-8 string content. For images
      // we need raw bytes — the existing endpoint can't deliver
      // those losslessly today (deferred per Stage 4). Fallback:
      // attempt to display SVG (which IS text) inline; everything
      // else surfaces "preview not supported."
      const ext = selected.value.name.split('.').pop()?.toLowerCase();
      if (ext === 'svg') {
        imageDataUrl.value = `data:image/svg+xml;utf8,${encodeURIComponent(text)}`;
      } else {
        imageDataUrl.value = null;
        $q.notify({
          type: 'info',
          position: 'top',
          message: 'Image preview for non-SVG formats lands when binary file reads ship.',
        });
      }
      return;
    }
    const res = await Workspaces.getWorkspaceFile({
      path: { slug: slug.value },
      query: { path: selected.value.path },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Failed to read file.',
      });
      return;
    }
    const content = res.data?.result?.content ?? '';
    editingContent.value = content;
    baseline.value = content;
  } finally {
    contentLoading.value = false;
  }
}

function forceTextView() {
  forceText.value = true;
  void loadSelectedContent();
}

function discardChanges() {
  editingContent.value = baseline.value;
  editing.value = false;
}

async function onSave() {
  if (!slug.value || !selected.value) return;
  saving.value = true;
  try {
    const res = await Workspaces.applyWorkspaceChanges({
      path: { slug: slug.value },
      body: {
        changes: [
          {
            path: selected.value.path,
            action: 'write',
            content: editingContent.value,
          },
        ],
      },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Save failed.',
      });
      return;
    }
    const result = res.data?.result;
    if (!result?.ok) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: result?.error ?? 'Save failed.',
        timeout: 8000,
      });
      return;
    }
    baseline.value = editingContent.value;
    editing.value = false;
    $q.notify({ type: 'positive', position: 'top', message: 'Saved.' });
    await reload();
  } finally {
    saving.value = false;
  }
}

function onNewFile() {
  if (!slug.value) return;
  $q.dialog({
    title: 'New file',
    message:
      currentPath.value.length > 0
        ? `Create a file under <code>${currentPath.value}/</code>. Path can include subfolders (e.g. <code>notes/today.md</code>) — missing parents are created.`
        : 'Create a file at the workspace root. Path can include subfolders — missing parents are created.',
    html: true,
    prompt: {
      model: '',
      type: 'text',
      isValid: (v: string) => v.trim().length > 0 && !v.includes('..'),
      autofocus: true,
      placeholder: 'e.g. notes/today.md',
    },
    ok: { label: 'Create', color: 'primary', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async (rawName: string) => {
    if (!slug.value) return;
    const trimmed = rawName.trim();
    const fullPath = currentPath.value
      ? `${currentPath.value}/${trimmed}`.replace(/\/+/g, '/')
      : trimmed;
    saving.value = true;
    try {
      const res = await Workspaces.applyWorkspaceChanges({
        path: { slug: slug.value },
        body: { changes: [{ path: fullPath, action: 'write', content: '' }] },
      });
      if (res.error) {
        $q.notify({
          type: 'negative',
          position: 'top',
          message: problemMessage(res.error) ?? 'Create failed.',
        });
        return;
      }
      $q.notify({ type: 'positive', position: 'top', message: `Created ${fullPath}.` });
      await reload();
      // Open the newly-created file.
      const file = listing.value?.files.find((f) => f.path === fullPath);
      if (file) await openFile(file);
    } finally {
      saving.value = false;
    }
  });
}

function onDeleteFile(file: WorkspaceFileEntry) {
  if (!slug.value) return;
  $q.dialog({
    title: 'Delete file?',
    message: `Remove <code>${file.path}</code> from the working tree.`,
    html: true,
    ok: { label: 'Delete', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async () => {
    if (!slug.value) return;
    deleting.value = true;
    try {
      const res = await Workspaces.applyWorkspaceChanges({
        path: { slug: slug.value },
        body: { changes: [{ path: file.path, action: 'delete' }] },
      });
      if (res.error) {
        $q.notify({
          type: 'negative',
          position: 'top',
          message: problemMessage(res.error) ?? 'Delete failed.',
        });
        return;
      }
      $q.notify({ type: 'positive', position: 'top', message: `Deleted ${file.path}.` });
      if (selected.value?.path === file.path) {
        selected.value = null;
        editing.value = false;
        editingContent.value = '';
        baseline.value = '';
      }
      await reload();
    } finally {
      deleting.value = false;
    }
  });
}

function onDeleteFolder(folderPath: string) {
  if (!slug.value) return;
  $q.dialog({
    title: 'Delete folder?',
    message:
      `<p>Remove <code>${folderPath}</code> and every file underneath it from the working tree.</p>` +
      `<p>For git workspaces this stages the deletions; click Commit in the header to record them.</p>`,
    html: true,
    ok: { label: 'Delete', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async () => {
    if (!slug.value) return;
    // Recursively collect every file under the folder via the same
    // list endpoint, then dispatch one batched delete. Folders
    // themselves don't have an inode in git so deleting all their
    // files is the deletion. Capped at the listing entry-cap per
    // level — folders that overflow the cap need narrowing first.
    deleting.value = true;
    try {
      const paths = await collectFilesUnder(slug.value, folderPath);
      if (paths.length === 0) {
        $q.notify({
          type: 'info',
          position: 'top',
          message: 'Folder is empty — nothing to delete.',
        });
        return;
      }
      const res = await Workspaces.applyWorkspaceChanges({
        path: { slug: slug.value },
        body: { changes: paths.map((p) => ({ path: p, action: 'delete' as const })) },
      });
      if (res.error) {
        $q.notify({
          type: 'negative',
          position: 'top',
          message: problemMessage(res.error) ?? 'Delete failed.',
        });
        return;
      }
      $q.notify({
        type: 'positive',
        position: 'top',
        message: `Deleted ${paths.length} file${paths.length === 1 ? '' : 's'}.`,
      });
      await reload();
    } finally {
      deleting.value = false;
    }
  });
}

async function collectFilesUnder(workspaceSlug: string, root: string): Promise<string[]> {
  const out: string[] = [];
  async function walk(p: string) {
    const res = await Workspaces.listWorkspaceFolder({
      path: { slug: workspaceSlug },
      query: { path: p },
    });
    const result = res.data?.result;
    if (!result) return;
    for (const f of result.files) out.push(f.path);
    for (const sub of result.folders) await walk(sub.path);
  }
  await walk(root);
  return out;
}

function iconForKind(kind: string): string {
  switch (kind) {
    case 'text':
      return 'description';
    case 'image':
      return 'image';
    case 'binary':
      return 'data_object';
    default:
      return 'insert_drive_file';
  }
}

function languageForFile(name: string): string {
  const ext = name.split('.').pop()?.toLowerCase();
  switch (ext) {
    case 'md':
      return 'markdown';
    case 'json':
    case 'jsonc':
      return 'json';
    case 'yaml':
    case 'yml':
      return 'yaml';
    case 'ts':
    case 'tsx':
      return 'typescript';
    case 'js':
    case 'jsx':
      return 'javascript';
    case 'vue':
      return 'html';
    case 'cs':
      return 'csharp';
    case 'css':
      return 'css';
    case 'scss':
    case 'sass':
      return 'scss';
    case 'sql':
      return 'sql';
    case 'sh':
    case 'bash':
    case 'zsh':
      return 'shell';
    case 'py':
      return 'python';
    case 'rs':
      return 'rust';
    case 'go':
      return 'go';
    default:
      return 'plaintext';
  }
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes}B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)}KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)}MB`;
}

function problemMessage(err: unknown): string | undefined {
  if (err && typeof err === 'object') {
    const e = err as { detail?: unknown; title?: unknown };
    if (typeof e.detail === 'string' && e.detail.length) return e.detail;
    if (typeof e.title === 'string' && e.title.length) return e.title;
  }
  return undefined;
}

onMounted(() => {
  if (slug.value && listing.value === null && !loading.value) void reload();
});
</script>

<style lang="scss" scoped>
.cr-fm {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 360px;
}

.cr-fm-header {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--cr-border-subtle);
  background: var(--cr-bg-elevated);
}

.cr-fm-crumbs {
  font-size: 12px;
  color: var(--cr-fg-secondary);
}

.cr-fm-crumb {
  cursor: pointer;
  color: var(--cr-fg-secondary);

  &:hover {
    color: var(--cr-fg-primary);
  }
}

.cr-fm-crumb--last {
  color: var(--cr-fg-primary);
  font-weight: 500;
}

.cr-fm-body {
  display: flex;
  flex: 1;
  min-height: 0;
}

.cr-fm-list {
  width: 320px;
  flex-shrink: 0;
  overflow-y: auto;
  border-right: 1px solid var(--cr-border-subtle);
  background: var(--cr-bg-surface);
}

.cr-fm-empty {
  padding: 24px 16px;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
  text-align: center;
}

.cr-fm-rows {
  list-style: none;
  margin: 0;
  padding: 4px 0;
}

.cr-fm-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 12px;
  cursor: pointer;
  font-size: 12px;
  color: var(--cr-fg-primary);
  user-select: none;

  &:hover {
    background: var(--cr-bg-hover);
  }
}

.cr-fm-row--active {
  background: var(--cr-brand-tint-soft);
  color: var(--cr-fg-primary);
}

.cr-fm-row-icon {
  color: var(--cr-fg-secondary);
  flex-shrink: 0;
}

.cr-fm-row-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cr-fm-row-size {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  color: var(--cr-fg-tertiary);
  flex-shrink: 0;
}

.cr-fm-row-chev {
  color: var(--cr-fg-tertiary);
}

.cr-fm-truncated {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 12px;
  font-size: 11px;
  color: var(--cr-fg-secondary);
  background: var(--cr-bg-elevated);
  border-top: 1px solid var(--cr-border-subtle);
}

.cr-fm-pane {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: var(--cr-bg-page);
}

.cr-fm-pane-empty {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--cr-fg-tertiary);
  font-size: 13px;
}

.cr-fm-pane-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--cr-border-subtle);
  background: var(--cr-bg-surface);
}

.cr-fm-pane-path {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
  color: var(--cr-fg-secondary);
  background: var(--cr-bg-elevated);
  padding: 1px 6px;
  border-radius: 3px;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cr-fm-pane-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.cr-fm-pane-loading {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
}

.cr-fm-monaco {
  flex: 1;
  min-height: 280px;
}

.cr-fm-image-frame {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
  overflow: auto;
}

.cr-fm-image {
  max-width: 100%;
  max-height: 80vh;
}

.cr-fm-binary {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  font-size: 13px;
  color: var(--cr-fg-secondary);
}

.cr-fm-binary-meta {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
}
</style>
