declare namespace NodeJS {
  interface ProcessEnv {
    NODE_ENV: string;
    VUE_ROUTER_MODE: 'hash' | 'history' | 'abstract' | undefined;
    VUE_ROUTER_BASE: string | undefined;
  }
}

// Fontsource packages are CSS-only — they don't ship type declarations.
// Vite handles the imports as side-effecting CSS modules; we just need
// to satisfy TypeScript's module resolver.
declare module '@fontsource-variable/*';
