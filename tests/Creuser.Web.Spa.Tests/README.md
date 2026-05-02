# Creuser.Web.Spa.Tests

Vitest test suite for `src/Creuser.Web.Spa`.

This is a standalone npm project. It imports from the SPA's source via the
same bare-specifier path aliases the SPA itself uses (`src/...`,
`stores/...`, `components/...`, etc.), resolved through `vitest.config.ts`
and `tsconfig.json`.

## Install

```bash
cd tests/Creuser.Web.Spa.Tests
npm install
```

## Run

```bash
npm test               # one-shot run
npm run test:watch     # watch mode
npm run test:ui        # Vitest UI
npm run coverage       # v8 coverage of SPA src
npm run typecheck      # vue-tsc --noEmit
```

## Layout

```
tests/Creuser.Web.Spa.Tests/
├── package.json
├── tsconfig.json
├── vitest.config.ts
├── setup.ts                     # Registers Quasar plugin globally for @vue/test-utils
└── src/
    ├── smoke.test.ts            # Sanity check
    └── stores/
        └── example-store.test.ts # Pinia store test against SPA's example-store
```

## Adding tests

Mirror the SPA's `src/` tree under this project's `src/`. Use the same
import paths the SPA uses (`stores/foo`, `components/bar`, etc.) — aliases
are wired in `vitest.config.ts`.

For component tests, mount with `@vue/test-utils`:

```ts
import { mount } from '@vue/test-utils';
import MyComponent from 'components/MyComponent.vue';

it('renders', () => {
  const wrapper = mount(MyComponent);
  expect(wrapper.text()).toContain('hello');
});
```

For Pinia stores in components, use `@pinia/testing`'s `createTestingPinia()`.
