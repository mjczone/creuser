<template>
  <div class="cr-color-field">
    <label class="cr-color-label">{{ label }}</label>
    <div class="cr-color-row">
      <button
        type="button"
        class="cr-color-swatch"
        :style="swatchStyle"
        :aria-label="`Pick ${label}`"
      >
        <q-popup-proxy cover transition-show="scale" transition-hide="scale">
          <q-color
            :model-value="modelValue || '#000000ff'"
            no-header-tabs
            format-model="auto"
            @update:model-value="(v: string | null) => emit('update:modelValue', v ?? '')"
          />
        </q-popup-proxy>
      </button>
      <q-input
        :model-value="modelValue"
        dense
        outlined
        placeholder="#rrggbb or rgba(...)"
        class="cr-color-input"
        :rules="[validateColor]"
        hide-bottom-space
        @update:model-value="onTextChange"
      />
      <q-btn
        v-if="modelValue"
        flat
        dense
        round
        icon="close"
        size="xs"
        :aria-label="`Clear ${label}`"
        @click="emit('update:modelValue', '')"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';

interface Props {
  label: string;
  modelValue: string;
}

const props = defineProps<Props>();
const emit = defineEmits<{
  'update:modelValue': [value: string];
}>();

const swatchStyle = computed(() =>
  isValidColor(props.modelValue) ? { backgroundColor: props.modelValue } : {},
);

function onTextChange(v: string | number | null) {
  emit('update:modelValue', String(v ?? ''));
}

function validateColor(v: string): true | string {
  if (!v) return true;
  return isValidColor(v) || 'Use #rrggbb, #rrggbbaa, or rgba(...)';
}

// Defer to the browser's CSS color parser — covers hex (3/4/6/8), rgb/rgba,
// hsl/hsla, named colors, and any future syntax. The trick: assign the
// candidate to `style.color` on a throwaway element; if it sticks, it's a
// valid color. Empty string back means the parser rejected it.
function isValidColor(v: string): boolean {
  if (!v.trim()) return false;
  const probe = document.createElement('span');
  probe.style.color = '';
  probe.style.color = v;
  return probe.style.color !== '';
}
</script>

<style lang="scss" scoped>
.cr-color-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.cr-color-label {
  font-size: 12px;
  font-weight: 500;
  color: var(--cr-fg-secondary);
}

.cr-color-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.cr-color-swatch {
  width: 28px;
  height: 28px;
  border-radius: 4px;
  border: 1px solid var(--cr-border-default);
  cursor: pointer;
  flex-shrink: 0;
  background-image:
    linear-gradient(45deg, var(--cr-bg-elevated) 25%, transparent 25%),
    linear-gradient(-45deg, var(--cr-bg-elevated) 25%, transparent 25%),
    linear-gradient(45deg, transparent 75%, var(--cr-bg-elevated) 75%),
    linear-gradient(-45deg, transparent 75%, var(--cr-bg-elevated) 75%);
  background-size: 8px 8px;
  background-position:
    0 0,
    0 4px,
    4px -4px,
    -4px 0;
  padding: 0;

  &:focus-visible {
    outline: 2px solid var(--q-primary);
    outline-offset: 2px;
  }
}

.cr-color-input {
  flex: 1;
  min-width: 0;
}
</style>
