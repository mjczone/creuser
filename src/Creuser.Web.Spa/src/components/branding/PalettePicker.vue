<template>
  <div class="cr-palette-picker">
    <div class="cr-palette-grid">
      <button
        v-for="preset in presets"
        :key="preset.id"
        type="button"
        class="cr-palette-card"
        :class="{ 'cr-palette-card--active': preset.id === activeId }"
        :data-preset-id="preset.id"
        :title="preset.description"
        @click="onPick(preset.id)"
      >
        <div class="cr-palette-swatches" :aria-hidden="true">
          <span
            v-for="(color, i) in preset.swatches"
            :key="i"
            class="cr-palette-swatch"
            :style="{ background: color }"
          />
        </div>
        <span class="cr-palette-label">{{ preset.label }}</span>
      </button>
    </div>

    <p v-if="!activeId" class="cr-palette-custom-note">
      <q-icon name="auto_fix_high" size="14px" />
      Custom — colors don't match a preset.
    </p>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import {
  PALETTE_PRESETS,
  type PalettePreset,
  type PresetMode,
} from 'src/css/palettes/registry';

interface Props {
  /** Filter the picker to presets for this mode only (dark or light). */
  mode: PresetMode;
  /** ID of the preset currently considered active for this slot, or null. */
  activeId: string | null;
}

const props = defineProps<Props>();
const emit = defineEmits<{
  pick: [preset: PalettePreset];
}>();

const presets = computed<PalettePreset[]>(() =>
  PALETTE_PRESETS.filter((p) => p.mode === props.mode),
);

function onPick(id: string) {
  const preset = PALETTE_PRESETS.find((p) => p.id === id);
  if (preset) emit('pick', preset);
}
</script>

<style lang="scss" scoped>
.cr-palette-picker {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.cr-palette-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
  gap: 8px;
}

.cr-palette-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  background: var(--cr-bg-elevated);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 6px;
  cursor: pointer;
  text-align: left;
  outline: none;
  transition:
    border-color 80ms ease-out,
    background 80ms ease-out;

  &:hover,
  &:focus-visible {
    border-color: var(--cr-border-strong);
  }

  &--active {
    border-color: var(--q-primary);
    box-shadow: 0 0 0 1px var(--q-primary) inset;
  }

  &--custom {
    cursor: default;
    border-style: dashed;
  }
}

.cr-palette-swatches {
  display: flex;
  height: 24px;
  border-radius: 4px;
  overflow: hidden;
  border: 1px solid var(--cr-border-subtle);
}

.cr-palette-swatch {
  flex: 1;
  min-width: 0;

  &--custom {
    flex: 1;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--cr-bg-surface);
    color: var(--cr-fg-tertiary);
    font-size: 14px;
  }
}

.cr-palette-label {
  font-size: 12px;
  font-weight: 500;
  color: var(--cr-fg-primary);
}

.cr-palette-custom-note {
  margin: 0;
  font-size: 12px;
  color: var(--cr-fg-tertiary);
  display: inline-flex;
  align-items: center;
  gap: 4px;
}
</style>
