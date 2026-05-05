<template>
  <div class="cr-branding-page">
    <header class="cr-branding-header">
      <h1 class="text-h5 q-ma-none">Branding</h1>
      <p class="cr-branding-subhead">
        Logo, product name, color palette, chrome, typography. Changes apply live as you edit; click
        <strong>Save</strong> to persist for everyone.
      </p>
    </header>

    <q-form class="cr-branding-form" @submit.prevent="onSave">
      <q-expansion-item
        v-model="expanded.identity"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="Identity"
        caption="Product name, login tagline, logos"
        header-class="cr-branding-section-header"
        class="cr-branding-section"
        data-section-key="identity"
      >
        <div class="cr-branding-section-body">
          <q-input
            v-model="draft.productName"
            label="Product name"
            dense
            outlined
            :rules="[(v: string) => !!v.trim() || 'Required']"
            class="cr-branding-input"
            @update:model-value="onLiveChange"
          />

          <q-input
            v-model="loginTaglineField"
            label="Login page tagline"
            placeholder="Workflow & agent orchestration"
            hint="Small subhead shown under the product name on the login screen."
            dense
            outlined
            class="cr-branding-input"
          />

          <div class="cr-branding-logos">
            <div class="cr-branding-logo-slot">
              <div class="cr-branding-logo-slot-label">Logo (dark mode)</div>
              <LogoUploadField
                :model-value="draft.logoUrl"
                :alt="draft.productName"
                @update:model-value="onLogoChange"
              />
            </div>
            <div class="cr-branding-logo-slot">
              <div class="cr-branding-logo-slot-label">Logo (light mode)</div>
              <LogoUploadField
                :model-value="draft.logoUrlLight"
                :alt="draft.productName"
                @update:model-value="onLogoLightChange"
              />
              <p class="cr-branding-logo-slot-hint">
                Optional — leave empty to reuse the dark-mode logo in light mode too.
              </p>
            </div>
          </div>
        </div>
      </q-expansion-item>

      <q-expansion-item
        v-model="expanded.defaultMode"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="Default mode for new users"
        caption="Each user can override from the header"
        header-class="cr-branding-section-header"
        class="cr-branding-section"
        data-section-key="defaultMode"
      >
        <div class="cr-branding-section-body">
          <p class="cr-branding-section-hint">
            This is the fallback for users on <em>auto</em> whose browser has no
            <code>prefers-color-scheme</code> signal.
          </p>
          <q-btn-toggle
            v-model="draft.mode"
            :options="[
              { label: 'Dark', value: 'dark' },
              { label: 'Light', value: 'light' },
            ]"
            unelevated
            no-caps
            toggle-color="primary"
            class="cr-branding-toggle"
            @update:model-value="onLiveChange"
          />
        </div>
      </q-expansion-item>

      <q-expansion-item
        v-model="expanded.typography"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="Typography"
        caption="Body + monospace fonts"
        header-class="cr-branding-section-header"
        class="cr-branding-section"
        data-section-key="typography"
      >
        <div class="cr-branding-section-body">
          <p class="cr-branding-section-hint">
            Pick from the curated bundled set, leave on <em>System default</em> for the OS stack, or
            choose <em>Custom</em> to paste a font-family list. Bundled fonts are lazy-loaded — the
            woff2 only ships when an admin selects it. Typography applies to both dark and light
            modes.
          </p>

          <FontPicker
            :model-value="fontFamilyField"
            type="sans"
            label="Body font"
            @update:model-value="onFontFamilyChange"
          />
          <FontPicker
            :model-value="fontFamilyMonoField"
            type="mono"
            label="Monospace font"
            @update:model-value="onFontFamilyMonoChange"
          />
        </div>
      </q-expansion-item>

      <q-expansion-item
        v-model="expanded.presets"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="Base theme presets"
        caption="Pick a starting point for each mode"
        header-class="cr-branding-section-header"
        class="cr-branding-section"
        data-section-key="presets"
      >
        <div class="cr-branding-section-body">
          <p class="cr-branding-section-hint">
            Each mode has its own base. Picking a preset overwrites that mode's palette + chrome
            below; identity, fonts, the other mode's customizations, and custom CSS are preserved.
            Tweak afterward in the Palette and Chrome sections.
          </p>

          <div class="cr-branding-presets-pair">
            <div class="cr-branding-preset-slot">
              <h3 class="cr-branding-preset-slot-title">Dark mode base</h3>
              <PalettePicker mode="dark" :active-id="activeDarkPresetId" @pick="onPickDarkPreset" />
            </div>
            <div class="cr-branding-preset-slot">
              <h3 class="cr-branding-preset-slot-title">Light mode base</h3>
              <PalettePicker
                mode="light"
                :active-id="activeLightPresetId"
                @pick="onPickLightPreset"
              />
            </div>
          </div>
        </div>
      </q-expansion-item>

      <q-expansion-item
        v-model="expanded.palette"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="Palette"
        caption="Quasar brand colors per mode"
        header-class="cr-branding-section-header"
        class="cr-branding-section"
        data-section-key="palette"
      >
        <div class="cr-branding-section-body">
          <p class="cr-branding-section-hint">
            Hex (or rgba) values. Leave blank to use the baked-in default. Edits made in the Dark
            tab affect what users see in dark mode; edits in the Light tab affect light mode. Use
            the theme switcher (top right) if you want to compare your changes live.
          </p>

          <q-tabs
            v-model="paletteEditMode"
            dense
            no-caps
            align="left"
            class="cr-branding-mode-tabs"
            indicator-color="primary"
            active-color="primary"
          >
            <q-tab name="dark" label="Dark" />
            <q-tab name="light" label="Light" />
          </q-tabs>

          <div class="cr-branding-grid">
            <ColorField
              v-for="entry in paletteFields"
              :key="entry.key"
              :label="entry.label"
              :model-value="paletteValue(entry.key)"
              @update:model-value="(v) => setPalette(entry.key, v)"
            />
          </div>
        </div>
      </q-expansion-item>

      <q-expansion-item
        v-model="expanded.chrome"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="Chrome"
        caption="Background, foreground, border tokens per mode"
        header-class="cr-branding-section-header"
        class="cr-branding-section"
        data-section-key="chrome"
      >
        <div class="cr-branding-section-body">
          <p class="cr-branding-section-hint">
            Background, foreground, and border tokens for the app shell. The Dark and Light tabs
            edit each mode independently — your current viewing mode (top right) doesn't affect
            which slot you're editing.
          </p>

          <q-tabs
            v-model="chromeEditMode"
            dense
            no-caps
            align="left"
            class="cr-branding-mode-tabs"
            indicator-color="primary"
            active-color="primary"
          >
            <q-tab name="dark" label="Dark" />
            <q-tab name="light" label="Light" />
          </q-tabs>

          <div class="cr-branding-grid">
            <ColorField
              v-for="entry in chromeFields"
              :key="entry.key"
              :label="entry.label"
              :model-value="chromeValue(entry.key)"
              @update:model-value="(v) => setChrome(entry.key, v)"
            />
          </div>
        </div>
      </q-expansion-item>

      <q-expansion-item
        v-model="expanded.customCss"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="Custom CSS"
        caption="Escape hatch for arbitrary rules"
        header-class="cr-branding-section-header"
        class="cr-branding-section"
        data-section-key="customCss"
      >
        <div class="cr-branding-section-body">
          <p class="cr-branding-section-hint">
            Injected last, so it wins over the structured overrides above. Use the available tokens
            or target arbitrary selectors.
          </p>

          <div class="cr-branding-css-actions">
            <q-btn
              flat
              dense
              no-caps
              icon="content_paste"
              label="Insert baseline tokens"
              size="sm"
              @click="onInsertBaseline"
            >
              <q-tooltip>
                Drop a starter block — Quasar palette + Creuser chrome (both modes) plus a commented
                dockview block — into the editor. Useful as a starting point when customizing.
              </q-tooltip>
            </q-btn>
          </div>

          <details class="cr-branding-help">
            <summary>Available tokens and mode selectors</summary>
            <div class="cr-branding-help-body">
              <p>
                <strong
                  >Per-mode rules — wrap any selector with the body class for that mode:</strong
                >
              </p>
              <pre>
body.body--dark  .cr-drawer { background: #0c2422; }
body.body--light .cr-drawer { background: #ffffff; color: #1f2328; }</pre
              >
              <p>
                <strong>Available <code>--cr-*</code> tokens (overridable directly):</strong>
              </p>
              <ul>
                <li>
                  <code>--cr-bg-page</code>, <code>--cr-bg-surface</code>,
                  <code>--cr-bg-header</code>, <code>--cr-bg-sidebar</code>,
                  <code>--cr-bg-elevated</code>
                </li>
                <li>
                  <code>--cr-fg-primary</code>, <code>--cr-fg-secondary</code>,
                  <code>--cr-fg-tertiary</code>, <code>--cr-fg-on-brand</code>
                </li>
                <li>
                  <code>--cr-border-subtle</code>, <code>--cr-border-default</code>,
                  <code>--cr-border-strong</code>, <code>--cr-border-header</code>,
                  <code>--cr-border-logo</code>
                </li>
                <li>
                  <code>--cr-bg-hover</code>, <code>--cr-bg-active</code>,
                  <code>--cr-brand-tint-soft</code>, <code>--cr-brand-tint-medium</code>
                </li>
                <li><code>--cr-font-family</code>, <code>--cr-font-family-mono</code></li>
              </ul>
              <p>
                <strong>Quasar palette tokens:</strong> <code>--q-primary</code>,
                <code>--q-secondary</code>, <code>--q-accent</code>, <code>--q-positive</code>,
                <code>--q-negative</code>, <code>--q-info</code>, <code>--q-warning</code>,
                <code>--q-dark</code>, <code>--q-dark-page</code>.
              </p>
            </div>
          </details>

          <q-input
            v-model="customCssField"
            type="textarea"
            autogrow
            dense
            outlined
            class="cr-branding-css"
            input-class="cr-branding-css-input"
            placeholder="/* Plain CSS — applied last, wins over the structured overrides. Applies on Save (typing CSS live can break the page mid-edit).&#10;&#10;   Use body.body--dark and body.body--light to scope rules to a specific mode:&#10;     body.body--light .cr-drawer { background: white; }&#10;     body.body--dark  .cr-header { background: black; }&#10;*/"
          />
        </div>
      </q-expansion-item>

      <footer class="cr-branding-actions">
        <q-btn
          flat
          no-caps
          color="negative"
          icon="restart_alt"
          label="Reset all"
          :disable="branding.isSaving"
          @click="onResetAll"
        />
        <q-space />
        <q-btn
          flat
          no-caps
          label="Reset to saved"
          :disable="!isDirty || branding.isSaving"
          @click="onReset"
        />
        <q-btn
          type="submit"
          color="primary"
          unelevated
          no-caps
          label="Save"
          :loading="branding.isSaving"
          :disable="!isDirty"
        />
      </footer>
    </q-form>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { useQuasar } from 'quasar';
import { useLocalStorage } from '@vueuse/core';
import {
  useBrandingStore,
  defaultBranding,
  CHROME_KEYS,
  PALETTE_KEYS,
  chromeCssName,
  quasarKey,
  type ChromeKey,
} from 'stores/branding';
import type { BrandingConfig } from 'stores/branding';
import ColorField from 'components/branding/ColorField.vue';
import FontPicker from 'components/branding/FontPicker.vue';
import LogoUploadField from 'components/branding/LogoUploadField.vue';
import PalettePicker from 'components/branding/PalettePicker.vue';
import { detectActivePreset, type PalettePreset } from 'src/css/palettes/registry';

type PaletteKey = 'primary' | 'secondary' | 'accent' | 'positive' | 'negative' | 'info' | 'warning';

interface FieldDef<K> {
  key: K;
  label: string;
}

const paletteFields: FieldDef<PaletteKey>[] = [
  { key: 'primary', label: 'Primary' },
  { key: 'secondary', label: 'Secondary' },
  { key: 'accent', label: 'Accent' },
  { key: 'positive', label: 'Positive' },
  { key: 'negative', label: 'Negative' },
  { key: 'info', label: 'Info' },
  { key: 'warning', label: 'Warning' },
];

const chromeLabels: Record<ChromeKey, string> = {
  bgPage: 'Page background',
  bgSurface: 'Surface',
  bgHeader: 'Header background',
  bgSidebar: 'Sidebar background',
  bgElevated: 'Elevated (popovers)',
  fgPrimary: 'Foreground (primary text)',
  fgSecondary: 'Foreground (secondary)',
  fgTertiary: 'Foreground (tertiary)',
  borderSubtle: 'Border (subtle)',
  borderDefault: 'Border (default)',
  borderStrong: 'Border (strong)',
};

const chromeFields: FieldDef<ChromeKey>[] = CHROME_KEYS.map((key) => ({
  key,
  label: chromeLabels[key],
}));

const $q = useQuasar();
const branding = useBrandingStore();

// The Palette and Chrome sections expose explicit Dark/Light tabs that are
// fully decoupled from the user's header theme toggle — editing the Light
// tab while viewing the page in dark mode just edits the data; the visible
// surfaces don't change. Persisted in localStorage so the admin's last
// choice sticks across reloads (with `dark` as the default since that's
// the most common starting point).
type EditMode = 'dark' | 'light';
const paletteEditMode = useLocalStorage<EditMode>('creuser.branding.paletteEditMode', 'dark');
const chromeEditMode = useLocalStorage<EditMode>('creuser.branding.chromeEditMode', 'dark');

// Per-user expand/collapse memory. localStorage means the state is scoped
// to the browser, not the user account — that's fine for v1, can promote
// to server-side `cr.user_preferences` later. Identity is opened by default
// since it's the most common edit; everything else starts collapsed and
// the user's choices stick.
const expanded = useLocalStorage<Record<string, boolean>>('creuser.branding.expanded', {
  identity: true,
  defaultMode: false,
  presets: false,
  palette: false,
  chrome: false,
  typography: false,
  customCss: false,
});

const draft = reactive<BrandingConfig>(cloneConfig(branding.config));

const fontFamilyField = ref(draft.fontFamily ?? '');
const fontFamilyMonoField = ref(draft.fontFamilyMono ?? '');
const loginTaglineField = ref(draft.loginTagline ?? '');
watch(loginTaglineField, (v) => {
  draft.loginTagline = v.trim() === '' ? null : v;
});

function onLogoChange(url: string | null) {
  draft.logoUrl = url;
  onLiveChange();
}

function onLogoLightChange(url: string | null) {
  draft.logoUrlLight = url;
  onLiveChange();
}

function onFontFamilyChange(v: string) {
  fontFamilyField.value = v;
  draft.fontFamily = v === '' ? null : v;
  onLiveChange();
}

function onFontFamilyMonoChange(v: string) {
  fontFamilyMonoField.value = v;
  draft.fontFamilyMono = v === '' ? null : v;
  onLiveChange();
}

const customCssField = ref(draft.customCss ?? '');
watch(customCssField, (v) => {
  draft.customCss = v === '' ? null : v;
});

/**
 * Build a baseline CSS snippet from the current draft — Quasar palette
 * (per mode), Creuser chrome (per mode), plus the dockview variables as a
 * commented block. Inserted into the Custom CSS editor as a starting
 * point so admins can tweak individual rules without having to look up
 * token names. The snippet only emits values the draft actually defines
 * (a preset's empty keys are skipped) — admins see exactly what they're
 * starting from.
 */
function buildBaselineSnippet(d: BrandingConfig): string {
  const palette = (d.palette ?? {}) as Record<string, string | null | undefined>;
  const paletteLight = (d.paletteLight ?? {}) as Record<string, string | null | undefined>;
  const chrome = (d.chrome ?? {}) as Record<string, string | null | undefined>;
  const chromeLight = (d.chromeLight ?? {}) as Record<string, string | null | undefined>;

  const palDarkLines = PALETTE_KEYS.map((k) =>
    palette[k] ? `  --q-${quasarKey(k)}: ${palette[k]};` : null,
  ).filter((s): s is string => s !== null);
  const palLightLines = PALETTE_KEYS.map((k) =>
    paletteLight[k] ? `  --q-${quasarKey(k)}: ${paletteLight[k]};` : null,
  ).filter((s): s is string => s !== null);
  const chromeDarkLines = CHROME_KEYS.map((k) =>
    chrome[k] ? `  ${chromeCssName(k)}: ${chrome[k]};` : null,
  ).filter((s): s is string => s !== null);
  const chromeLightLines = CHROME_KEYS.map((k) =>
    chromeLight[k] ? `  ${chromeCssName(k)}: ${chromeLight[k]};` : null,
  ).filter((s): s is string => s !== null);

  const out: string[] = [];
  out.push(
    '/* Baseline copied from current draft. Edit any line; the Custom CSS block wins over the structured fields above. */',
    '',
  );

  if (palDarkLines.length || chromeDarkLines.length) {
    out.push(':root {');
    if (palDarkLines.length) out.push('  /* Quasar palette (dark mode) */', ...palDarkLines);
    if (palDarkLines.length && chromeDarkLines.length) out.push('');
    if (chromeDarkLines.length) out.push('  /* Creuser chrome (dark mode) */', ...chromeDarkLines);
    out.push('}', '');
  }

  if (palLightLines.length || chromeLightLines.length) {
    out.push('.body--light {');
    if (palLightLines.length) out.push('  /* Quasar palette (light mode) */', ...palLightLines);
    if (palLightLines.length && chromeLightLines.length) out.push('');
    if (chromeLightLines.length)
      out.push('  /* Creuser chrome (light mode) */', ...chromeLightLines);
    out.push('}', '');
  }

  // Dockview variables are auto-derived in theme.scss from --cr-*/--q-*
  // tokens above. Surfacing them here as a commented block lets admins
  // override individual aspects (e.g. drag-over highlight) without having
  // to look up the variable name. Uncomment a line and edit to taste.
  out.push(
    '/* Dockview chrome — auto-derived from the tokens above. Uncomment any',
    '   line to override that specific aspect of the dock area. */',
    '/*',
    '.cr-dash-canvas .dv-shell {',
    '  --dv-tabs-and-actions-container-background-color: var(--cr-bg-surface);',
    '  --dv-activegroup-visiblepanel-tab-background-color: var(--cr-bg-page);',
    '  --dv-activegroup-hiddenpanel-tab-background-color: var(--cr-bg-surface);',
    '  --dv-activegroup-visiblepanel-tab-color: var(--cr-fg-primary);',
    '  --dv-activegroup-hiddenpanel-tab-color: var(--cr-fg-tertiary);',
    '  --dv-inactivegroup-visiblepanel-tab-background-color: var(--cr-bg-surface);',
    '  --dv-inactivegroup-hiddenpanel-tab-background-color: var(--cr-bg-surface);',
    '  --dv-inactivegroup-visiblepanel-tab-color: var(--cr-fg-secondary);',
    '  --dv-inactivegroup-hiddenpanel-tab-color: var(--cr-fg-tertiary);',
    '  --dv-group-view-background-color: var(--cr-bg-page);',
    '  --dv-separator-border: var(--cr-border-default);',
    '  --dv-tab-divider-color: var(--cr-border-subtle);',
    '  --dv-paneview-active-outline-color: var(--q-primary);',
    '  --dv-active-sash-color: var(--q-primary);',
    '  --dv-icon-hover-background-color: var(--cr-bg-hover);',
    '  --dv-drag-over-background-color: color-mix(in srgb, var(--q-primary), transparent 80%);',
    '  --dv-drag-over-border-color: var(--q-primary);',
    '  --dv-context-menu-background-color: var(--cr-bg-elevated);',
    '  --dv-context-menu-color: var(--cr-fg-primary);',
    '}',
    '*/',
  );

  return out.join('\n');
}

function onInsertBaseline() {
  const snippet = buildBaselineSnippet(draft);
  // If the textarea already has content, keep it and prepend the baseline
  // with a separator so the admin's prior work isn't clobbered. Otherwise
  // just drop the baseline in directly.
  customCssField.value = customCssField.value.trim()
    ? `${snippet}\n\n/* ── existing custom CSS below ── */\n${customCssField.value}`
    : snippet;
}

const isDirty = computed(() => JSON.stringify(draft) !== JSON.stringify(branding.config));

// Each mode's slot is matched against its own pool of presets independently.
// `Custom` shows in the picker when the user has tweaked colors away from
// any preset for that mode.
const activeDarkPresetId = computed(
  () => detectActivePreset(draft.palette, draft.chrome, 'dark')?.id ?? null,
);
const activeLightPresetId = computed(
  () => detectActivePreset(draft.paletteLight, draft.chromeLight, 'light')?.id ?? null,
);

function onPickDarkPreset(preset: PalettePreset) {
  // Writes only to the dark slot — paletteLight, chromeLight, and the
  // admin's "default mode for new users" setting are preserved so a dark
  // preset pick doesn't disturb the light side of the brand.
  draft.palette = JSON.parse(JSON.stringify(preset.palette)) as typeof draft.palette;
  draft.chrome = JSON.parse(JSON.stringify(preset.chrome)) as typeof draft.chrome;
  onLiveChange();
}

function onPickLightPreset(preset: PalettePreset) {
  // Light presets ship their palette in `palette` and their chrome in
  // `chromeLight` (the `chrome` field is empty `{}` for light presets),
  // so we pull from each preset field into the appropriate config slot.
  draft.paletteLight = JSON.parse(JSON.stringify(preset.palette)) as typeof draft.paletteLight;
  draft.chromeLight = JSON.parse(JSON.stringify(preset.chromeLight)) as typeof draft.chromeLight;
  onLiveChange();
}

function paletteValue(key: PaletteKey): string {
  const slot = paletteEditMode.value === 'light' ? draft.paletteLight : draft.palette;
  return slot?.[key] ?? '';
}

function setPalette(key: PaletteKey, value: string) {
  const isLight = paletteEditMode.value === 'light';
  if (isLight) {
    if (!draft.paletteLight) draft.paletteLight = {};
    draft.paletteLight[key] = value === '' ? null : value;
  } else {
    if (!draft.palette) draft.palette = {};
    draft.palette[key] = value === '' ? null : value;
  }
  onLiveChange();
}

function chromeValue(key: ChromeKey): string {
  const slot = chromeEditMode.value === 'light' ? draft.chromeLight : draft.chrome;
  return slot?.[key] ?? '';
}

function setChrome(key: ChromeKey, value: string) {
  const isLight = chromeEditMode.value === 'light';
  if (isLight) {
    if (!draft.chromeLight) draft.chromeLight = {};
    draft.chromeLight[key] = value === '' ? null : value;
  } else {
    if (!draft.chrome) draft.chrome = {};
    draft.chrome[key] = value === '' ? null : value;
  }
  onLiveChange();
}

function onLiveChange() {
  // `preview()` does visual side effects only — does NOT update
  // branding.config, so the dirty-tracking that drives the Save button
  // stays accurate. Custom CSS is excluded from the live preview — typing
  // CSS in real time can break the page (e.g. mid-keystroke
  // `* { display: none }` hides the editor itself). It applies only when
  // the user clicks Save.
  const live = cloneConfig(draft);
  live.customCss = branding.config.customCss;
  branding.preview(live);
}

async function onSave() {
  if (!draft.productName.trim()) return;
  try {
    await branding.save(cloneConfig(draft));
    syncFieldsFromConfig();
    $q.notify({ type: 'positive', message: 'Branding saved.', position: 'top' });
  } catch (e) {
    const msg = e instanceof Error ? e.message : 'Failed to save branding.';
    $q.notify({ type: 'negative', message: msg, position: 'top' });
  }
}

function onReset() {
  syncFieldsFromConfig();
  branding.preview(cloneConfig(branding.config));
}

/**
 * Wipe the draft back to the bundled defaults — every field returns to the
 * baked-in seed values (Creuser logo, sage palette, neutral chrome, system
 * fonts, no custom CSS). Confirmation dialog because this is destructive
 * and easy to misclick. Save still required to persist.
 */
function onResetAll() {
  $q.dialog({
    title: 'Reset all branding?',
    message:
      'This reverts every branding setting (logos, palette + chrome for both modes, ' +
      'typography, custom CSS) back to the bundled defaults. Click Save afterward to persist ' +
      'for everyone.',
    ok: { label: 'Reset all', color: 'negative', unelevated: true, noCaps: true },
    cancel: { flat: true, noCaps: true },
    persistent: true,
  }).onOk(() => {
    Object.assign(draft, cloneConfig(defaultBranding));
    fontFamilyField.value = draft.fontFamily ?? '';
    fontFamilyMonoField.value = draft.fontFamilyMono ?? '';
    loginTaglineField.value = draft.loginTagline ?? '';
    customCssField.value = draft.customCss ?? '';
    onLiveChange();
  });
}

function syncFieldsFromConfig() {
  Object.assign(draft, cloneConfig(branding.config));
  fontFamilyField.value = draft.fontFamily ?? '';
  fontFamilyMonoField.value = draft.fontFamilyMono ?? '';
  loginTaglineField.value = draft.loginTagline ?? '';
  customCssField.value = draft.customCss ?? '';
}

function cloneConfig(c: BrandingConfig): BrandingConfig {
  return JSON.parse(JSON.stringify(c)) as BrandingConfig;
}

const route = useRoute();

/**
 * Honor `?expand=<sectionKey>` from the URL — fed by the assistant's
 * navigation links. Expands the requested section and scrolls it into view.
 */
function honorExpandQuery() {
  const key = route.query.expand;
  if (typeof key !== 'string' || !(key in expanded.value)) return;
  expanded.value = { ...expanded.value, [key]: true };
  void nextTick(() => {
    const el = document.querySelector(`[data-section-key="${key}"]`);
    el?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  });
}

onMounted(honorExpandQuery);
</script>

<style lang="scss" scoped>
.cr-branding-page {
  padding: 32px 40px 96px;
  max-width: 880px;
}

.cr-branding-header {
  margin-bottom: 24px;
}

.cr-branding-subhead {
  margin: 8px 0 0;
  font-size: 13px;
  color: var(--cr-fg-secondary);
}

.cr-branding-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

// Each section is a q-expansion-item. Style the header to read as a section
// title and the body to keep the existing field layout.
.cr-branding-section {
  border: 1px solid var(--cr-border-subtle);
  border-radius: 6px;
  background: var(--cr-bg-surface);
  overflow: hidden;
}

:deep(.cr-branding-section-header) {
  padding: 10px 12px;
  min-height: 0;

  .q-item__label {
    font-size: 13px;
    font-weight: 600;
    color: var(--cr-fg-primary);
    line-height: 1.3;
  }

  .q-item__label--caption {
    font-size: 11px;
    color: var(--cr-fg-tertiary);
    margin-top: 2px;
  }

  .q-icon {
    color: var(--cr-fg-tertiary);
  }
}

.cr-branding-section-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 4px 16px 20px;
  border-top: 1px solid var(--cr-border-subtle);
}

.cr-branding-section-hint {
  font-size: 12px;
  color: var(--cr-fg-secondary);
  margin: 0 0 4px;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}

.cr-branding-input {
  max-width: 520px;
}

.cr-branding-toggle {
  align-self: flex-start;
}

.cr-branding-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 12px;
}

// Two side-by-side logo upload fields (dark + light). Stacks on narrow widths.
.cr-branding-logos {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 16px;
}

.cr-branding-logo-slot {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.cr-branding-logo-slot-label {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.04em;
  color: var(--cr-fg-secondary);
  text-transform: uppercase;
}

.cr-branding-logo-slot-hint {
  margin: 0;
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  line-height: 1.4;
}

// Two side-by-side preset pickers (dark + light) for the Base theme presets
// section. Stacks on narrow widths.
.cr-branding-presets-pair {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 20px;
}

.cr-branding-preset-slot {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.cr-branding-preset-slot-title {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--cr-fg-secondary);
  margin: 0;
}

// Dark/Light tabs above the Palette and Chrome editing grids — explicit
// per-mode editing target, decoupled from the user's header theme toggle.
.cr-branding-mode-tabs {
  align-self: flex-start;
  min-height: 28px;
  border-bottom: 1px solid var(--cr-border-subtle);

  :deep(.q-tab) {
    min-height: 28px;
    padding: 0 12px;
    font-size: 12px;
    font-weight: 500;
  }
}

.cr-branding-css-actions {
  display: flex;
  gap: 6px;
}

.cr-branding-help {
  background: var(--cr-bg-elevated);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 4px;
  padding: 8px 12px;
  font-size: 12px;
  color: var(--cr-fg-secondary);

  summary {
    cursor: pointer;
    font-weight: 500;
    color: var(--cr-fg-primary);
  }

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-surface);
    padding: 1px 4px;
    border-radius: 3px;
  }

  pre {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-surface);
    padding: 8px;
    margin: 4px 0;
    border-radius: 3px;
    overflow-x: auto;
    white-space: pre-wrap;
  }
}

.cr-branding-help-body {
  margin-top: 8px;

  p {
    margin: 8px 0 4px;
  }

  ul {
    margin: 0;
    padding-left: 20px;
  }
}

.cr-branding-css {
  font-family: var(--cr-font-family-mono);
}

:deep(.cr-branding-css-input) {
  font-family: var(--cr-font-family-mono);
  font-size: 12px;
  min-height: 120px;
}

.cr-branding-actions {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
  margin-top: 16px;
  border-top: 1px solid var(--cr-border-subtle);
  position: sticky;
  bottom: 0;
  background: var(--cr-bg-page);
}
</style>
