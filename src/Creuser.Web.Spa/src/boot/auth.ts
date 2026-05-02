import { defineBoot } from '#q-app/wrappers';
import { useAuthStore } from 'stores/auth';

// Loads /api/auth/me before the app mounts so route guards can decide
// where to send the user without flicker.
export default defineBoot(async () => {
  const auth = useAuthStore();
  await auth.load();
});
