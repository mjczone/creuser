<template>
  <div :class="['cr-status-banner', `cr-status-banner--${variant}`]" role="status">
    <q-icon :name="icon ?? defaultIcon" size="16px" class="cr-status-banner-icon" />
    <div class="cr-status-banner-body">
      <strong v-if="title">{{ title }}</strong>
      <slot />
    </div>
    <q-btn
      v-if="dismissable"
      flat
      dense
      round
      icon="close"
      size="xs"
      aria-label="Dismiss"
      class="cr-status-banner-dismiss"
      @click="emit('dismiss')"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue';

/**
 * Inline status banner for confirmations, errors, warnings, info notes that
 * shouldn't escape to a global toast (e.g. action results that belong next
 * to the field they relate to). Use `q-notify` for one-shot toasts; use
 * this when the message has a context that's already visible on screen.
 *
 * Pure visual chrome — no business semantics. Domain-specific wrappers
 * (HealthBanner, etc.) should compose this rather than re-invent the
 * styling.
 */
export type StatusVariant = 'success' | 'error' | 'warning' | 'info';

interface Props {
  variant: StatusVariant;
  /** Optional bold prefix shown before the slot content. */
  title?: string;
  /** Override the variant's default icon if a more specific one fits. */
  icon?: string;
  /** When true, renders an X button on the right and emits `dismiss`. */
  dismissable?: boolean;
  /**
   * Auto-dismiss after N milliseconds. Emits `dismiss` once when the timer
   * fires; the parent decides what dismissal means (clear state, fade out,
   * etc.). Pass 0 / undefined to disable. Common values: 3000 for routine
   * confirmations, 5000–8000 for errors that need a beat to read.
   */
  timeoutMs?: number;
}

const props = withDefaults(defineProps<Props>(), { dismissable: false });
const emit = defineEmits<{ dismiss: [] }>();

let autoDismissTimer: ReturnType<typeof setTimeout> | null = null;

onMounted(() => {
  if (props.timeoutMs && props.timeoutMs > 0) {
    autoDismissTimer = setTimeout(() => emit('dismiss'), props.timeoutMs);
  }
});

onBeforeUnmount(() => {
  if (autoDismissTimer !== null) clearTimeout(autoDismissTimer);
});

const VARIANT_ICONS: Record<StatusVariant, string> = {
  success: 'check_circle',
  error: 'error',
  warning: 'warning',
  info: 'info',
};

const defaultIcon = computed(() => VARIANT_ICONS[props.variant]);
</script>

<style lang="scss" scoped>
// Variant colors are derived from Quasar's brand palette via `color-mix`, so
// admins overriding `--q-positive` / `--q-negative` / etc. (via the Branding
// page or Custom CSS) get the banner tint to follow without us needing
// per-variant rgba literals.
.cr-status-banner {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px 12px;
  border-radius: 4px;
  font-size: 12px;
  line-height: 1.5;
}

.cr-status-banner--success {
  background: color-mix(in srgb, var(--q-positive) 10%, transparent);
  color: var(--q-positive);
  border: 1px solid color-mix(in srgb, var(--q-positive) 25%, transparent);
}

.cr-status-banner--error {
  background: color-mix(in srgb, var(--q-negative) 10%, transparent);
  color: var(--q-negative);
  border: 1px solid color-mix(in srgb, var(--q-negative) 25%, transparent);
}

.cr-status-banner--warning {
  background: color-mix(in srgb, var(--q-warning) 12%, transparent);
  color: var(--q-warning);
  border: 1px solid color-mix(in srgb, var(--q-warning) 28%, transparent);
}

.cr-status-banner--info {
  background: color-mix(in srgb, var(--q-info) 10%, transparent);
  color: var(--q-info);
  border: 1px solid color-mix(in srgb, var(--q-info) 25%, transparent);
}

.cr-status-banner-icon {
  flex-shrink: 0;
  margin-top: 1px;
}

.cr-status-banner-body {
  flex: 1;
  min-width: 0;
  word-break: break-word;
}

.cr-status-banner-dismiss {
  flex-shrink: 0;
  margin: -4px -4px -4px 0;
  // Inherit the banner's variant tint via currentColor — one rule, four
  // variants automatically.
  color: currentColor;
  opacity: 0.7;

  &:hover {
    opacity: 1;
  }
}
</style>
