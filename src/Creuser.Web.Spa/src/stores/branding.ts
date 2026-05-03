import { defineStore, acceptHMRUpdate } from 'pinia';
import { computed, ref } from 'vue';
import { setCssVar } from 'quasar';
import { Branding } from 'src/api';
import type { BrandingConfig as ApiBrandingConfig } from 'src/api';
import { useThemeModeStore } from 'stores/themeMode';
import { loadIfBundled } from 'src/css/fonts/registry';

export type BrandingConfig = ApiBrandingConfig;

// Exported so the Branding settings page can use it as the target for
// "Reset all to defaults". Marked readonly because callers should never
// mutate the canonical default in place — clone it (JSON.parse(JSON.stringify))
// before assigning to a draft.
export const defaultBranding: BrandingConfig = {
  productName: 'Creuser',
  // Match the C# BrandingConfig.Default — points at the bundled logo + ico
  // in `public/`. Used when the API call fails or hasn't returned yet so
  // there's no flash of an unbranded "C" placeholder before the API hydrates.
  logoUrl: '/logo.svg',
  faviconUrl: '/favicon.ico',
  loginBackgroundUrl: null,
  loginTagline: 'Workflow & agent orchestration',
  mode: 'dark',
  palette: {},
  chrome: {},
  chromeLight: {},
  fontFamily: null,
  fontFamilyMono: null,
  customCss: null,
  footerText: null,
  supportEmail: null,
};

const PALETTE_KEYS = [
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

// Quasar's setCssVar uses kebab-case keys for `dark-page` etc.
function quasarKey(k: string): string {
  return k === 'darkPage' ? 'dark-page' : k;
}

// Chrome keys are camelCase on the wire, kebab-case as `--cr-*` CSS vars.
function chromeCssName(k: string): string {
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

function buildTypographyRules(next: BrandingConfig): string {
  const declarations: string[] = [];
  if (next.fontFamily) declarations.push(`  --cr-font-family: ${next.fontFamily};`);
  if (next.fontFamilyMono)
    declarations.push(`  --cr-font-family-mono: ${next.fontFamilyMono};`);
  return declarations.length ? `:root {\n${declarations.join('\n')}\n}` : '';
}

/**
 * Build the full stylesheet body that the injected style tag carries.
 * Order matters — later rules override earlier ones:
 *   1. typography (font-family vars on :root)
 *   2. dark-mode chrome (:root)
 *   3. light-mode chrome (.body--light)
 *   4. admin's custom CSS (wins over everything)
 *
 * Quasar palette colors (--q-primary, etc.) are NOT in this stylesheet.
 * They're set via `setCssVar` directly on the document root because Quasar
 * components reference them with the same `:root` precedence and we want
 * a single source of truth for them.
 */
function buildStyleSheet(next: BrandingConfig): string {
  const blocks = [
    buildTypographyRules(next),
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

  /**
   * Push a config into the live document — visual side effects only.
   * Quasar palette colors go via `setCssVar` (matches Quasar's own component
   * expectations); chrome tokens, typography, and custom CSS go into a
   * single injected `<style>` tag so per-mode rules and admin-authored
   * selectors apply with the right precedence.
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

    // Clear any prior palette overrides before applying the new ones.
    // Quasar's `setCssVar` writes inline styles onto document.body; if a new
    // preset omits a key (e.g. Creuser Default uses an empty palette to fall
    // back to the SCSS build-time defaults), the prior preset's inline value
    // for that key would otherwise persist. Remove all known palette
    // properties first, then re-apply only the values present in `next`.
    const palette = (next.palette ?? {}) as Record<string, string | null | undefined>;
    for (const key of PALETTE_KEYS) {
      document.body.style.removeProperty(`--q-${quasarKey(key)}`);
    }
    for (const key of PALETTE_KEYS) {
      const value = palette[key];
      if (value) setCssVar(quasarKey(key), value);
    }

    // Lazy-load any bundled fontsource font referenced by this config.
    // Fire-and-forget — the fallback stack in cssFamily renders correctly
    // until the woff2 finishes loading, then the browser swaps the glyphs.
    loadIfBundled(next.fontFamily);
    loadIfBundled(next.fontFamilyMono);

    ensureStyleTag().textContent = buildStyleSheet(next);

    // Favicon defaults to the uploaded logo when no separate faviconUrl is
    // set — one upload covers both surfaces, with an override path for
    // organizations that want a different glyph in the browser tab.
    setFavicon(next.faviconUrl ?? next.logoUrl);
  }

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
