<template>
  <div class="cr-logo-upload">
    <div
      class="cr-logo-dropzone"
      :class="{ 'cr-logo-dropzone--drag': isDragging }"
      @click="onPickFile"
      @keydown.enter="onPickFile"
      @keydown.space.prevent="onPickFile"
      @dragenter.prevent="isDragging = true"
      @dragover.prevent="isDragging = true"
      @dragleave.prevent="isDragging = false"
      @drop.prevent="onDrop"
      tabindex="0"
      role="button"
      :aria-label="modelValue ? 'Replace logo' : 'Upload logo'"
    >
      <img v-if="modelValue" :src="modelValue" :alt="alt" class="cr-logo-preview" />
      <div v-else class="cr-logo-placeholder">
        <q-icon name="image" size="32px" />
        <span>Drop a logo here, or click to browse</span>
      </div>

      <q-spinner v-if="isUploading" class="cr-logo-spinner" size="24px" />
    </div>

    <div class="cr-logo-actions">
      <q-btn
        flat
        dense
        no-caps
        icon="upload"
        :label="modelValue ? 'Replace' : 'Upload'"
        :disable="isUploading"
        @click="onPickFile"
      />
      <q-btn
        v-if="modelValue"
        flat
        dense
        no-caps
        icon="close"
        label="Remove"
        :disable="isUploading"
        @click="onClear"
      />
    </div>

    <p v-if="error" class="cr-logo-error">{{ error }}</p>
    <p v-else class="cr-logo-hint">
      PNG, JPG, WebP, SVG, ICO. Up to 2 MB. Used for the sidebar logo, login screen, and the browser
      tab favicon.
    </p>

    <input
      ref="fileInput"
      type="file"
      accept="image/png,image/jpeg,image/webp,image/svg+xml,image/x-icon,.ico"
      class="cr-logo-file-input"
      @change="onFileChange"
    />
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { Branding } from 'src/api';

interface Props {
  modelValue: string | null;
  alt?: string;
}

withDefaults(defineProps<Props>(), { alt: 'Logo' });
const emit = defineEmits<{
  'update:modelValue': [value: string | null];
}>();

const fileInput = ref<HTMLInputElement | null>(null);
const isDragging = ref(false);
const isUploading = ref(false);
const error = ref('');

function onPickFile() {
  if (isUploading.value) return;
  fileInput.value?.click();
}

function onFileChange(e: Event) {
  const target = e.target as HTMLInputElement;
  const file = target.files?.[0];
  if (file) void uploadFile(file);
  // Allow re-uploading the same file (browsers suppress change events otherwise).
  target.value = '';
}

function onDrop(e: DragEvent) {
  isDragging.value = false;
  const file = e.dataTransfer?.files[0];
  if (file) void uploadFile(file);
}

async function uploadFile(file: File) {
  error.value = '';
  isUploading.value = true;
  try {
    const res = await Branding.uploadLogo({ body: { file } });
    if (res.error) throw new Error(toErrorMessage(res.error));
    const url = res.data?.result?.url;
    if (!url) throw new Error('Upload returned no URL.');
    emit('update:modelValue', url);
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Upload failed.';
  } finally {
    isUploading.value = false;
  }
}

function onClear() {
  emit('update:modelValue', null);
}

function toErrorMessage(err: unknown): string {
  if (err && typeof err === 'object' && 'detail' in err) {
    const d = (err as { detail?: unknown }).detail;
    if (typeof d === 'string') return d;
  }
  if (err && typeof err === 'object' && 'title' in err) {
    const t = (err as { title?: unknown }).title;
    if (typeof t === 'string') return t;
  }
  return 'Upload failed.';
}
</script>

<style lang="scss" scoped>
.cr-logo-upload {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: 360px;
}

.cr-logo-dropzone {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 160px;
  height: 160px;
  border: 1px dashed var(--cr-border-default);
  border-radius: 6px;
  background: var(--cr-bg-elevated);
  cursor: pointer;
  outline: none;
  transition:
    border-color 80ms ease-out,
    background 80ms ease-out;

  &:hover,
  &:focus-visible {
    border-color: var(--q-primary);
  }
}

.cr-logo-dropzone--drag {
  border-color: var(--q-primary);
  background: var(--cr-brand-tint-soft);
}

.cr-logo-preview {
  max-width: 80%;
  max-height: 80%;
  object-fit: contain;
}

.cr-logo-placeholder {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
  padding: 16px;
  text-align: center;
}

.cr-logo-spinner {
  position: absolute;
  bottom: 8px;
  right: 8px;
  color: var(--q-primary);
}

.cr-logo-actions {
  display: flex;
  gap: 4px;
}

.cr-logo-hint {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  margin: 0;
}

.cr-logo-error {
  font-size: 11px;
  color: var(--q-negative);
  margin: 0;
}

.cr-logo-file-input {
  position: absolute;
  width: 1px;
  height: 1px;
  opacity: 0;
  pointer-events: none;
}
</style>
