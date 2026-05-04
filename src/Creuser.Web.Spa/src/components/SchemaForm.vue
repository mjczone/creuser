<template>
  <div class="cr-schema-form">
    <div v-for="(field, key) in fields" :key="key" class="cr-schema-row">
      <label class="cr-schema-label" :for="`cr-sf-${key}`">
        {{ field.label }}
        <span v-if="field.required" class="cr-schema-required">*</span>
      </label>
      <p v-if="field.description" class="cr-schema-help">{{ field.description }}</p>
      <q-select
        v-if="field.kind === 'enum'"
        :model-value="modelValue[key]"
        :options="field.options"
        :label="''"
        outlined
        dense
        emit-value
        map-options
        @update:model-value="(v) => set(key, v)"
      />
      <q-input
        v-else-if="field.kind === 'integer'"
        :model-value="(modelValue[key] as number | null) ?? null"
        type="number"
        :step="1"
        :min="field.min"
        :max="field.max"
        outlined
        dense
        @update:model-value="(v) => set(key, parseIntOrNull(v))"
      />
      <q-input
        v-else-if="field.kind === 'string-multiline'"
        :model-value="(modelValue[key] as string | null) ?? ''"
        type="textarea"
        autogrow
        outlined
        dense
        @update:model-value="(v) => set(key, v)"
      />
      <q-input
        v-else
        :model-value="(modelValue[key] as string | null) ?? ''"
        outlined
        dense
        @update:model-value="(v) => set(key, v)"
      />
    </div>
    <div v-if="fields.length === 0" class="cr-schema-empty">
      This widget has no per-instance configuration.
    </div>
  </div>
</template>

<script setup lang="ts">
/**
 * Minimal JSON Schema → form auto-renderer for the dashboard composer's
 * "Add Widget" + "Configure Widget" flows. Supports the v1 widget set's
 * simple shapes:
 *   - object root with named properties
 *   - string + integer + boolean primitives
 *   - enum (rendered as q-select)
 *   - description (rendered as inline help)
 *   - default (used when the form opens without a value)
 *
 * Unknown property types fall back to a plain text input — graceful
 * degradation rather than refusing to render. v0.2 may swap this for a
 * proper JSON Schema library when widgets accumulate complex shapes;
 * v1 widgets all fit in <30 fields total.
 */
import { computed } from 'vue';

interface SchemaProperty {
  type?: string | string[];
  description?: string;
  enum?: unknown[];
  default?: unknown;
  minimum?: number;
  maximum?: number;
  format?: string;
  multiline?: boolean;
}

interface Schema {
  type?: string;
  required?: string[];
  properties?: Record<string, SchemaProperty>;
}

interface Field {
  key: string;
  label: string;
  description?: string;
  required: boolean;
  kind: 'string' | 'string-multiline' | 'integer' | 'enum';
  options?: { label: string; value: unknown }[];
  min?: number;
  max?: number;
}

const props = defineProps<{
  schema: Schema;
  modelValue: Record<string, unknown>;
}>();
const emit = defineEmits<{
  'update:modelValue': [Record<string, unknown>];
}>();

const fields = computed<Field[]>(() => {
  const out: Field[] = [];
  const required = new Set(props.schema.required ?? []);
  const propsObj = props.schema.properties ?? {};
  for (const [key, def] of Object.entries(propsObj)) {
    out.push(toField(key, def, required.has(key)));
  }
  return out;
});

function toField(key: string, def: SchemaProperty, isRequired: boolean): Field {
  const label = humanize(key);
  if (Array.isArray(def.enum)) {
    return {
      key,
      label,
      description: def.description,
      required: isRequired,
      kind: 'enum',
      options: def.enum.map((v) => ({ label: String(v), value: v })),
    };
  }
  const t = Array.isArray(def.type) ? def.type[0] : def.type;
  if (t === 'integer' || t === 'number') {
    return {
      key,
      label,
      description: def.description,
      required: isRequired,
      kind: 'integer',
      min: def.minimum,
      max: def.maximum,
    };
  }
  if (def.multiline) {
    return {
      key,
      label,
      description: def.description,
      required: isRequired,
      kind: 'string-multiline',
    };
  }
  return {
    key,
    label,
    description: def.description,
    required: isRequired,
    kind: 'string',
  };
}

function humanize(key: string): string {
  return key
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .replace(/^\w/, (c) => c.toUpperCase());
}

function set(key: string | number, value: unknown) {
  const next = { ...props.modelValue, [String(key)]: value };
  emit('update:modelValue', next);
}

function parseIntOrNull(v: string | number | null): number | null {
  if (v === null || v === '') return null;
  const n = typeof v === 'number' ? v : parseInt(v, 10);
  return Number.isFinite(n) ? n : null;
}
</script>

<style lang="scss" scoped>
.cr-schema-form {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.cr-schema-row {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.cr-schema-label {
  font-size: 12px;
  font-weight: 500;
  color: var(--cr-fg-secondary, #ccc);
}

.cr-schema-required {
  color: rgb(248, 113, 113);
  margin-left: 2px;
}

.cr-schema-help {
  margin: 0 0 4px;
  font-size: 11px;
  color: var(--cr-fg-tertiary, #888);
  line-height: 1.4;
}

.cr-schema-empty {
  font-size: 12px;
  color: var(--cr-fg-tertiary, #888);
  font-style: italic;
  text-align: center;
  padding: 16px;
}
</style>
