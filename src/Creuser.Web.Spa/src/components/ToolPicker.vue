<template>
  <div class="cr-toolpicker">
    <div class="cr-toolpicker-head">
      <span class="cr-toolpicker-label">Allowed commands</span>
      <span class="cr-toolpicker-count">
        <strong>{{ modelValue.length }}</strong> selected
      </span>
    </div>
    <p v-if="modelValue.length === 0" class="cr-toolpicker-warning">
      <q-icon name="warning" size="14px" />
      No commands selected — shell jobs will reject every script. Pick from below or add custom
      binaries.
    </p>

    <div class="cr-toolpicker-selected">
      <q-chip
        v-for="cmd in modelValue"
        :key="cmd"
        dense
        removable
        color="primary"
        text-color="white"
        class="cr-toolpicker-chip"
        @remove="remove(cmd)"
      >
        {{ cmd }}
      </q-chip>
      <span v-if="modelValue.length === 0" class="cr-toolpicker-empty"> (none) </span>
    </div>

    <q-input
      v-model="customInput"
      label="Add a custom binary (e.g. a tool you've added via a derivative image)"
      dense
      outlined
      class="cr-toolpicker-custom"
      @keyup.enter="addCustom"
    >
      <template #append>
        <q-btn
          flat
          dense
          no-caps
          label="Add"
          color="primary"
          :disable="!customInput.trim()"
          @click="addCustom"
        />
      </template>
    </q-input>

    <q-expansion-item
      v-for="group in groupedCatalog"
      :key="group.category"
      :label="group.label"
      :caption="`${group.entries.length} tool${group.entries.length === 1 ? '' : 's'} · click a chip to toggle`"
      class="cr-toolpicker-group"
      header-class="cr-toolpicker-group-head"
    >
      <div class="cr-toolpicker-grid">
        <q-chip
          v-for="entry in group.entries"
          :key="entry.name"
          clickable
          dense
          :outline="!isSelected(entry.name)"
          :color="isSelected(entry.name) ? 'primary' : 'grey-7'"
          :text-color="isSelected(entry.name) ? 'white' : undefined"
          class="cr-toolpicker-chip"
          @click="toggle(entry.name)"
        >
          <span class="cr-toolpicker-name">{{ entry.name }}</span>
          <q-tooltip v-if="entry.description">
            {{ entry.description }}
          </q-tooltip>
        </q-chip>
      </div>
    </q-expansion-item>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { Tools, type ToolEntry } from 'src/api';

const props = defineProps<{
  modelValue: string[];
}>();
const emit = defineEmits<{ 'update:modelValue': [value: string[]] }>();

const catalog = ref<ToolEntry[]>([]);
const customInput = ref('');

const CATEGORY_LABELS: Record<string, string> = {
  system: 'System / POSIX',
  core: 'Core text & search',
  'code-aware': 'Code-aware',
  'schema-data': 'Schema & data',
  'diff-merge': 'Diff & merge',
  runtime: 'Language runtimes',
  specialized: 'Specialized',
};

const groupedCatalog = computed(() => {
  const groups = new Map<string, ToolEntry[]>();
  for (const entry of catalog.value) {
    const list = groups.get(entry.category) ?? [];
    list.push(entry);
    groups.set(entry.category, list);
  }
  // Stable order: follow CATEGORY_LABELS keys, then any remaining categories
  // alphabetically (defensive — plugin-contributed tools may add new ones).
  const ordered = [
    ...Object.keys(CATEGORY_LABELS),
    ...[...groups.keys()].filter((k) => !(k in CATEGORY_LABELS)).sort(),
  ];
  return ordered
    .filter((c) => groups.has(c))
    .map((category) => ({
      category,
      label: CATEGORY_LABELS[category] ?? category,
      entries: (groups.get(category) ?? []).sort((a, b) => a.name.localeCompare(b.name)),
    }));
});

function isSelected(name: string): boolean {
  return props.modelValue.includes(name);
}

function toggle(name: string) {
  const next = isSelected(name)
    ? props.modelValue.filter((c) => c !== name)
    : [...props.modelValue, name].sort();
  emit('update:modelValue', next);
}

function remove(name: string) {
  emit(
    'update:modelValue',
    props.modelValue.filter((c) => c !== name),
  );
}

function addCustom() {
  const trimmed = customInput.value.trim();
  if (!trimmed) return;
  if (!/^[a-zA-Z0-9._-]+$/.test(trimmed)) {
    customInput.value = '';
    return;
  }
  if (!isSelected(trimmed)) {
    emit('update:modelValue', [...props.modelValue, trimmed].sort());
  }
  customInput.value = '';
}

async function load() {
  try {
    const res = await Tools.listTools();
    catalog.value = res.data?.result ?? [];
  } catch {
    catalog.value = [];
  }
}

onMounted(() => void load());
</script>

<style lang="scss" scoped>
.cr-toolpicker {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.cr-toolpicker-head {
  display: flex;
  align-items: baseline;
  gap: 12px;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
}

.cr-toolpicker-count {
  font-size: 11px;
  color: var(--cr-fg-secondary);
  text-transform: none;
  letter-spacing: 0;
  font-weight: normal;

  strong {
    color: var(--cr-fg-primary);
  }
}

.cr-toolpicker-warning {
  margin: 0;
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--q-warning);
}

.cr-toolpicker-selected {
  min-height: 32px;
  padding: 6px 8px;
  background: var(--cr-bg-elevated);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 4px;
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  align-items: center;
}

.cr-toolpicker-empty {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  font-style: italic;
}

.cr-toolpicker-custom {
  margin-top: 4px;
}

.cr-toolpicker-group {
  border: 1px solid var(--cr-border-subtle);
  border-radius: 4px;
}

.cr-toolpicker-group-head {
  font-size: 12px;
  font-weight: 600;
}

.cr-toolpicker-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 8px 16px 12px;
}

.cr-toolpicker-chip {
  font-family: var(--cr-font-family-mono);
  font-size: 11px;
}

.cr-toolpicker-name {
  font-weight: 500;
}
</style>
