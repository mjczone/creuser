import { defineBoot } from '#q-app/wrappers';
import { useBrandingStore } from 'stores/branding';

// Loads branding config before the app mounts so the first paint is
// already themed — avoids a flash of neutral chrome before runtime
// overrides apply.
export default defineBoot(async () => {
  const branding = useBrandingStore();
  await branding.load();
});
