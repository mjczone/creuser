import { defineStore, acceptHMRUpdate } from 'pinia';
import { computed, ref, watch } from 'vue';
import { Branding } from 'src/api';
import type { BrandingConfig as ApiBrandingConfig } from 'src/api';
import { useThemeModeStore } from 'stores/themeMode';
import { loadIfBundled } from 'src/css/fonts/registry';
import {
  CREUSER_BUNDLED_PALETTE,
  CREUSER_BUNDLED_CHROME_DARK,
  CREUSER_BUNDLED_CHROME_LIGHT,
} from 'src/css/palettes/registry';

export type BrandingConfig = ApiBrandingConfig;

// Exported so the Branding settings page can use it as the target for
// "Reset all to defaults". Marked readonly because callers should never
// mutate the canonical default in place — clone it (JSON.parse(JSON.stringify))
// before assigning to a draft.
export const defaultBranding: BrandingConfig = {
  productName: 'Creuser',
  // Match the C# BrandingConfig.Default — points at the bundled per-mode
  // Creuser logos plus the icongenie-regenerated favicon, all under
  // `public/`. Used when the API call fails or hasn't returned yet so
  // there's no flash of an unbranded "C" placeholder before the API
  // hydrates. The two logo URLs let the sidebar / login / favicon pick
  // the right asset for the user's effective mode out of the box.
  logoUrl: '/logo-dark.svg',
  logoUrlLight: '/logo-light.svg',
  faviconUrl: '/favicon.ico',
  loginBackgroundUrl: null,
  loginTagline: 'Workflow & agent orchestration',
  mode: 'dark',
  // Each slot mirrors the corresponding bundled SCSS values so Reset All
  // (which copies defaultBranding into the draft) lands on a config that
  // exactly matches the Creuser Dark + Creuser Light presets — the
  // detect-active-preset comparison is a strict canonical equality, so an
  // empty `{}` here would read as "Custom" instead of "Creuser Dark".
  // Sourced from src/css/palettes/registry.ts (which itself mirrors the
  // SCSS — single edit point per side, two-way comments keep them honest).
  palette: { ...CREUSER_BUNDLED_PALETTE },
  paletteLight: { ...CREUSER_BUNDLED_PALETTE },
  chrome: { ...CREUSER_BUNDLED_CHROME_DARK },
  chromeLight: { ...CREUSER_BUNDLED_CHROME_LIGHT },
  fontFamily: null,
  fontFamilyMono: null,
  customCss: null,
  footerText: null,
  supportEmail: null,
};

// Every URL in this set is a bundled-default placeholder, not an admin
// choice. Treated as "unset" by the favicon picker so a row with
// `logoUrl: "/logo-dark.svg"` falls through to the bundled `.ico` instead
// of trying to render the generic SVG as a 16x16 tab icon.
//
// Includes `/logo.svg` for older saved configs that still point at it,
// even though the current default is the per-mode pair.
const BUNDLED_LOGO_URLS = new Set<string>(['/logo.svg', '/logo-dark.svg', '/logo-light.svg']);

export const PALETTE_KEYS = [
  'primary',
  'secondary',
  'accent',
  'positive',
  'negative',
  'info',
  'warning',
  'dark',
  'darkPage',
] as const satisfies readonly (keyof NonNullable<BrandingConfig['palette']>)[];

export const CHROME_KEYS = [
  'bgPage',
  'bgSurface',
  'bgHeader',
  'bgSidebar',
  'bgElevated',
  'fgPrimary',
  'fgSecondary',
  'fgTertiary',
  'borderSubtle',
  'borderDefault',
  'borderStrong',
] as const satisfies readonly (keyof NonNullable<BrandingConfig['chrome']>)[];

export type ChromeKey = (typeof CHROME_KEYS)[number];

const STYLE_TAG_ID = 'cr-branding-overrides';

// Quasar's setCssVar uses kebab-case keys for `dark-page` etc. Exported so
// the BrandingPage's "insert baseline tokens" button can format `--q-*`
// names the same way the runtime applier does.
export function quasarKey(k: string): string {
  return k === 'darkPage' ? 'dark-page' : k;
}

// Chrome keys are camelCase on the wire, kebab-case as `--cr-*` CSS vars.
export function chromeCssName(k: string): string {
  return `--cr-${k.replace(/[A-Z]/g, (m) => '-' + m.toLowerCase())}`;
}

function buildChromeRules(
  selector: string,
  tokens: Record<string, string | null | undefined> | null | undefined,
): string {
  if (!tokens) return '';
  const declarations = CHROME_KEYS.map((key) => {
    const value = tokens[key];
    return value ? `  ${chromeCssName(key)}: ${value};` : null;
  })
    .filter((s): s is string => s !== null)
    .join('\n');
  return declarations ? `${selector} {\n${declarations}\n}` : '';
}

// Quasar palette is now applied via the same injected stylesheet that handles
// chrome — `:root { --q-primary: ... }` for the dark slot and
// `.body--light { --q-primary: ... }` for the light slot — so a header theme
// toggle flips Quasar's brand colors via CSS cascade without any JS work.
// Keys not set in `paletteLight` simply inherit the `:root` value, which
// matches the legacy single-palette behavior for partially-customized
// configs.
function buildPaletteRules(
  selector: string,
  palette: Record<string, string | null | undefined> | null | undefined,
): string {
  if (!palette) return '';
  const declarations = PALETTE_KEYS.map((key) => {
    const value = palette[key];
    return value ? `  --q-${quasarKey(key)}: ${value};` : null;
  })
    .filter((s): s is string => s !== null)
    .join('\n');
  return declarations ? `${selector} {\n${declarations}\n}` : '';
}

function buildTypographyRules(next: BrandingConfig): string {
  const declarations: string[] = [];
  if (next.fontFamily) declarations.push(`  --cr-font-family: ${next.fontFamily};`);
  if (next.fontFamilyMono) declarations.push(`  --cr-font-family-mono: ${next.fontFamilyMono};`);
  return declarations.length ? `:root {\n${declarations.join('\n')}\n}` : '';
}

/**
 * Build the full stylesheet body that the injected style tag carries.
 * Order matters — later rules override earlier ones:
 *   1. typography (font-family vars on :root)
 *   2. dark-mode palette (:root)
 *   3. light-mode palette (.body--light)
 *   4. dark-mode chrome (:root)
 *   5. light-mode chrome (.body--light)
 *   6. admin's custom CSS (wins over everything)
 *
 * Both Quasar palette tokens (`--q-*`) and Creuser chrome tokens (`--cr-*`)
 * live in this stylesheet. The `:root` block sets the dark-mode values; the
 * `.body--light` block overrides for light. When the user toggles their
 * theme (top right), Quasar swaps the body class and the cascade does the
 * rest — no JS palette work happens on toggle.
 */
function buildStyleSheet(next: BrandingConfig): string {
  const blocks = [
    buildTypographyRules(next),
    buildPaletteRules(':root', next.palette ?? null),
    buildPaletteRules('.body--light', next.paletteLight ?? null),
    buildChromeRules(':root', next.chrome ?? null),
    buildChromeRules('.body--light', next.chromeLight ?? null),
    next.customCss?.trim() ? `/* custom */\n${next.customCss}` : '',
  ].filter(Boolean);
  return blocks.join('\n\n');
}

/**
 * Fall through to the bundled defaults for nullable identity fields when
 * the saved config has them blank. Pre-existing rows in `cr.app_settings`
 * may have been written before the defaults pointed at `/logo.svg` /
 * `/favicon.ico` — without this merge the chrome would render the
 * placeholder initial instead of the brand logo for those deployments.
 *
 * Only nullable identity fields fall through. The palette / chrome / mode
 * fields are admin-meaningful as null/empty (= "use the baked-in default")
 * and the rest of the apply pipeline already handles those correctly.
 */
function mergeWithDefaults(saved: BrandingConfig): BrandingConfig {
  return {
    ...saved,
    logoUrl: saved.logoUrl ?? defaultBranding.logoUrl,
    logoUrlLight: saved.logoUrlLight ?? defaultBranding.logoUrlLight,
    faviconUrl: saved.faviconUrl ?? defaultBranding.faviconUrl,
    loginTagline: saved.loginTagline ?? defaultBranding.loginTagline,
  };
}

function ensureStyleTag(): HTMLStyleElement {
  let el = document.getElementById(STYLE_TAG_ID) as HTMLStyleElement | null;
  if (!el) {
    el = document.createElement('style');
    el.id = STYLE_TAG_ID;
    document.head.appendChild(el);
  }
  return el;
}

/**
 * Update the document's favicon. We take exclusive ownership of the icon
 * link so this is reliable across browsers — the bundled index.html has
 * five `<link rel="icon">` tags (16/32/96/128 + ICO) and different
 * browsers pick different ones, so updating just the first wouldn't
 * always swap the tab. Strategy:
 *
 *   1. Remove every `<link rel="icon">` we don't own.
 *   2. Maintain a single managed link (id="cr-favicon") whose href is
 *      driven by the branding config.
 *   3. When no logo / faviconUrl is configured, fall back to the bundled
 *      /favicon.ico that icongenie generated.
 *
 * The branded asset URL is content-addressed and served with
 * `Cache-Control: immutable`, so updating `link.href` is enough — no
 * cache-busting tricks needed.
 */
const FAVICON_LINK_ID = 'cr-favicon';

function setFavicon(url: string | null) {
  for (const link of document.head.querySelectorAll<HTMLLinkElement>("link[rel='icon']")) {
    if (link.id !== FAVICON_LINK_ID) link.remove();
  }

  let managed = document.getElementById(FAVICON_LINK_ID) as HTMLLinkElement | null;
  if (!managed) {
    managed = document.createElement('link');
    managed.id = FAVICON_LINK_ID;
    managed.rel = 'icon';
    document.head.appendChild(managed);
  }

  managed.href = url ?? '/favicon.ico';
}

// The bundled URLs in `defaultBranding` double as "no admin choice yet"
// sentinels — they're seeded into the C# default and persisted to
// `cr.app_settings` on first save, so a row with `faviconUrl: "/favicon.ico"`
// almost always means "admin never picked one" rather than "admin deliberately
// pointed the favicon at the bundled ICO". Treat those literal defaults as
// unset so a logo upload becomes the favicon without requiring a separate
// favicon field. An admin who genuinely wants a custom favicon can set
// `faviconUrl` to any other URL via the API and that value wins.
//
// `isLight` mirrors the sidebar/login resolution from `effectiveLogoUrl`:
// when the user is in light mode and the admin uploaded a `logoUrlLight`,
// the favicon picks that asset; otherwise it falls back to the dark-mode
// logo. The store wires this to `themeMode.effective` so toggling the
// header theme reactively swaps the browser-tab icon too.
function pickFaviconHref(b: BrandingConfig, isLight: boolean): string | null {
  const customFavicon =
    b.faviconUrl && b.faviconUrl !== defaultBranding.faviconUrl ? b.faviconUrl : null;
  const lightLogo =
    isLight && b.logoUrlLight && b.logoUrlLight.trim().length > 0 ? b.logoUrlLight : null;
  const effectiveLogo = lightLogo ?? b.logoUrl;
  const customLogo =
    effectiveLogo && !BUNDLED_LOGO_URLS.has(effectiveLogo) ? effectiveLogo : null;
  return customFavicon ?? customLogo;
}

export const useBrandingStore = defineStore('branding', () => {
  // `config` is the canonical saved state — what's persisted on the server.
  // The Branding page's dirty-tracking compares its draft against this, and
  // `onLiveChange` reads the saved `customCss` from this so in-progress CSS
  // doesn't leak into the preview.
  const config = ref<BrandingConfig>(defaultBranding);

  // `liveConfig` is what's currently applied to the document — driven by
  // `preview()` during live edits and `apply()` on load/save. Chrome
  // consumers (sidebar logo, header product name, login screen) read from
  // here so unsaved drafts immediately reflect in the page chrome without
  // marking the saved config dirty.
  const liveConfig = ref<BrandingConfig>(defaultBranding);

  const isLoaded = ref(false);
  const isSaving = ref(false);

  const productName = computed(() => liveConfig.value.productName);
  const logoUrl = computed(() => liveConfig.value.logoUrl);
  const mode = computed(() => liveConfig.value.mode);

  // The logo asset that should render at the user's *current* effective
  // theme — picks `logoUrlLight` in light mode when the admin uploaded one,
  // otherwise falls back to `logoUrl`. Sidebar, login, and any other
  // chrome consumers should bind to this rather than `logoUrl` so a
  // header-toggle flip swaps the asset reactively.
  const effectiveLogoUrl = computed(() => {
    const themeMode = useThemeModeStore();
    if (themeMode.effective === 'light') {
      const lightLogo = liveConfig.value.logoUrlLight;
      if (lightLogo && lightLogo.trim().length > 0) return lightLogo;
    }
    return liveConfig.value.logoUrl;
  });

  /**
   * Push a config into the live document — visual side effects only.
   * Both the Quasar palette and Creuser chrome tokens go into a single
   * injected `<style>` tag, with `:root` carrying the dark-mode values and
   * `.body--light` overriding for light. The user's header theme toggle
   * already drives `Dark.set()` (via the themeMode store), which adds /
   * removes `.body--light` on the body — palette + chrome both flip via
   * cascade with no further JS work.
   *
   * Crucially this does NOT update `config.value` — it's the live-preview
   * surface. Callers that need to mark the config canonically saved (load,
   * save) should use `apply()` instead, which calls into here after
   * setting `config.value`.
   */
  function preview(next: BrandingConfig) {
    liveConfig.value = next;

    // The admin's `mode` is the *default* for users whose preference is
    // `auto` and whose browser has no `prefers-color-scheme` signal. The
    // theme-mode store owns the actual Quasar `Dark.set()` call so that
    // user preference (localStorage) wins over admin default.
    const themeMode = useThemeModeStore();
    themeMode.setAdminDefault(next.mode === 'light' ? 'light' : 'dark');

    // Earlier versions of this code applied palette via Quasar's
    // `setCssVar`, which writes inline styles onto document.body. Inline
    // styles outrank `:root` rules in our injected stylesheet, so any
    // leftover inline value would prevent the new per-mode CSS from
    // taking effect. Clear them once on every preview as a safety net.
    for (const key of PALETTE_KEYS) {
      document.body.style.removeProperty(`--q-${quasarKey(key)}`);
    }

    // Lazy-load any bundled fontsource font referenced by this config.
    // Fire-and-forget — the fallback stack in cssFamily renders correctly
    // until the woff2 finishes loading, then the browser swaps the glyphs.
    loadIfBundled(next.fontFamily);
    loadIfBundled(next.fontFamilyMono);

    ensureStyleTag().textContent = buildStyleSheet(next);

    // Favicon defaults to the uploaded logo when no separate faviconUrl is
    // set — one upload covers both surfaces, with an override path for
    // organizations that want a different glyph in the browser tab. Picks
    // the light-mode logo when the user is currently viewing light, mirroring
    // the sidebar/login resolution.
    setFavicon(pickFaviconHref(next, themeMode.effective === 'light'));
  }

  // Browser tab icon needs to track the user's effective mode reactively,
  // not just at preview() time. Without this watcher, a header theme toggle
  // flips the sidebar logo (via the `effectiveLogoUrl` computed) but the
  // favicon stays pinned to whatever mode preview() last ran in. Re-runs
  // setFavicon whenever the effective mode flips.
  const themeModeStore = useThemeModeStore();
  watch(
    () => themeModeStore.effective,
    (mode) => {
      setFavicon(pickFaviconHref(liveConfig.value, mode === 'light'));
    },
  );

  /**
   * Mark `next` as the canonical saved config and reflect it visually.
   * Called from `load()` and `save()` only — pages doing live preview
   * should use `preview()` so they don't clobber the dirty-tracking that
   * Save buttons depend on.
   */
  function apply(next: BrandingConfig) {
    config.value = next;
    preview(next);
  }

  async function load() {
    try {
      const res = await Branding.getBranding();
      const next = res.data?.result;
      apply(next ? mergeWithDefaults(next) : defaultBranding);
    } catch {
      apply(defaultBranding);
    } finally {
      isLoaded.value = true;
    }
  }

  async function save(next: BrandingConfig) {
    isSaving.value = true;
    try {
      const res = await Branding.updateBranding({ body: next });
      if (res.error) throw new Error('Failed to save branding.');
      const saved = res.data?.result ?? next;
      apply(mergeWithDefaults(saved));
    } finally {
      isSaving.value = false;
    }
  }

  return {
    config,
    liveConfig,
    isLoaded,
    isSaving,
    productName,
    logoUrl,
    effectiveLogoUrl,
    mode,
    preview,
    apply,
    load,
    save,
  };
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useBrandingStore, import.meta.hot));
}
