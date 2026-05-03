<template>
  <div class="cr-palette-picker">
    <div v-for="group in groups" :key="group.label" class="cr-palette-group">
      <h3 class="cr-palette-group-title">{{ group.label }}</h3>
      <div class="cr-palette-grid">
        <button
          v-for="preset in group.presets"
          :key="preset.id"
          type="button"
          class="cr-palette-card"
          :class="{ 'cr-palette-card--active': preset.id === activeId }"
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

        <div
          v-if="!activeId && group.label === 'Custom'"
          class="cr-palette-card cr-palette-card--custom"
          aria-hidden="true"
        >
          <div class="cr-palette-swatches">
            <span class="cr-palette-swatch cr-palette-swatch--custom">…</span>
          </div>
          <span class="cr-palette-label">Custom</span>
        </div>
      </div>
    </div>

    <p v-if="!activeId" class="cr-palette-custom-note">
      <q-icon name="auto_fix_high" size="14px" />
      Custom — colors don't match a preset.
    </p>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { PALETTE_PRESETS, type PalettePreset } from 'src/css/palettes/registry';

interface Props {
  activeId: string | null;
}

defineProps<Props>();
const emit = defineEmits<{
  pick: [preset: PalettePreset];
}>();

interface Group {
  label: string;
  presets: PalettePreset[];
}

const groups = computed<Group[]>(() => [
  { label: 'Universal', presets: PALETTE_PRESETS.filter((p) => p.mode === 'both') },
  { label: 'Dark mode', presets: PALETTE_PRESETS.filter((p) => p.mode === 'dark') },
  { label: 'Light mode', presets: PALETTE_PRESETS.filter((p) => p.mode === 'light') },
]);

function onPick(id: string) {
  const preset = PALETTE_PRESETS.find((p) => p.id === id);
  if (preset) emit('pick', preset);
}
</script>

<style lang="scss" scoped>
.cr-palette-picker {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.cr-palette-group-title {
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  margin: 0 0 8px;
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
