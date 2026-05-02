import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';
import vue from '@vitejs/plugin-vue';

const spaSrc = fileURLToPath(new URL('../../src/Creuser.Web.Spa/src', import.meta.url));

export default defineConfig({
  plugins: [vue()],
  resolve: {
    dedupe: ['vue', 'pinia', 'quasar', '@vue/test-utils'],
    alias: [
      { find: /^src$/, replacement: spaSrc },
      { find: /^src\/(.*)$/, replacement: `${spaSrc}/$1` },
      { find: /^stores$/, replacement: `${spaSrc}/stores` },
      { find: /^stores\/(.*)$/, replacement: `${spaSrc}/stores/$1` },
      { find: /^components$/, replacement: `${spaSrc}/components` },
      { find: /^components\/(.*)$/, replacement: `${spaSrc}/components/$1` },
      { find: /^layouts$/, replacement: `${spaSrc}/layouts` },
      { find: /^layouts\/(.*)$/, replacement: `${spaSrc}/layouts/$1` },
      { find: /^pages$/, replacement: `${spaSrc}/pages` },
      { find: /^pages\/(.*)$/, replacement: `${spaSrc}/pages/$1` },
      { find: /^assets$/, replacement: `${spaSrc}/assets` },
      { find: /^assets\/(.*)$/, replacement: `${spaSrc}/assets/$1` },
      { find: /^boot$/, replacement: `${spaSrc}/boot` },
      { find: /^boot\/(.*)$/, replacement: `${spaSrc}/boot/$1` },
    ],
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./setup.ts'],
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html', 'lcov'],
      include: [`${spaSrc}/**/*.{ts,vue}`],
      exclude: [`${spaSrc}/**/*.d.ts`, `${spaSrc}/boot/**`, `${spaSrc}/router/**`],
    },
  },
});
