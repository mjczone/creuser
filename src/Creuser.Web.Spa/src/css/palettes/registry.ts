import type { BrandingConfig } from 'src/api';

/**
 * Curated palette presets. Each entry is a complete `palette + chrome
 * (dark) + chromeLight` snippet — applying a preset overwrites those
 * three fields on the BrandingConfig draft. Identity (productName, logo,
 * favicon, fonts, customCss) and the admin's default-mode preference are
 * preserved.
 *
 * Why hand-curated rather than something like nice-color-palettes:
 *   1. UI palettes need explicit slot assignments (primary / secondary /
 *      accent / positive / negative / info / warning) that 5-color art
 *      palettes don't model.
 *   2. Chrome tokens (page < surface < header/sidebar < elevated) need
 *      careful relative-lightness tuning that doesn't emerge from random
 *      pretty colors.
 *   3. Recognizable named themes ("GitHub", "Solarized Dark", etc.) are
 *      a stronger UX than abstract "Palette #47".
 */

type Palette = NonNullable<BrandingConfig['palette']>;
type Chrome = NonNullable<BrandingConfig['chrome']>;

export type PresetMode = 'dark' | 'light' | 'both';

export interface PalettePreset {
  id: string;
  label: string;
  description: string;
  /**
   * Which mode the preset is *designed for*. The picker groups by this so
   * admins can tell at a glance whether a preset will look right in their
   * current mode. `both` means the preset specifies palette + dark chrome +
   * light chrome that read coherently in either mode.
   */
  mode: PresetMode;
  /** Small set of representative colors for the picker swatch row. */
  swatches: string[];
  palette: Palette;
  /** Chrome applied in dark mode. */
  chrome: Chrome;
  /** Chrome applied in light mode. Empty preserves the bundled neutral defaults. */
  chromeLight: Chrome;
}

// ─────────────────────────────────────────────────────────────────────────
// Both-mode presets — designed to work in dark or light
// ─────────────────────────────────────────────────────────────────────────

const DEFAULT: PalettePreset = {
  id: 'creuser-default',
  label: 'Creuser Default',
  description: 'Sage green on dark teal-forest. Matches the Creuser logo.',
  mode: 'both',
  swatches: ['#5c7e62', '#7c9a82', '#d7a06b', '#143734', '#0c2422'],
  palette: {},
  chrome: {},
  chromeLight: {},
};

const GITHUB: PalettePreset = {
  id: 'github',
  label: 'GitHub',
  description: 'Neutral gray with a clean blue accent.',
  mode: 'both',
  swatches: ['#0969da', '#bf3989', '#1f883d', '#0d1117', '#21262d'],
  palette: {
    primary: '#0969da',
    secondary: '#6e7781',
    accent: '#bf3989',
    positive: '#1f883d',
    negative: '#cf222e',
    info: '#0969da',
    warning: '#bf8700',
  },
  chrome: {
    bgPage: '#0d1117',
    bgSurface: '#161b22',
    bgHeader: '#161b22',
    bgSidebar: '#161b22',
    bgElevated: '#21262d',
    fgPrimary: 'rgba(230, 237, 243, 0.95)',
    fgSecondary: 'rgba(173, 186, 199, 0.85)',
    fgTertiary: 'rgba(125, 133, 144, 0.85)',
    borderSubtle: 'rgba(48, 54, 61, 0.5)',
    borderDefault: '#30363d',
    borderStrong: '#484f58',
  },
  chromeLight: {
    bgPage: '#ffffff',
    bgSurface: '#ffffff',
    bgHeader: '#f6f8fa',
    bgSidebar: '#f6f8fa',
    bgElevated: '#eaeef2',
    fgPrimary: 'rgba(31, 35, 40, 0.95)',
    fgSecondary: 'rgba(101, 109, 118, 0.95)',
    fgTertiary: 'rgba(101, 109, 118, 0.7)',
    borderSubtle: 'rgba(208, 215, 222, 0.5)',
    borderDefault: '#d0d7de',
    borderStrong: '#afb8c1',
  },
};

const NORD: PalettePreset = {
  id: 'nord',
  label: 'Nord',
  description: 'Cool, frosty blues on muted polar night.',
  mode: 'both',
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
    fgPrimary: 'rgba(236, 239, 244, 0.95)',
    fgSecondary: 'rgba(216, 222, 233, 0.8)',
    fgTertiary: 'rgba(216, 222, 233, 0.55)',
    borderSubtle: 'rgba(67, 76, 94, 0.6)',
    borderDefault: '#434c5e',
    borderStrong: '#4c566a',
  },
  chromeLight: {
    bgPage: '#eceff4',
    bgSurface: '#e5e9f0',
    bgHeader: '#e5e9f0',
    bgSidebar: '#e5e9f0',
    bgElevated: '#d8dee9',
    fgPrimary: 'rgba(46, 52, 64, 0.95)',
    fgSecondary: 'rgba(59, 66, 82, 0.8)',
    fgTertiary: 'rgba(59, 66, 82, 0.55)',
    borderSubtle: 'rgba(216, 222, 233, 0.6)',
    borderDefault: '#d8dee9',
    borderStrong: '#c0c5cf',
  },
};

// ─────────────────────────────────────────────────────────────────────────
// Dark-mode presets
// ─────────────────────────────────────────────────────────────────────────

const SOLARIZED_DARK: PalettePreset = {
  id: 'solarized-dark',
  label: 'Solarized Dark',
  description: "Ethan Schoonover's classic — muted accents on warm teal.",
  mode: 'dark',
  swatches: ['#268bd2', '#859900', '#dc322f', '#073642', '#002b36'],
  palette: {
    primary: '#268bd2',
    secondary: '#2aa198',
    accent: '#d33682',
    positive: '#859900',
    negative: '#dc322f',
    info: '#268bd2',
    warning: '#b58900',
  },
  chrome: {
    bgPage: '#002b36',
    bgSurface: '#073642',
    bgHeader: '#073642',
    bgSidebar: '#073642',
    bgElevated: '#0d4453',
    fgPrimary: 'rgba(238, 232, 213, 0.95)',
    fgSecondary: 'rgba(147, 161, 161, 0.95)',
    fgTertiary: 'rgba(101, 123, 131, 0.95)',
    borderSubtle: 'rgba(7, 54, 66, 0.6)',
    borderDefault: '#0d4453',
    borderStrong: '#134659',
  },
  chromeLight: {},
};

const ONE_DARK: PalettePreset = {
  id: 'one-dark',
  label: 'One Dark',
  description: "Atom's default — soft lavender + amber on slate.",
  mode: 'dark',
  swatches: ['#61afef', '#c678dd', '#98c379', '#282c34', '#3e4451'],
  palette: {
    primary: '#61afef',
    secondary: '#c678dd',
    accent: '#d19a66',
    positive: '#98c379',
    negative: '#e06c75',
    info: '#56b6c2',
    warning: '#e5c07b',
  },
  chrome: {
    bgPage: '#282c34',
    bgSurface: '#21252b',
    bgHeader: '#21252b',
    bgSidebar: '#21252b',
    bgElevated: '#3e4451',
    fgPrimary: 'rgba(220, 223, 228, 0.95)',
    fgSecondary: 'rgba(171, 178, 191, 0.85)',
    fgTertiary: 'rgba(125, 133, 144, 0.85)',
    borderSubtle: 'rgba(62, 68, 81, 0.6)',
    borderDefault: '#3e4451',
    borderStrong: '#5c6370',
  },
  chromeLight: {},
};

const DRACULA: PalettePreset = {
  id: 'dracula',
  label: 'Dracula',
  description: 'Vivid purple + pink on deep slate.',
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
};

const TOKYO_NIGHT: PalettePreset = {
  id: 'tokyo-night',
  label: 'Tokyo Night',
  description: 'Cool blues + violets on midnight indigo.',
  mode: 'dark',
  swatches: ['#7aa2f7', '#bb9af7', '#9ece6a', '#1a1b26', '#24283b'],
  palette: {
    primary: '#7aa2f7',
    secondary: '#bb9af7',
    accent: '#7dcfff',
    positive: '#9ece6a',
    negative: '#f7768e',
    info: '#7dcfff',
    warning: '#e0af68',
  },
  chrome: {
    bgPage: '#1a1b26',
    bgSurface: '#24283b',
    bgHeader: '#24283b',
    bgSidebar: '#24283b',
    bgElevated: '#2f334d',
    fgPrimary: 'rgba(192, 202, 245, 0.95)',
    fgSecondary: 'rgba(154, 165, 206, 0.85)',
    fgTertiary: 'rgba(86, 95, 137, 0.95)',
    borderSubtle: 'rgba(47, 51, 77, 0.6)',
    borderDefault: '#2f334d',
    borderStrong: '#414868',
  },
  chromeLight: {},
};

// ─────────────────────────────────────────────────────────────────────────
// Light-mode presets
// ─────────────────────────────────────────────────────────────────────────

const SOLARIZED_LIGHT: PalettePreset = {
  id: 'solarized-light',
  label: 'Solarized Light',
  description: 'The light counterpart — same accents, parchment backgrounds.',
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
    fgPrimary: 'rgba(7, 54, 66, 0.95)',
    fgSecondary: 'rgba(88, 110, 117, 0.95)',
    fgTertiary: 'rgba(147, 161, 161, 0.95)',
    borderSubtle: 'rgba(238, 232, 213, 0.8)',
    borderDefault: '#e0dac0',
    borderStrong: '#cdc69c',
  },
};

const ONE_LIGHT: PalettePreset = {
  id: 'one-light',
  label: 'One Light',
  description: "Atom's light counterpart — clean sky-blue on warm white.",
  mode: 'light',
  swatches: ['#4078f2', '#a626a4', '#50a14f', '#fafafa', '#ffffff'],
  palette: {
    primary: '#4078f2',
    secondary: '#a626a4',
    accent: '#c18401',
    positive: '#50a14f',
    negative: '#e45649',
    info: '#0184bc',
    warning: '#c18401',
  },
  chrome: {},
  chromeLight: {
    bgPage: '#fafafa',
    bgSurface: '#ffffff',
    bgHeader: '#ffffff',
    bgSidebar: '#f3f3f3',
    bgElevated: '#ececec',
    fgPrimary: 'rgba(56, 58, 66, 0.95)',
    fgSecondary: 'rgba(112, 117, 125, 0.95)',
    fgTertiary: 'rgba(160, 161, 167, 0.95)',
    borderSubtle: 'rgba(229, 229, 230, 0.7)',
    borderDefault: '#e5e5e6',
    borderStrong: '#bfbfc2',
  },
};

const CATPPUCCIN_LATTE: PalettePreset = {
  id: 'catppuccin-latte',
  label: 'Catppuccin Latte',
  description: 'Pastel-soft palette on a cream-paper background.',
  mode: 'light',
  swatches: ['#1e66f5', '#dd7878', '#40a02b', '#eff1f5', '#dce0e8'],
  palette: {
    primary: '#1e66f5',
    secondary: '#179299',
    accent: '#dd7878',
    positive: '#40a02b',
    negative: '#d20f39',
    info: '#04a5e5',
    warning: '#df8e1d',
  },
  chrome: {},
  chromeLight: {
    bgPage: '#eff1f5',
    bgSurface: '#e6e9ef',
    bgHeader: '#e6e9ef',
    bgSidebar: '#dce0e8',
    bgElevated: '#ccd0da',
    fgPrimary: 'rgba(76, 79, 105, 0.95)',
    fgSecondary: 'rgba(108, 111, 133, 0.95)',
    fgTertiary: 'rgba(140, 143, 161, 0.95)',
    borderSubtle: 'rgba(204, 208, 218, 0.6)',
    borderDefault: '#bcc0cc',
    borderStrong: '#9ca0b0',
  },
};

export const PALETTE_PRESETS: PalettePreset[] = [
  // Both-mode (universally applicable) — surface first
  DEFAULT,
  GITHUB,
  NORD,
  // Dark-only
  SOLARIZED_DARK,
  ONE_DARK,
  DRACULA,
  TOKYO_NIGHT,
  // Light-only
  SOLARIZED_LIGHT,
  ONE_LIGHT,
  CATPPUCCIN_LATTE,
];

/** Group presets by mode for the picker UI. */
export function groupPresetsByMode(): Record<PresetMode, PalettePreset[]> {
  return {
    both: PALETTE_PRESETS.filter((p) => p.mode === 'both'),
    dark: PALETTE_PRESETS.filter((p) => p.mode === 'dark'),
    light: PALETTE_PRESETS.filter((p) => p.mode === 'light'),
  };
}

/**
 * Detect which preset (if any) the current config matches by exact equality
 * of its palette + chrome fields. Used by the picker to highlight the
 * active preset. Returns null when the user has manually tweaked colors
 * (no preset matches), which the picker shows as "Custom".
 */
export function detectActivePreset(
  palette: Palette | null | undefined,
  chrome: Chrome | null | undefined,
  chromeLight: Chrome | null | undefined,
): PalettePreset | null {
  const a = JSON.stringify({
    palette: palette ?? {},
    chrome: chrome ?? {},
    chromeLight: chromeLight ?? {},
  });
  for (const preset of PALETTE_PRESETS) {
    const b = JSON.stringify({
      palette: preset.palette,
      chrome: preset.chrome,
      chromeLight: preset.chromeLight,
    });
    if (a === b) return preset;
  }
  return null;
}
