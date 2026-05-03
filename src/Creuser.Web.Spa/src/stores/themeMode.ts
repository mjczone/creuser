import { defineStore, acceptHMRUpdate } from 'pinia';
import { computed, ref, watch } from 'vue';
import { Dark } from 'quasar';

/**
 * User-controlled light/dark preference. Three values:
 *   - `dark` / `light` — explicit user pick, survives across sessions.
 *   - `auto` — follow `prefers-color-scheme`; falls back to the admin's
 *     `BrandingConfig.mode` if the system has no preference.
 *
 * Storage is currently localStorage (per-browser). A future iteration may
 * promote this to a `cr.user_preferences` table so the choice follows the
 * user across devices.
 */

export type ThemeMode = 'dark' | 'light' | 'auto';
export type EffectiveMode = 'dark' | 'light';

const STORAGE_KEY = 'creuser.theme-mode';

function readPreference(): ThemeMode {
  if (typeof localStorage === 'undefined') return 'auto';
  const v = localStorage.getItem(STORAGE_KEY);
  return v === 'dark' || v === 'light' || v === 'auto' ? v : 'auto';
}

function readSystem(): EffectiveMode | null {
  if (typeof window === 'undefined' || !window.matchMedia) return null;
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export const useThemeModeStore = defineStore('themeMode', () => {
  const preference = ref<ThemeMode>(readPreference());
  const system = ref<EffectiveMode | null>(readSystem());

  // Set by the branding store once `BrandingConfig.mode` is known. Default
  // matches the build-time Quasar dark default so first paint is consistent.
  const adminDefault = ref<EffectiveMode>('dark');

  const effective = computed<EffectiveMode>(() => {
    if (preference.value === 'dark') return 'dark';
    if (preference.value === 'light') return 'light';
    return system.value ?? adminDefault.value;
  });

  function setPreference(next: ThemeMode) {
    preference.value = next;
    if (typeof localStorage !== 'undefined') localStorage.setItem(STORAGE_KEY, next);
  }

  function setAdminDefault(mode: EffectiveMode) {
    adminDefault.value = mode;
  }

  // Drive Quasar's body class from the effective mode. Quasar's components
  // (q-input, q-banner, q-color, q-table) read this to style themselves.
  // Our `.body--light` CSS-variable overrides in theme.scss read it too —
  // one toggle, two consumers.
  watch(
    effective,
    (mode) => {
      Dark.set(mode === 'dark');
    },
    { immediate: true },
  );

  // React to OS-level light/dark switches when the user's preference is auto.
  if (typeof window !== 'undefined' && window.matchMedia) {
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    mq.addEventListener('change', (e) => {
      system.value = e.matches ? 'dark' : 'light';
    });
  }

  return {
    preference,
    effective,
    adminDefault,
    setPreference,
    setAdminDefault,
  };
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useThemeModeStore, import.meta.hot));
}
