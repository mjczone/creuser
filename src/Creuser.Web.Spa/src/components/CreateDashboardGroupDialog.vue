<template>
  <q-dialog ref="dialogRef" persistent>
    <q-card class="cr-create">
      <q-card-section class="cr-create-header">
        <h2 class="cr-create-title">New dashboard group</h2>
        <q-space />
        <q-btn flat dense round icon="close" @click="cancel" />
      </q-card-section>

      <q-card-section class="cr-create-body">
        <p class="cr-create-help">
          Groups are folders in the workspace icon bar. Click the group's icon to expand its
          dashboards in the sub-sidebar.
        </p>
        <q-input
          v-model="name"
          label="Name"
          autofocus
          outlined
          dense
          :error="!!nameError"
          :error-message="nameError"
          @update:model-value="onNameInput"
          @blur="touched.name = true"
        />
        <q-input
          v-model="slug"
          label="Slug"
          outlined
          dense
          hint="URL-stable identifier; lowercase letters, numbers, hyphens"
          :error="!!slugError"
          :error-message="slugError"
          @blur="touched.slug = true"
        />
        <q-input
          v-model="icon"
          label="Icon (Material icon name)"
          outlined
          dense
          :error="!!iconError"
          :error-message="iconError"
          hint="Required — e.g. bolt, analytics, layers"
          @blur="touched.icon = true"
        >
          <template #prepend>
            <q-icon :name="icon || 'folder'" />
          </template>
        </q-input>
        <p v-if="error" class="cr-create-error">{{ error }}</p>
      </q-card-section>

      <q-card-actions align="right" class="cr-create-actions">
        <q-btn flat label="Cancel" @click="cancel" />
        <q-btn
          unelevated
          color="primary"
          label="Create"
          :loading="submitting"
          :disable="!canSubmit"
          @click="confirm"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<script setup lang="ts">
/**
 * Modal for creating a new dashboard group. Same form-shape conventions
 * as CreateDashboardDialog (auto-slug-from-name until manually edited,
 * touched-state-driven validation, error surfacing from hey-api).
 */
import { computed, ref, watch } from 'vue';
import { useDialogPluginComponent } from 'quasar';
import { DashboardGroups } from 'src/api';

const props = defineProps<{ workspaceSlug: string }>();
defineEmits([...useDialogPluginComponent.emits]);
const { dialogRef, onDialogOK, onDialogCancel } = useDialogPluginComponent();

const name = ref('');
const slug = ref('');
const icon = ref('');
const slugTouchedManually = ref(false);
const touched = ref({ name: false, slug: false, icon: false });
const submitting = ref(false);
const error = ref<string | null>(null);

const slugRegex = /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/;

const nameError = computed<string | undefined>(() => {
  if (!touched.value.name) return undefined;
  if (!name.value.trim()) return 'Name is required.';
  return undefined;
});
const slugError = computed<string | undefined>(() => {
  if (!touched.value.slug && !slugTouchedManually.value) return undefined;
  if (!slug.value.trim()) return 'Slug is required.';
  if (!slugRegex.test(slug.value)) return 'Lowercase letters, numbers, hyphens only.';
  return undefined;
});
const iconError = computed<string | undefined>(() => {
  if (!touched.value.icon) return undefined;
  if (!icon.value.trim()) return 'Icon is required.';
  return undefined;
});

const canSubmit = computed(
  () =>
    !submitting.value &&
    name.value.trim().length > 0 &&
    slug.value.trim().length > 0 &&
    slugRegex.test(slug.value) &&
    icon.value.trim().length > 0,
);

function onNameInput(value: string | number | null) {
  if (slugTouchedManually.value) return;
  slug.value = slugify(String(value ?? ''));
}

function slugify(s: string): string {
  return s
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/(^-|-$)/g, '')
    .slice(0, 64);
}

watch(slug, (next, prev) => {
  if (prev !== '' && next !== slugify(name.value)) {
    slugTouchedManually.value = true;
  }
});

function cancel() {
  onDialogCancel();
}

async function confirm() {
  if (!canSubmit.value) return;
  submitting.value = true;
  error.value = null;
  try {
    const res = await DashboardGroups.createDashboardGroup({
      path: { slug: props.workspaceSlug },
      body: {
        slug: slug.value,
        name: name.value.trim(),
        icon: icon.value.trim(),
        position: null,
      },
    });
    if (res.error) {
      const detail =
        (res.error as { detail?: string; title?: string })?.detail ??
        (res.error as { title?: string })?.title ??
        'Failed to create group.';
      error.value = detail;
      submitting.value = false;
      return;
    }
    onDialogOK(res.data?.result);
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : 'Failed to create group.';
    submitting.value = false;
  }
}
</script>

<style lang="scss" scoped>
.cr-create {
  width: min(480px, 90vw);
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-elevated, #1a1a1d);
}

.cr-create-header {
  display: flex;
  align-items: center;
  padding: 12px 16px;
  border-bottom: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
}

.cr-create-title {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-create-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px;
}

.cr-create-help {
  margin: 0 0 4px;
  font-size: 12px;
  color: var(--cr-fg-tertiary, #888);
  line-height: 1.5;
}

.cr-create-actions {
  border-top: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
  padding: 8px 12px;
}

.cr-create-error {
  margin: 0;
  font-size: 12px;
  color: rgb(248, 113, 113);
  background: rgba(239, 68, 68, 0.08);
  padding: 6px 8px;
  border-radius: 4px;
  border-left: 2px solid rgb(248, 113, 113);
}
</style>
