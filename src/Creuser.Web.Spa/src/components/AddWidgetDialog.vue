<template>
  <q-dialog ref="dialogRef" persistent>
    <q-card class="cr-add-widget">
      <q-card-section class="cr-add-widget-header">
        <h2 class="cr-add-widget-title">
          {{ step === 'pick' ? 'Add a widget' : `Configure ${selected?.name}` }}
        </h2>
        <q-space />
        <q-btn flat dense round icon="close" @click="cancel" />
      </q-card-section>

      <!-- Step 1: pick a widget type -->
      <q-card-section v-if="step === 'pick'" class="cr-add-widget-grid">
        <button
          v-for="def in widgets"
          :key="def.type"
          class="cr-add-widget-card"
          @click="pick(def)"
        >
          <q-icon :name="def.icon" size="28px" class="cr-add-widget-card-icon" />
          <div class="cr-add-widget-card-info">
            <h3 class="cr-add-widget-card-name">{{ def.name }}</h3>
            <p class="cr-add-widget-card-desc">{{ def.description }}</p>
            <span class="cr-add-widget-card-type">{{ def.type }}</span>
          </div>
        </button>
        <p v-if="widgets.length === 0" class="cr-add-widget-empty">No widgets registered.</p>
      </q-card-section>

      <!-- Step 2: configure props -->
      <q-card-section v-else-if="step === 'config'" class="cr-add-widget-config">
        <SchemaForm v-model="propsValue" :schema="(selected?.propsSchema ?? {}) as never" />
      </q-card-section>

      <q-card-actions align="right" class="cr-add-widget-actions">
        <q-btn v-if="step === 'pick'" flat label="Cancel" @click="cancel" />
        <template v-else>
          <q-btn flat label="Back" @click="step = 'pick'" />
          <q-btn flat label="Cancel" @click="cancel" />
          <q-btn unelevated color="primary" label="Add" @click="confirm" />
        </template>
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<script setup lang="ts">
/**
 * Two-step modal for adding a widget instance to the active dashboard.
 *
 * Step 1: pick from `listWidgets()` — a card grid keyed by widget type.
 * Step 2: edit props via `SchemaForm` (auto-rendered from the widget's
 *   propsSchema). Defaults from the registry's `defaultProps` pre-fill.
 *
 * Emits `add` with `{ widgetType, props, defaultDockview }` when the user
 * clicks "Add". The parent (DashboardPage) is responsible for inserting
 * the new instance into the dashboard's widgets array and calling
 * dockview's addPanel.
 */
import { ref } from 'vue';
import { useDialogPluginComponent } from 'quasar';
import SchemaForm from 'src/components/SchemaForm.vue';
import { listWidgets, type WidgetDefinition } from 'src/widgets/registry';

defineEmits([...useDialogPluginComponent.emits]);
const { dialogRef, onDialogOK, onDialogCancel } = useDialogPluginComponent();

type Step = 'pick' | 'config';
const step = ref<Step>('pick');
const widgets = ref<WidgetDefinition[]>(listWidgets());
const selected = ref<WidgetDefinition | null>(null);
const propsValue = ref<Record<string, unknown>>({});

function pick(def: WidgetDefinition) {
  selected.value = def;
  // Clone defaults so the form's mutations don't bleed into the
  // registered defaultProps record.
  propsValue.value = { ...def.defaultProps };
  step.value = 'config';
}

function cancel() {
  onDialogCancel();
}

function confirm() {
  if (!selected.value) return;
  onDialogOK({
    widgetType: selected.value.type,
    props: propsValue.value,
    defaultDockview: selected.value.defaultDockview,
  });
}
</script>

<style lang="scss" scoped>
.cr-add-widget {
  width: min(640px, 90vw);
  max-height: 80vh;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-elevated, #1a1a1d);
}

.cr-add-widget-header {
  display: flex;
  align-items: center;
  padding: 12px 16px;
  border-bottom: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
}

.cr-add-widget-title {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-add-widget-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 8px;
  padding: 16px;
  overflow-y: auto;
}

.cr-add-widget-card {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px;
  background: var(--cr-bg-subtle, #1f1f22);
  border: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
  border-radius: 6px;
  cursor: pointer;
  text-align: left;
  color: inherit;
  font: inherit;
  transition:
    border-color 0.12s,
    background 0.12s;

  &:hover {
    border-color: var(--cr-accent, rgb(96, 165, 250));
    background: var(--cr-bg-elevated, #262629);
  }
}

.cr-add-widget-card-icon {
  color: var(--cr-fg-secondary, #ccc);
  flex-shrink: 0;
}

.cr-add-widget-card-info {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.cr-add-widget-card-name {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--cr-fg-primary, #f0f0f0);
}

.cr-add-widget-card-desc {
  margin: 0;
  font-size: 11px;
  color: var(--cr-fg-tertiary, #888);
  line-height: 1.4;
}

.cr-add-widget-card-type {
  font-family: var(--cr-font-mono, ui-monospace, monospace);
  font-size: 10px;
  color: var(--cr-fg-tertiary, #888);
}

.cr-add-widget-config {
  padding: 16px;
  overflow-y: auto;
}

.cr-add-widget-actions {
  border-top: 1px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.08));
  padding: 8px 12px;
}

.cr-add-widget-empty {
  grid-column: 1 / -1;
  text-align: center;
  color: var(--cr-fg-tertiary, #888);
  font-style: italic;
  padding: 24px;
}
</style>
