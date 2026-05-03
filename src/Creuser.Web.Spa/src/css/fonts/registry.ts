/**
 * Curated font registry. Each `BundledFont` entry's `load()` is a dynamic
 * import of the fontsource package's index.css — Vite code-splits these into
 * separate chunks, so a font's woff2 + @font-face CSS only ships to a user's
 * browser when an admin actually picks it from the FontPicker.
 *
 * To add a font:
 *   1. `npm install @fontsource-variable/<id>`
 *   2. Add an entry below.
 *   3. The picker, lookup, and lazy-load all pick it up automatically.
 */

export type FontType = 'sans' | 'mono';

export interface FontEntry {
  /** Stable id; used as the q-select option value. */
  id: string;
  /** Human label shown in the picker. */
  label: string;
  /** CSS font-family list — exactly what we store in BrandingConfig.fontFamily. */
  cssFamily: string;
  type: FontType;
  /** Bundled fonts have a loader; system defaults don't. */
  load?: () => Promise<unknown>;
}

const SYSTEM_SANS_STACK =
  '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Oxygen, Ubuntu, Cantarell, "Helvetica Neue", Arial, sans-serif';

const SYSTEM_MONO_STACK =
  'ui-monospace, SFMono-Regular, "SF Mono", Consolas, "Liberation Mono", Menlo, monospace';

export const SYSTEM_SANS: FontEntry = {
  id: 'system-sans',
  label: 'System default',
  cssFamily: SYSTEM_SANS_STACK,
  type: 'sans',
};

export const SYSTEM_MONO: FontEntry = {
  id: 'system-mono',
  label: 'System default',
  cssFamily: SYSTEM_MONO_STACK,
  type: 'mono',
};

export const BUNDLED_FONTS: FontEntry[] = [
  // Sans-serif
  {
    id: 'inter',
    label: 'Inter',
    cssFamily: `"Inter Variable", ${SYSTEM_SANS_STACK}`,
    type: 'sans',
    load: () => import('@fontsource-variable/inter'),
  },
  {
    id: 'ibm-plex-sans',
    label: 'IBM Plex Sans',
    cssFamily: `"IBM Plex Sans Variable", ${SYSTEM_SANS_STACK}`,
    type: 'sans',
    load: () => import('@fontsource-variable/ibm-plex-sans'),
  },
  {
    id: 'source-sans-3',
    label: 'Source Sans 3',
    cssFamily: `"Source Sans 3 Variable", ${SYSTEM_SANS_STACK}`,
    type: 'sans',
    load: () => import('@fontsource-variable/source-sans-3'),
  },

  // Monospace
  {
    id: 'jetbrains-mono',
    label: 'JetBrains Mono',
    cssFamily: `"JetBrains Mono Variable", ${SYSTEM_MONO_STACK}`,
    type: 'mono',
    load: () => import('@fontsource-variable/jetbrains-mono'),
  },
  {
    id: 'fira-code',
    label: 'Fira Code',
    cssFamily: `"Fira Code Variable", ${SYSTEM_MONO_STACK}`,
    type: 'mono',
    load: () => import('@fontsource-variable/fira-code'),
  },
  {
    id: 'source-code-pro',
    label: 'Source Code Pro',
    cssFamily: `"Source Code Pro Variable", ${SYSTEM_MONO_STACK}`,
    type: 'mono',
    load: () => import('@fontsource-variable/source-code-pro'),
  },
];

/** Type-filtered list of {system default, ...bundled} for a picker. */
export function fontsForType(type: FontType): FontEntry[] {
  const system = type === 'sans' ? SYSTEM_SANS : SYSTEM_MONO;
  return [system, ...BUNDLED_FONTS.filter((f) => f.type === type)];
}

/**
 * Match a stored CSS-family string back to a known registry entry.
 * Returns null if the value is empty (use baked-in default) or doesn't
 * match any known recipe (treat as custom).
 */
export function lookupFont(cssFamily: string | null | undefined, type: FontType): FontEntry | null {
  if (!cssFamily) return null;
  return fontsForType(type).find((f) => f.cssFamily === cssFamily) ?? null;
}

/**
 * Cache of loaders that have already fired. Bundled fonts only need to
 * load once per session; subsequent picks are no-ops.
 */
const loaded = new Set<string>();

/** Fire-and-forget: kick off the dynamic import for a bundled font. */
export function loadIfBundled(cssFamily: string | null | undefined): void {
  if (!cssFamily) return;
  const entry = BUNDLED_FONTS.find((f) => f.cssFamily === cssFamily);
  if (!entry?.load || loaded.has(entry.id)) return;
  loaded.add(entry.id);
  entry.load().catch(() => {
    // CSS import failures shouldn't break the app; the fallback stack
    // in cssFamily means the page still renders with system fonts.
    loaded.delete(entry.id);
  });
}
