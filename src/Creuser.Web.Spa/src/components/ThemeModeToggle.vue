<template>
  <q-btn
    flat
    dense
    round
    :icon="iconForCurrent"
    :aria-label="`Theme mode: ${themeMode.preference}`"
    class="cr-theme-toggle"
  >
    <q-tooltip anchor="bottom right" self="top right">
      Theme: {{ labelFor(themeMode.preference) }}
    </q-tooltip>
    <q-menu auto-close anchor="bottom right" self="top right" class="cr-theme-menu">
      <q-list dense>
        <q-item
          v-for="opt in options"
          :key="opt.value"
          clickable
          dense
          :active="themeMode.preference === opt.value"
          active-class="cr-theme-menu-active"
          @click="themeMode.setPreference(opt.value)"
        >
          <q-item-section avatar>
            <q-icon :name="opt.icon" size="18px" />
          </q-item-section>
          <q-item-section>
            <q-item-label>{{ opt.label }}</q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
    </q-menu>
  </q-btn>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useThemeModeStore, type ThemeMode } from 'stores/themeMode';

interface Option {
  value: ThemeMode;
  label: string;
  icon: string;
}

const themeMode = useThemeModeStore();

const options: Option[] = [
  { value: 'dark', label: 'Dark', icon: 'dark_mode' },
  { value: 'light', label: 'Light', icon: 'light_mode' },
  { value: 'auto', label: 'Auto (system)', icon: 'brightness_auto' },
];

const iconForCurrent = computed(() => {
  if (themeMode.preference === 'auto') return 'brightness_auto';
  return themeMode.effective === 'dark' ? 'dark_mode' : 'light_mode';
});

function labelFor(value: ThemeMode): string {
  return options.find((o) => o.value === value)?.label ?? value;
}
</script>

<style lang="scss" scoped>
.cr-theme-toggle {
  color: var(--cr-fg-secondary);

  &:hover {
    color: var(--cr-fg-primary);
  }
}

.cr-theme-menu {
  background: var(--cr-bg-elevated);
  border: 1px solid var(--cr-border-subtle);
  min-width: 180px;
}

.cr-theme-menu-active {
  color: var(--q-primary) !important;
  background: var(--cr-brand-tint-soft) !important;

  .q-icon {
    color: var(--q-primary);
  }
}
</style>
