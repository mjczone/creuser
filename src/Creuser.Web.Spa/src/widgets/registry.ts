import type { Component } from 'vue';

/**
 * Hint dockview should follow when adding a freshly-created widget instance.
 * The composer designer respects these defaults; users can override.
 */
export interface WidgetDockviewHint {
  /** Pixel hint for the new pane's minimum width. */
  minWidth?: number;
  /** Pixel hint for the new pane's minimum height. */
  minHeight?: number;
  /**
   * Where the new pane lands relative to the active pane:
   *  - `right`: split horizontally
   *  - `below`: split vertically
   *  - `tab`: open as a new tab in the active group
   */
  preferredPosition?: 'right' | 'below' | 'tab';
}

/**
 * One widget contribution to the dashboard composer. Widgets register
 * themselves at module-load time via {@link registerWidget}; the dashboard
 * page reads from this registry when composing dockview panes.
 *
 * The registry also drives the "Add widget" picker — its metadata fields
 * (icon, name, description) populate the picker tile, and `propsSchema`
 * drives the auto-form for instance configuration.
 *
 * Future plugin-contributed widgets register against the same surface, so
 * built-in and plugin-contributed widgets coexist transparently.
 */
export interface WidgetDefinition<TProps = Record<string, unknown>> {
  /** Stable identifier persisted in cr.dashboards.widgets[].widgetType. */
  type: string;
  /** Human-readable name for the picker. */
  name: string;
  /** One-sentence description for the picker tile. */
  description: string;
  /** Material icon name (e.g. `'play_circle'`). */
  icon: string;
  /** The Vue component mounted inside a dockview pane. Receives `props`. */
  component: Component;
  /** JSON Schema for `props` — drives the auto-form in the designer. */
  propsSchema: object;
  /** Defaults pre-populated when this widget is first added to a dashboard. */
  defaultProps: TProps;
  /** Suggested initial dockview placement. */
  defaultDockview: WidgetDockviewHint;
}

const registry = new Map<string, WidgetDefinition>();

export function registerWidget<T = Record<string, unknown>>(def: WidgetDefinition<T>): void {
  if (registry.has(def.type)) {
    throw new Error(
      `Widget already registered: ${def.type}. Each widgetType must be unique across the registry.`,
    );
  }
  registry.set(def.type, def as WidgetDefinition);
}

export function getWidget(type: string): WidgetDefinition | undefined {
  return registry.get(type);
}

export function listWidgets(): WidgetDefinition[] {
  return Array.from(registry.values()).sort((a, b) => a.name.localeCompare(b.name));
}

/** Test-only — clears the registry between tests. Don't call from app code. */
export function _clearRegistryForTests(): void {
  registry.clear();
}
