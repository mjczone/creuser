<template>
  <div class="cr-font-picker">
    <q-select
      :model-value="selectedId"
      :options="options"
      :label="label"
      option-value="id"
      option-label="label"
      emit-value
      map-options
      dense
      outlined
      class="cr-font-picker-select"
      @update:model-value="onSelect"
    >
      <template #selected>
        <span :style="selectedFontStyle">{{ selectedLabel }}</span>
      </template>
      <template #option="scope">
        <q-item v-bind="scope.itemProps" dense>
          <q-item-section>
            <q-item-label :style="optionFontStyle(scope.opt)">
              {{ scope.opt.label }}
            </q-item-label>
            <q-item-label v-if="scope.opt.id === 'custom'" caption class="cr-font-picker-caption">
              Paste a CSS font-family list
            </q-item-label>
          </q-item-section>
        </q-item>
      </template>
    </q-select>
    <q-input
      v-if="selectedId === 'custom'"
      :model-value="customValue"
      :placeholder="customPlaceholder"
      dense
      outlined
      class="cr-font-picker-custom"
      @update:model-value="onCustomChange"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import {
  fontsForType,
  loadIfBundled,
  lookupFont,
  type FontType,
} from 'src/css/fonts/registry';

interface Props {
  modelValue: string;
  type: FontType;
  label: string;
}

const props = defineProps<Props>();
const emit = defineEmits<{
  'update:modelValue': [value: string];
}>();

interface Option {
  id: string;
  label: string;
  cssFamily: string;
}

const CUSTOM: Option = { id: 'custom', label: 'Custom font-family…', cssFamily: '' };

const options = computed<Option[]>(() => [
  ...fontsForType(props.type).map((f) => ({
    id: f.id,
    label: f.label,
    cssFamily: f.cssFamily,
  })),
  CUSTOM,
]);

const customPlaceholder = computed(() =>
  props.type === 'mono'
    ? 'e.g. "JetBrains Mono", Courier, monospace'
    : 'e.g. "Inter", system-ui, sans-serif',
);

// Look up which option matches the current modelValue.
//   empty string  → first option (system default)
//   matches a known recipe → that option
//   else → custom (and customValue holds the raw string)
const selectedOption = computed<Option>(() => {
  if (!props.modelValue) return options.value[0]!;
  const match = lookupFont(props.modelValue, props.type);
  if (match) return options.value.find((o) => o.id === match.id)!;
  return CUSTOM;
});

const selectedId = computed(() => selectedOption.value.id);
const selectedLabel = computed(() =>
  selectedId.value === 'custom' ? 'Custom' : selectedOption.value.label,
);
const selectedFontStyle = computed(() =>
  // Show the selected (closed-state) label rendered in its own font when
  // it's a known recipe; for custom values, fall through to the input's
  // own preview, since we can't know what font the user typed.
  selectedId.value === 'custom' || selectedId.value === 'system-sans' || selectedId.value === 'system-mono'
    ? {}
    : { fontFamily: selectedOption.value.cssFamily },
);

function optionFontStyle(opt: Option) {
  if (opt.id === 'custom') return {};
  return { fontFamily: opt.cssFamily };
}

const customValue = ref(selectedId.value === 'custom' ? props.modelValue : '');

watch(
  () => props.modelValue,
  (next) => {
    if (selectedId.value === 'custom') customValue.value = next;
  },
);

function onSelect(id: string) {
  if (id === 'system-sans' || id === 'system-mono') {
    // Empty string = "use baked-in default" — that's how the store decides
    // not to override --cr-font-family / --cr-font-family-mono.
    emit('update:modelValue', '');
    return;
  }
  if (id === 'custom') {
    // User intends to type. Don't emit yet — wait for the custom field.
    // If they had a custom value before, restore it; otherwise empty.
    emit('update:modelValue', customValue.value);
    return;
  }
  const opt = options.value.find((o) => o.id === id);
  if (!opt) return;
  // Pre-warm the font load so the preview shows the new font without
  // waiting for the next apply() call.
  loadIfBundled(opt.cssFamily);
  emit('update:modelValue', opt.cssFamily);
}

function onCustomChange(value: string | number | null) {
  const v = String(value ?? '');
  customValue.value = v;
  emit('update:modelValue', v);
}
</script>

<style lang="scss" scoped>
.cr-font-picker {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: 520px;
}

.cr-font-picker-caption {
  color: var(--cr-fg-tertiary);
}
</style>
