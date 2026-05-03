<template>
  <div class="cr-secret-input">
    <div class="cr-secret-input-label-row">
      <label class="cr-secret-input-label">{{ label }}</label>
      <q-chip
        v-if="present"
        dense
        outline
        color="positive"
        text-color="positive"
        class="cr-secret-input-chip"
      >
        Set
      </q-chip>
      <q-chip
        v-else
        dense
        outline
        :color="optional ? 'grey-6' : 'warning'"
        :text-color="optional ? 'grey-6' : 'warning'"
        class="cr-secret-input-chip"
      >
        {{ optional ? 'Optional' : 'Not set' }}
      </q-chip>
    </div>

    <!-- View mode: secret is set and we're not editing. Shows a fixed mask
         so visitors can't shoulder-surf the length and the value never
         reaches the DOM. The Edit button switches to edit mode. -->
    <div v-if="present && mode === 'view'" class="cr-secret-input-row">
      <q-input
        :model-value="MASK"
        readonly
        outlined
        dense
        class="cr-secret-input-field cr-secret-input-mask"
      />
      <q-btn
        no-caps
        unelevated
        color="primary"
        icon="edit"
        label="Edit"
        @click="enterEdit"
      />
      <q-btn
        flat
        no-caps
        color="negative"
        icon="close"
        label="Clear"
        :disable="saving"
        @click="onClear"
      />
    </div>

    <!-- Edit mode: actual input where the admin types a new value. Same
         path as the not-set case below. Cancel reverts to view mode without
         touching the saved value. -->
    <div v-else class="cr-secret-input-row">
      <q-input
        ref="inputRef"
        v-model="value"
        :type="reveal ? 'text' : 'password'"
        :placeholder="placeholder"
        :hint="hint ?? defaultHint"
        outlined
        dense
        autocomplete="off"
        class="cr-secret-input-field"
        @keydown.enter.prevent="onSave"
        @keydown.escape="cancelEdit"
      >
        <template #append>
          <q-icon
            :name="reveal ? 'visibility_off' : 'visibility'"
            class="cursor-pointer"
            @click="reveal = !reveal"
          />
        </template>
      </q-input>

      <q-btn
        no-caps
        unelevated
        color="primary"
        icon="save"
        :label="present ? 'Replace' : 'Save'"
        :disable="!value.trim() || saving"
        :loading="saving"
        @click="onSave"
      />

      <q-btn
        v-if="present"
        flat
        no-caps
        icon="cancel"
        label="Cancel"
        :disable="saving"
        @click="cancelEdit"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, useTemplateRef, watch } from 'vue';
import type { QInput } from 'quasar';
import { useQuasar } from 'quasar';
import { Environment } from 'src/api';

interface Props {
  label: string;
  /** Filename under /data/secrets/ (e.g. `anthropic.key`). Stored on the wire as the lookup key. */
  name: string;
  /** Whether the on-disk file currently has a value. Drives the Set/Not set chip + view-mode mask. */
  present: boolean;
  /**
   * Set true when the consumer doesn't actually need this secret — e.g.
   * a local LLM that doesn't authenticate. Changes the empty-state chip
   * from a warning-toned "Not set" to a muted "Optional" so admins don't
   * read it as a missing-required-field alarm.
   */
  optional?: boolean;
  hint?: string;
  placeholder?: string;
}

const props = defineProps<Props>();
const emit = defineEmits<{
  (e: 'saved'): void;
  (e: 'cleared'): void;
}>();

const $q = useQuasar();

// Visual mask shown in view mode. Length is fixed (10 chars) so it doesn't
// hint at the real secret's length to anyone watching.
const MASK = '•'.repeat(10);

const value = ref('');
const reveal = ref(false);
const saving = ref(false);
const mode = ref<'view' | 'edit'>(props.present ? 'view' : 'edit');
const inputRef = useTemplateRef<QInput>('inputRef');

// When the parent re-renders with a flipped `present` (after save / clear /
// initial load), reset to the appropriate mode and drop any in-progress draft.
watch(
  () => props.present,
  (next) => {
    mode.value = next ? 'view' : 'edit';
    value.value = '';
    reveal.value = false;
  },
);

const defaultHint = computed(
  () =>
    `Stored at /data/secrets/${props.name} (chmod 600). Server-side only — never returned by the API.`,
);

function enterEdit() {
  mode.value = 'edit';
  value.value = '';
  reveal.value = false;
  void nextTick(() => inputRef.value?.focus());
}

function cancelEdit() {
  // Only meaningful when a value is already set; for not-set state there's
  // nothing to revert to.
  if (!props.present) return;
  mode.value = 'view';
  value.value = '';
  reveal.value = false;
}

async function onSave() {
  if (!value.value.trim()) return;
  saving.value = true;
  try {
    const res = await Environment.setEnvironmentSecret({
      path: { name: props.name },
      body: { value: value.value },
    });
    if (res.error) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? `Failed to save ${props.label}.`,
      });
      return;
    }
    value.value = '';
    reveal.value = false;
    mode.value = 'view';
    $q.notify({ type: 'positive', position: 'top', message: `${props.label} saved.` });
    emit('saved');
  } finally {
    saving.value = false;
  }
}

function onClear() {
  $q
    .dialog({
      title: `Clear ${props.label}?`,
      message:
        'The value will be removed from disk. Anything that depends on this secret ' +
        '(API calls, SMTP) will fail until you set it again.',
      ok: { label: 'Clear', color: 'negative', unelevated: true, noCaps: true },
      cancel: { flat: true, noCaps: true },
      persistent: true,
    })
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
    .onOk(async () => {
      saving.value = true;
      try {
        const res = await Environment.deleteEnvironmentSecret({
          path: { name: props.name },
        });
        if (res.error) {
          $q.notify({
            type: 'negative',
            position: 'top',
            message: problemMessage(res.error) ?? `Failed to clear ${props.label}.`,
          });
          return;
        }
        $q.notify({ type: 'positive', position: 'top', message: `${props.label} cleared.` });
        emit('cleared');
      } finally {
        saving.value = false;
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
.cr-secret-input {
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-width: 600px;
}

.cr-secret-input-label-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.cr-secret-input-label {
  font-size: 12px;
  font-weight: 500;
  color: var(--cr-fg-secondary);
}

.cr-secret-input-chip {
  font-size: 10px;
}

.cr-secret-input-row {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}

.cr-secret-input-field {
  flex: 1;
  min-width: 0;
}

// Subtle visual cue that the masked field isn't editable — same input
// chrome but a slightly muted text color so the dots don't read as
// real content.
.cr-secret-input-mask :deep(input) {
  font-family: var(--cr-font-family-mono);
  letter-spacing: 0.15em;
  color: var(--cr-fg-tertiary);
  cursor: default;
}
</style>
