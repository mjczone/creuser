<template>
  <section class="cr-conv-rels">
    <header class="cr-conv-rels-header">
      <span class="cr-conv-rels-title">Relationships</span>
      <q-space />
      <q-btn
        unelevated
        no-caps
        dense
        size="sm"
        color="primary"
        icon="add"
        label="Add rule"
        :disable="!conventionId"
        @click="openAddDialog"
      />
    </header>

    <div v-if="rules.length === 0" class="cr-conv-rels-empty">
      No relationship rules. Click <strong>Add rule</strong> to declare an edge that the CDFS file
      manager will render as a navigable folder under matching entities.
    </div>

    <ul v-else class="cr-conv-rels-list">
      <li v-for="r in rules" :key="r.kind" class="cr-conv-rels-item">
        <div class="cr-conv-rels-item-row">
          <q-icon :name="r.icon || 'link'" size="14px" class="cr-conv-rels-item-icon" />
          <code class="cr-conv-rels-item-kind">{{ r.kind }}</code>
          <span class="cr-conv-rels-item-name">{{ r.name }}</span>
          <q-space />
          <q-btn
            flat
            dense
            round
            size="sm"
            icon="delete_outline"
            :loading="busyKind === r.kind"
            aria-label="Remove rule"
            @click="onRemove(r)"
          >
            <q-tooltip>Remove this rule</q-tooltip>
          </q-btn>
        </div>
        <div class="cr-conv-rels-item-meta">
          <span>
            <strong>source:</strong>
            <code> {{ r.sourceKind }}{{ r.sourceKey ? `.${r.sourceKey}` : '' }} </code>
          </span>
          <span v-if="r.filterKind">
            <strong>filter:</strong>
            <code>{{ r.filterKind }}={{ r.filterPattern }}</code>
          </span>
          <span
            ><strong>interpret:</strong> <code>{{ r.interpret }}</code></span
          >
          <span>
            <strong>target:</strong>
            <code>{{ r.targetKindAny ? 'any' : r.targetKindAllowed.join(',') || '?' }}</code>
          </span>
          <span v-if="r.inverse">
            <strong>inverse:</strong>
            <code>{{ r.inverse }}</code>
            <span v-if="r.inverseName" class="cr-conv-rels-item-inverse-name">
              ({{ r.inverseName }})
            </span>
          </span>
        </div>
      </li>
    </ul>

    <q-dialog v-model="addOpen" persistent>
      <q-card class="cr-conv-rels-dialog">
        <q-card-section class="cr-conv-rels-dialog-header">
          <div class="text-subtitle1">Add relationship</div>
          <p class="cr-conv-rels-dialog-sub">
            Validates against the convention schema before writing. Cancel any time without
            persisting.
          </p>
        </q-card-section>

        <q-card-section class="cr-conv-rels-dialog-body">
          <q-input
            v-model="form.kind"
            dense
            outlined
            label="kind (snake_case)"
            hint="Edge label written to entity_refs.relationship."
            :rules="[(v: string) => !!v.trim() || 'Required']"
          />
          <q-input
            v-model="form.name"
            dense
            outlined
            label="name"
            hint="CDFS folder name (defaulted from kind when empty)."
          />
          <q-input
            v-model="form.source"
            dense
            outlined
            label="source"
            hint="frontmatter.related / glob:packages/db/** / path-template:{file_dir}/index.md"
            :rules="[(v: string) => !!v.trim() || 'Required']"
          />
          <q-select
            v-model="form.interpret"
            dense
            outlined
            label="interpret"
            :options="interpretOptions"
            emit-value
            map-options
          />
          <q-input
            v-model="form.targetKind"
            dense
            outlined
            label="target_kind"
            hint="any | <kind> | k1,k2,k3"
          />
          <q-input
            v-model="form.inverse"
            dense
            outlined
            label="inverse (optional)"
            hint="Reverse edge label. Same as kind for symmetric."
          />
          <q-input
            v-model="form.inverseName"
            dense
            outlined
            label="inverse_name (optional)"
            hint="CDFS folder name for the reverse edge."
          />

          <q-expansion-item
            label="Advanced (filter, icon, order, description)"
            switch-toggle-side
            class="cr-conv-rels-dialog-advanced"
          >
            <q-input v-model="form.icon" dense outlined label="icon (optional)" />
            <q-input v-model="form.description" dense outlined label="description (optional)" />
            <q-input
              v-model.number="form.order"
              type="number"
              dense
              outlined
              label="order (default 100)"
            />
            <q-select
              v-model="form.filterKind"
              dense
              outlined
              clearable
              label="filter.kind (optional)"
              :options="['glob', 'regex', 'type']"
            />
            <q-input
              v-if="form.filterKind"
              v-model="form.filterPattern"
              dense
              outlined
              label="filter.pattern"
              hint="docs/ADR/**/*.md (glob), ^v\\d+/ (regex), or url/path/glob/slug (type)."
            />
          </q-expansion-item>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" :disable="adding" @click="closeAddDialog" />
          <q-btn
            unelevated
            no-caps
            color="primary"
            label="Add"
            :loading="adding"
            :disable="!form.kind.trim() || !form.source.trim()"
            @click="onAdd"
          />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </section>
</template>

<script setup lang="ts">
/**
 * Structured relationship-rule editor. Renders the convention's existing
 * rules and adds/removes via the new authoring ops (`addConventionRelationship`,
 * `removeConventionRelationship`) — no YAML round-trips on the client.
 *
 * v1 supports add + remove. Update flows through "remove + add" today; an
 * inline edit lands later when there's a clear UX pattern for it.
 */
import { computed, reactive, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { Projections } from 'src/api';
import type { ConventionRelationshipSummary } from 'src/api';

const props = defineProps<{
  workspaceSlug: string;
  conventionId: string | null;
  relationships: ConventionRelationshipSummary[];
}>();

const emit = defineEmits<{
  (e: 'changed'): void;
}>();

const $q = useQuasar();

const rules = computed<ConventionRelationshipSummary[]>(() =>
  [...props.relationships].sort(
    (a, b) => Number(a.order) - Number(b.order) || a.kind.localeCompare(b.kind),
  ),
);

const busyKind = ref<string | null>(null);
const addOpen = ref(false);
const adding = ref(false);

interface FormState {
  kind: string;
  name: string;
  source: string;
  interpret: string;
  targetKind: string;
  inverse: string;
  inverseName: string;
  icon: string;
  description: string;
  order: number | null;
  filterKind: string | null;
  filterPattern: string;
}

const form = reactive<FormState>(emptyForm());

const interpretOptions = [
  { label: 'auto (sniff URL → glob → path → slug)', value: 'auto' },
  { label: 'path', value: 'path' },
  { label: 'slug', value: 'slug' },
  { label: 'glob', value: 'glob' },
  { label: 'url', value: 'url' },
];

watch(
  () => props.conventionId,
  () => {
    closeAddDialog();
  },
);

function emptyForm(): FormState {
  return {
    kind: '',
    name: '',
    source: '',
    interpret: 'auto',
    targetKind: 'any',
    inverse: '',
    inverseName: '',
    icon: '',
    description: '',
    order: null,
    filterKind: null,
    filterPattern: '',
  };
}

function openAddDialog() {
  Object.assign(form, emptyForm());
  addOpen.value = true;
}

function closeAddDialog() {
  addOpen.value = false;
}

function buildTargetKind(raw: string): string | string[] | undefined {
  const v = raw.trim();
  if (!v) return undefined;
  if (v.includes(','))
    return v
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean);
  return v;
}

function buildFilter(): { kind: string; pattern: string } | undefined {
  if (!form.filterKind || !form.filterPattern.trim()) return undefined;
  return { kind: form.filterKind, pattern: form.filterPattern.trim() };
}

async function onAdd() {
  if (!props.conventionId) return;
  adding.value = true;
  try {
    const res = await Projections.addConventionRelationship({
      path: { slug: props.workspaceSlug, id: props.conventionId },
      body: {
        kind: form.kind.trim(),
        name: form.name.trim() || null,
        icon: form.icon.trim() || null,
        description: form.description.trim() || null,
        order: form.order ?? null,
        source: form.source.trim(),
        filter: buildFilter(),
        interpret: form.interpret,
        targetKind: buildTargetKind(form.targetKind),
        inverse: form.inverse.trim() || null,
        inverseName: form.inverseName.trim() || null,
        inverseIcon: null,
        metadata: null,
      },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Add failed.',
      });
      return;
    }
    $q.notify({
      type: 'positive',
      position: 'top',
      message: `Added '${form.kind.trim()}' to ${props.conventionId}.`,
      timeout: 2500,
    });
    closeAddDialog();
    emit('changed');
  } finally {
    adding.value = false;
  }
}

function onRemove(r: ConventionRelationshipSummary) {
  if (!props.conventionId) return;
  $q.dialog({
    title: `Remove ${r.kind}?`,
    message: `<p>Drop the <code>${r.kind}</code> rule from <code>${props.conventionId}</code>.</p><p>This rewrites the convention YAML and re-fires projection-sync. Affected refs disappear from the projection on the next sync.</p>`,
    html: true,
    ok: { label: 'Remove', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
  }).onOk(async () => {
    if (!props.conventionId) return;
    busyKind.value = r.kind;
    try {
      const res = await Projections.removeConventionRelationship({
        path: { slug: props.workspaceSlug, id: props.conventionId, kind: r.kind },
      });
      if (res.error) {
        $q.notify({
          type: 'negative',
          position: 'top',
          message: problemMessage(res.error) ?? 'Remove failed.',
        });
        return;
      }
      $q.notify({
        type: 'positive',
        position: 'top',
        message: `Removed '${r.kind}'.`,
        timeout: 2000,
      });
      emit('changed');
    } finally {
      busyKind.value = null;
    }
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
</script>

<style lang="scss" scoped>
.cr-conv-rels {
  border: 1px solid var(--cr-border-subtle);
  background: var(--cr-bg-surface);
  border-radius: 4px;
  padding: 10px 12px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.cr-conv-rels-header {
  display: flex;
  align-items: center;
  gap: 6px;
}

.cr-conv-rels-title {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
}

.cr-conv-rels-empty {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  font-style: italic;
  padding: 4px 0;
}

.cr-conv-rels-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.cr-conv-rels-item {
  border: 1px solid var(--cr-border-faint);
  border-radius: 3px;
  background: var(--cr-bg-page);
  padding: 6px 8px;
  font-size: 11px;
}

.cr-conv-rels-item-row {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 4px;
}

.cr-conv-rels-item-icon {
  color: var(--cr-fg-secondary);
}

.cr-conv-rels-item-kind {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  background: var(--cr-bg-elevated);
  padding: 1px 5px;
  border-radius: 3px;
}

.cr-conv-rels-item-name {
  font-weight: 500;
  color: var(--cr-fg-primary);
}

.cr-conv-rels-item-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 12px;
  font-size: 10px;
  color: var(--cr-fg-secondary);
  margin-left: 18px;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 10px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }

  strong {
    font-weight: 500;
    color: var(--cr-fg-tertiary);
    margin-right: 2px;
  }
}

.cr-conv-rels-item-inverse-name {
  color: var(--cr-fg-tertiary);
  font-style: italic;
  margin-left: 4px;
}

.cr-conv-rels-dialog {
  min-width: 480px;
  max-width: 600px;
}

.cr-conv-rels-dialog-header {
  padding-bottom: 4px;
}

.cr-conv-rels-dialog-sub {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  margin: 4px 0 0;
}

.cr-conv-rels-dialog-body {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding-top: 8px;
}

.cr-conv-rels-dialog-advanced {
  border-top: 1px solid var(--cr-border-subtle);
  padding-top: 6px;

  ::v-deep(.q-expansion-item__content) {
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 10px 0 4px;
  }
}
</style>
