import { defineRouter } from '#q-app/wrappers';
import {
  createMemoryHistory,
  createRouter,
  createWebHashHistory,
  createWebHistory,
} from 'vue-router';
import routes from './routes';
import { useAuthStore } from 'stores/auth';
import { useWorkspaceStore } from 'stores/workspace';

export default defineRouter((/* { store, ssrContext } */) => {
  const createHistory = process.env.SERVER
    ? createMemoryHistory
    : process.env.VUE_ROUTER_MODE === 'history'
      ? createWebHistory
      : createWebHashHistory;

  const Router = createRouter({
    scrollBehavior: () => ({ left: 0, top: 0 }),
    routes,

    // Leave this as is and make changes in quasar.conf.js instead!
    // quasar.conf.js -> build -> vueRouterMode
    // quasar.conf.js -> build -> publicPath
    history: createHistory(process.env.VUE_ROUTER_BASE),
  });

  // Auth guard:
  //   - Routes with `meta.public` are open (login, etc.).
  //   - All others require an authenticated session; anonymous users are
  //     bounced to /login with a redirect query.
  //   - Routes with `meta.requiresAdmin` additionally require the Admin role;
  //     non-admins are sent home rather than to a 403 page.
  //   - Routes with `meta.workspaceScoped` require the workspace slug to
  //     resolve (admins see all; non-admins fall through until
  //     cr.workspace_members lands).
  //   - Already-authenticated users hitting /login go home.
  Router.beforeEach(async (to) => {
    const auth = useAuthStore();
    const isPublic = to.matched.some((r) => r.meta.public === true);
    const requiresAdmin = to.matched.some((r) => r.meta.requiresAdmin === true);
    const isWorkspaceScoped = to.matched.some((r) => r.meta.workspaceScoped === true);
    const requiresWorkspaceEditor = to.matched.some((r) => r.meta.requiresWorkspaceEditor === true);

    if (!auth.isAuthenticated && !isPublic) {
      return { name: 'login', query: { redirect: to.fullPath } };
    }
    if (auth.isAuthenticated && to.name === 'login') {
      return { path: '/' };
    }
    if (requiresAdmin && !auth.isAdmin) {
      return { path: '/' };
    }
    if (isWorkspaceScoped) {
      const slug = typeof to.params.workspaceSlug === 'string' ? to.params.workspaceSlug : null;
      if (!slug) {
        return { path: '/' };
      }
      // Pre-load so the page can read from the cache without flicker.
      // ensureLoaded returns null on 403/404 — both are "no access".
      const ws = await useWorkspaceStore().ensureLoaded(slug);
      if (!ws) {
        return { path: '/' };
      }
      // Workspace-editor gate: today only platform admins can hit
      // /w/:slug/settings/* because cr.workspace_members doesn't exist yet.
      // When membership lands, this becomes (ws.member?.role === 'Editor').
      if (requiresWorkspaceEditor && !auth.isAdmin) {
        return { path: `/w/${slug}` };
      }
    }
    return true;
  });

  return Router;
});
