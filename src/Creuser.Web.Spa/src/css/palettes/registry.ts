import {
  themeAbyss,
  themeCatppuccinMocha,
  themeDark,
  themeDracula,
  themeGithubDark,
  themeGithubLight,
  themeLight,
  themeMonokai,
  themeNord,
  themeSolarizedLight,
  themeVisualStudio,
} from 'dockview-core';
import type { DockviewTheme } from 'dockview-core';
import type { BrandingConfig } from 'src/api';

/**
 * Curated palette presets — each pairs 1:1 with a dockview-bundled theme so
 * the dock area (`.dv-shell`), the surrounding shell (sidebar, header,
 * page), and the Quasar palette all read as one piece. Applying a preset
 * overwrites palette + chrome (both modes) on the BrandingConfig draft and
 * its `dockviewTheme` is passed to `DockviewVue` at render time. Identity
 * (productName, logo, favicon, fonts, customCss) is preserved.
 *
 * Two kinds of preset:
 *
 *   1. **Creuser Dark / Creuser Light** — house brand. Pairs with
 *      `dockview-theme-dark` / `dockview-theme-light` as the structural
 *      base, then overrides dockview's `--dv-*` via the `--cr-*` → `--dv-*`
 *      mapping in `theme.scss` (gated by `cr-dock-creuser` on the canvas)
 *      so the dock takes the same sage-on-teal-forest values as the
 *      surrounding chrome. Set `useCreuserDockMapping: true`.
 *
 *   2. **Named themes** (Standard Dark/Light, VS Dark, Abyss, Dracula,
 *      Nord, Catppuccin Mocha, Monokai, GitHub Dark/Light, Solarized
 *      Light) — pairs with the dockview theme of the same name. The dock
 *      shows dockview's bundled colors verbatim; chrome values are tuned
 *      to match those colors so sidebar/header/page-bg blend in.
 */

type Palette = NonNullable<BrandingConfig['palette']>;
type Chrome = NonNullable<BrandingConfig['chrome']>;

export type PresetMode = 'dark' | 'light';

export interface PalettePreset {
  id: string;
  label: string;
  description: string;
  mode: PresetMode;
  /** Small set of representative colors for the picker swatch row. */
  swatches: string[];
  palette: Palette;
  /** Chrome applied in dark mode. */
  chrome: Chrome;
  /** Chrome applied in light mode. Empty preserves the bundled neutral defaults. */
  chromeLight: Chrome;
  /** dockview-core theme object passed to DockviewVue's `theme` prop. */
  dockviewTheme: DockviewTheme;
  /**
   * If true, DashboardPage adds the `cr-dock-creuser` marker class to the
   * canvas, activating the `--cr-*` → `--dv-*` mapping in `theme.scss`.
   * Used by the Creuser house presets to make the dock chrome follow our
   * brand colors instead of dockview-theme-dark/light's bundled grays.
   */
  useCreuserDockMapping?: boolean;
}

// ─────────────────────────────────────────────────────────────────────────
// Dark presets
// ─────────────────────────────────────────────────────────────────────────

const CREUSER_DARK: PalettePreset = {
  id: 'creuser-dark',
  label: 'Creuser Dark',
  description: 'Sage green on dark teal-forest. The Creuser house dark theme.',
  mode: 'dark',
  swatches: ['#5c7e62', '#7c9a82', '#d7a06b', '#143734', '#0c2422'],
  palette: {},
  chrome: {},
  chromeLight: {},
  dockviewTheme: themeDark,
  useCreuserDockMapping: true,
};

const STANDARD_DARK: PalettePreset = {
  id: 'standard-dark',
  label: 'Standard Dark',
  description: "Dockview's stock dark theme — VS Code-flavored grays.",
  mode: 'dark',
  swatches: ['#007acc', '#1e1e1e', '#252526', '#2d2d30', '#444444'],
  palette: {
    primary: '#007acc',
    secondary: '#3794ff',
    accent: '#cca700',
    positive: '#4ec9b0',
    negative: '#f48771',
    info: '#75beff',
    warning: '#cca700',
  },
  chrome: {
    bgPage: '#1e1e1e',
    bgSurface: '#252526',
    bgHeader: '#252526',
    bgSidebar: '#252526',
    bgElevated: '#2d2d30',
    fgPrimary: 'rgba(255, 255, 255, 0.95)',
    fgSecondary: 'rgba(255, 255, 255, 0.7)',
    fgTertiary: 'rgba(255, 255, 255, 0.5)',
    borderSubtle: 'rgba(204, 204, 204, 0.1)',
    borderDefault: '#444444',
    borderStrong: '#5a5a5a',
  },
  chromeLight: {},
  dockviewTheme: themeDark,
};

const VISUAL_STUDIO_DARK: PalettePreset = {
  id: 'visual-studio-dark',
  label: 'Visual Studio Dark',
  description: "Dockview's VS theme — high-contrast tabs with a blue accent strip.",
  mode: 'dark',
  swatches: ['#007acc', '#1e1e1e', '#3f3f46', '#252526', '#cccccc'],
  palette: {
    primary: '#007acc',
    secondary: '#3794ff',
    accent: '#cca700',
    positive: '#4ec9b0',
    negative: '#f48771',
    info: '#75beff',
    warning: '#cca700',
  },
  chrome: {
    bgPage: '#1e1e1e',
    bgSurface: '#252526',
    bgHeader: '#3f3f46',
    bgSidebar: '#252526',
    bgElevated: '#3f3f46',
    fgPrimary: 'rgba(255, 255, 255, 0.95)',
    fgSecondary: 'rgba(204, 204, 204, 0.85)',
    fgTertiary: 'rgba(204, 204, 204, 0.55)',
    borderSubtle: 'rgba(204, 204, 204, 0.08)',
    borderDefault: '#3f3f46',
    borderStrong: '#5a5a5a',
  },
  chromeLight: {},
  dockviewTheme: themeVisualStudio,
};

const ABYSS: PalettePreset = {
  id: 'abyss',
  label: 'Abyss',
  description: 'Deep midnight blues with a violet accent. Dockview-flagship.',
  mode: 'dark',
  swatches: ['#5b1ecf', '#10192c', '#000c18', '#1c1c2a', '#2b2b4a'],
  palette: {
    primary: '#5b1ecf',
    secondary: '#9479d8',
    accent: '#bd93f9',
    positive: '#5ac8a8',
    negative: '#f06868',
    info: '#5b9bd1',
    warning: '#e2b341',
  },
  chrome: {
    bgPage: '#000c18',
    bgSurface: '#10192c',
    bgHeader: '#10192c',
    bgSidebar: '#10192c',
    bgElevated: '#1c1c2a',
    fgPrimary: '#ffffff',
    fgSecondary: 'rgb(148, 151, 169)',
    fgTertiary: 'rgba(148, 151, 169, 0.65)',
    borderSubtle: 'rgba(43, 43, 74, 0.5)',
    borderDefault: '#2b2b4a',
    borderStrong: '#3a3a66',
  },
  chromeLight: {},
  dockviewTheme: themeAbyss,
};

const DRACULA: PalettePreset = {
  id: 'dracula',
  label: 'Dracula',
  description: 'Vivid purple + pink on deep slate. The dockview-theme-dracula bundle.',
  mode: 'dark',
  swatches: ['#bd93f9', '#ff79c6', '#50fa7b', '#282a36', '#44475a'],
  palette: {
    primary: '#bd93f9',
    secondary: '#8be9fd',
    accent: '#ff79c6',
    positive: '#50fa7b',
    negative: '#ff5555',
    info: '#8be9fd',
    warning: '#f1fa8c',
  },
  chrome: {
    bgPage: '#282a36',
    bgSurface: '#21222c',
    bgHeader: '#21222c',
    bgSidebar: '#21222c',
    bgElevated: '#44475a',
    fgPrimary: 'rgba(248, 248, 242, 0.95)',
    fgSecondary: 'rgba(189, 147, 249, 0.85)',
    fgTertiary: 'rgba(98, 114, 164, 0.85)',
    borderSubtle: 'rgba(68, 71, 90, 0.5)',
    borderDefault: '#44475a',
    borderStrong: '#6272a4',
  },
  chromeLight: {},
  dockviewTheme: themeDracula,
};

const NORD: PalettePreset = {
  id: 'nord',
  label: 'Nord',
  description: 'Cool, frosty blues on muted polar night. Dockview-flagship.',
  mode: 'dark',
  swatches: ['#88c0d0', '#5e81ac', '#a3be8c', '#2e3440', '#3b4252'],
  palette: {
    primary: '#88c0d0',
    secondary: '#5e81ac',
    accent: '#b48ead',
    positive: '#a3be8c',
    negative: '#bf616a',
    info: '#81a1c1',
    warning: '#ebcb8b',
  },
  chrome: {
    bgPage: '#2e3440',
    bgSurface: '#3b4252',
    bgHeader: '#3b4252',
    bgSidebar: '#3b4252',
    bgElevated: '#434c5e',
    fgPrimary: '#eceff4',
    fgSecondary: '#d8dee9',
    fgTertiary: 'rgba(216, 222, 233, 0.6)',
    borderSubtle: 'rgba(67, 76, 94, 0.6)',
    borderDefault: '#434c5e',
    borderStrong: '#4c566a',
  },
  chromeLight: {},
  dockviewTheme: themeNord,
};

const CATPPUCCIN_MOCHA: PalettePreset = {
  id: 'catppuccin-mocha',
  label: 'Catppuccin Mocha',
  description: 'Pastel mauve + lavender on Mocha indigo. Dockview-flagship.',
  mode: 'dark',
  swatches: ['#cba6f7', '#b4befe', '#a6e3a1', '#1e1e2e', '#181825'],
  palette: {
    primary: '#cba6f7',
    secondary: '#b4befe',
    accent: '#f5c2e7',
    positive: '#a6e3a1',
    negative: '#f38ba8',
    info: '#89b4fa',
    warning: '#f9e2af',
  },
  chrome: {
    bgPage: '#1e1e2e',
    bgSurface: '#181825',
    bgHeader: '#181825',
    bgSidebar: '#181825',
    bgElevated: '#313244',
    fgPrimary: '#cdd6f4',
    fgSecondary: '#bac2de',
    fgTertiary: '#a6adc8',
    borderSubtle: 'rgba(49, 50, 68, 0.5)',
    borderDefault: '#313244',
    borderStrong: '#45475a',
  },
  chromeLight: {},
  dockviewTheme: themeCatppuccinMocha,
};

const MONOKAI: PalettePreset = {
  id: 'monokai',
  label: 'Monokai',
  description: 'The classic high-contrast scheme — pink + green on charcoal.',
  mode: 'dark',
  swatches: ['#f92672', '#a6e22e', '#66d9ef', '#272822', '#3e3d32'],
  palette: {
    primary: '#66d9ef',
    secondary: '#ae81ff',
    accent: '#f92672',
    positive: '#a6e22e',
    negative: '#f92672',
    info: '#66d9ef',
    warning: '#fd971f',
  },
  chrome: {
    bgPage: '#272822',
    bgSurface: '#1f1f1c',
    bgHeader: '#1f1f1c',
    bgSidebar: '#1f1f1c',
    bgElevated: '#3e3d32',
    fgPrimary: '#f8f8f2',
    fgSecondary: 'rgba(248, 248, 242, 0.75)',
    fgTertiary: '#75715e',
    borderSubtle: 'rgba(62, 61, 50, 0.5)',
    borderDefault: '#3e3d32',
    borderStrong: '#49483e',
  },
  chromeLight: {},
  dockviewTheme: themeMonokai,
};

const GITHUB_DARK: PalettePreset = {
  id: 'github-dark',
  label: 'GitHub Dark',
  description: 'Neutral grays with GitHub blue accents.',
  mode: 'dark',
  swatches: ['#58a6ff', '#0d1117', '#161b22', '#21262d', '#30363d'],
  palette: {
    primary: '#58a6ff',
    secondary: '#79c0ff',
    accent: '#bc8cff',
    positive: '#3fb950',
    negative: '#f85149',
    info: '#58a6ff',
    warning: '#d29922',
  },
  chrome: {
    bgPage: '#0d1117',
    bgSurface: '#161b22',
    bgHeader: '#161b22',
    bgSidebar: '#161b22',
    bgElevated: '#21262d',
    fgPrimary: '#e6edf3',
    fgSecondary: '#8b949e',
    fgTertiary: '#6e7681',
    borderSubtle: 'rgba(48, 54, 61, 0.5)',
    borderDefault: '#30363d',
    borderStrong: '#484f58',
  },
  chromeLight: {},
  dockviewTheme: themeGithubDark,
};

// ─────────────────────────────────────────────────────────────────────────
// Light presets
// ─────────────────────────────────────────────────────────────────────────

const CREUSER_LIGHT: PalettePreset = {
  id: 'creuser-light',
  label: 'Creuser Light',
  description: 'Sage green on warm parchment. The Creuser house light theme.',
  mode: 'light',
  swatches: ['#5c7e62', '#7c9a82', '#d7a06b', '#fafafa', '#f6f6f7'],
  palette: {},
  chrome: {},
  chromeLight: {},
  dockviewTheme: themeLight,
  useCreuserDockMapping: true,
};

const STANDARD_LIGHT: PalettePreset = {
  id: 'standard-light',
  label: 'Standard Light',
  description: "Dockview's stock light theme — neutral grays with a blue accent.",
  mode: 'light',
  swatches: ['#0078d4', '#ffffff', '#f3f3f3', '#ececec', '#cccccc'],
  palette: {
    primary: '#0078d4',
    secondary: '#005a9e',
    accent: '#bf5af2',
    positive: '#0e7c50',
    negative: '#a4262c',
    info: '#0078d4',
    warning: '#9d5d00',
  },
  chrome: {},
  chromeLight: {
    bgPage: '#ffffff',
    bgSurface: '#f3f3f3',
    bgHeader: '#f3f3f3',
    bgSidebar: '#f3f3f3',
    bgElevated: '#ececec',
    fgPrimary: 'rgba(51, 51, 51, 0.95)',
    fgSecondary: 'rgba(51, 51, 51, 0.7)',
    fgTertiary: 'rgba(51, 51, 51, 0.45)',
    borderSubtle: 'rgba(204, 204, 204, 0.5)',
    borderDefault: '#cccccc',
    borderStrong: '#9c9c9c',
  },
  dockviewTheme: themeLight,
};

const SOLARIZED_LIGHT: PalettePreset = {
  id: 'solarized-light',
  label: 'Solarized Light',
  description: "Schoonover's parchment classic — muted accents on warm cream.",
  mode: 'light',
  swatches: ['#268bd2', '#859900', '#cb4b16', '#fdf6e3', '#eee8d5'],
  palette: {
    primary: '#268bd2',
    secondary: '#2aa198',
    accent: '#d33682',
    positive: '#859900',
    negative: '#dc322f',
    info: '#268bd2',
    warning: '#b58900',
  },
  chrome: {},
  chromeLight: {
    bgPage: '#fdf6e3',
    bgSurface: '#eee8d5',
    bgHeader: '#eee8d5',
    bgSidebar: '#eee8d5',
    bgElevated: '#f5efdc',
    fgPrimary: '#586e75',
    fgSecondary: '#657b83',
    fgTertiary: '#93a1a1',
    borderSubtle: 'rgba(238, 232, 213, 0.8)',
    borderDefault: '#e0dac0',
    borderStrong: '#cdc69c',
  },
  dockviewTheme: themeSolarizedLight,
};

const GITHUB_LIGHT: PalettePreset = {
  id: 'github-light',
  label: 'GitHub Light',
  description: 'Clean white surfaces with GitHub blue accents.',
  mode: 'light',
  swatches: ['#0969da', '#bf3989', '#1f883d', '#ffffff', '#f6f8fa'],
  palette: {
    primary: '#0969da',
    secondary: '#6e7781',
    accent: '#bf3989',
    positive: '#1f883d',
    negative: '#cf222e',
    info: '#0969da',
    warning: '#bf8700',
  },
  chrome: {},
  chromeLight: {
    bgPage: '#ffffff',
    bgSurface: '#f6f8fa',
    bgHeader: '#f6f8fa',
    bgSidebar: '#f6f8fa',
    bgElevated: '#eaeef2',
    fgPrimary: '#1f2328',
    fgSecondary: '#656d76',
    fgTertiary: '#6e7781',
    borderSubtle: 'rgba(208, 215, 222, 0.5)',
    borderDefault: '#d0d7de',
    borderStrong: '#afb8c1',
  },
  dockviewTheme: themeGithubLight,
};

export const PALETTE_PRESETS: PalettePreset[] = [
  // Dark presets — Creuser first, then dockview-flagships
  CREUSER_DARK,
  STANDARD_DARK,
  VISUAL_STUDIO_DARK,
  ABYSS,
  DRACULA,
  NORD,
  CATPPUCCIN_MOCHA,
  MONOKAI,
  GITHUB_DARK,
  // Light presets — Creuser first, then dockview-flagships
  CREUSER_LIGHT,
  STANDARD_LIGHT,
  SOLARIZED_LIGHT,
  GITHUB_LIGHT,
];

/** Group presets by mode for the picker UI. */
export function groupPresetsByMode(): Record<PresetMode, PalettePreset[]> {
  return {
    dark: PALETTE_PRESETS.filter((p) => p.mode === 'dark'),
    light: PALETTE_PRESETS.filter((p) => p.mode === 'light'),
  };
}

/**
 * Detect which preset (if any) the current config matches by exact equality
 * of its palette + chrome fields. Used by the picker to highlight the
 * active preset and by DashboardPage to look up the preset's dockview
 * theme. Returns null when the user has manually tweaked colors away from
 * any preset (the picker shows "Custom" then; DashboardPage falls back to
 * Creuser-mapped chrome on `themeAbyss`).
 *
 * Normalizes both sides — strips null/undefined entries before comparing
 * — because the persisted config (`cr.app_settings` row) materializes
 * every key with a null default, while presets use sparse `{}` for "use
 * the bundled values." Without normalization, every config would always
 * read as "Custom" even when it exactly matches a preset.
 */
export function detectActivePreset(
  palette: Palette | null | undefined,
  chrome: Chrome | null | undefined,
  chromeLight: Chrome | null | undefined,
  mode?: PresetMode | null,
): PalettePreset | null {
  const a = canonical({ palette, chrome, chromeLight });
  // Two-pass match — prefer presets whose `mode` matches first, then fall
  // back to mode-agnostic match. The Creuser Dark / Creuser Light presets
  // are identical except for `mode` (both ship empty palette/chrome and
  // rely on the bundled defaults that flip via `.body--light`), so without
  // mode-based disambiguation Creuser Light would always match Creuser
  // Dark (the first entry).
  if (mode) {
    for (const preset of PALETTE_PRESETS) {
      if (preset.mode !== mode) continue;
      const b = canonical({
        palette: preset.palette,
        chrome: preset.chrome,
        chromeLight: preset.chromeLight,
      });
      if (a === b) return preset;
    }
  }
  for (const preset of PALETTE_PRESETS) {
    const b = canonical({
      palette: preset.palette,
      chrome: preset.chrome,
      chromeLight: preset.chromeLight,
    });
    if (a === b) return preset;
  }
  return null;
}

function canonical(input: {
  palette: Palette | null | undefined;
  chrome: Chrome | null | undefined;
  chromeLight: Chrome | null | undefined;
}): string {
  return JSON.stringify({
    palette: stripNulls(input.palette),
    chrome: stripNulls(input.chrome),
    chromeLight: stripNulls(input.chromeLight),
  });
}

function stripNulls(obj: Record<string, unknown> | null | undefined): Record<string, unknown> {
  if (!obj) return {};
  const out: Record<string, unknown> = {};
  // Sort keys so JSON.stringify produces a deterministic string regardless
  // of property insertion order (the API and the preset registry could
  // disagree on order without this).
  for (const key of Object.keys(obj).sort()) {
    const v = obj[key];
    if (v !== null && v !== undefined) out[key] = v;
  }
  return out;
}
