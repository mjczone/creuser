import { defineConfig } from '@hey-api/openapi-ts';

// The .NET project emits ../Creuser.Web/Creuser.Web.json on every build
// (Microsoft.Extensions.ApiDescription.Server). This config reads that file
// and writes a typed fetch client, schemas, and SDK functions into src/api/.
export default defineConfig({
  input: '../Creuser.Web/Creuser.Web.json',
  output: {
    path: 'src/api',
    postProcess: ['prettier'],
  },
  plugins: [
    '@hey-api/client-fetch',
    '@hey-api/schemas',
    '@hey-api/typescript',
    {
      name: '@hey-api/sdk',
      operations: { strategy: 'byTags' },
    },
  ],
});
