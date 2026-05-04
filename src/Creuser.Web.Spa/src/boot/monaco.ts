import { boot } from 'quasar/wrappers';
import { install as VueMonacoEditorPlugin } from '@guolao/vue-monaco-editor';

/**
 * Register `@guolao/vue-monaco-editor` globally so widgets can use
 * `<vue-monaco-editor>` without per-component plugin install. Monaco
 * itself is loaded lazily from a CDN by the plugin's loader — this
 * keeps the SPA bundle small but requires network access on first
 * editor mount.
 *
 * Air-gapped deployments can pin a self-hosted vs path here (e.g.
 * `/assets/monaco/vs` shipped from the platform's wwwroot) once that
 * deployment shape ships. v0.1 ships the CDN default (jsdelivr) and
 * tests verify the editor mounts.
 */
export default boot(({ app }) => {
  app.use(VueMonacoEditorPlugin, {
    paths: {
      vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.55.0/min/vs',
    },
  });
});
