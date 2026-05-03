import { defineRouter } from '#q-app/wrappers';
import {
  createMemoryHistory,
  createRouter,
  createWebHashHistory,
  createWebHistory,
} from 'vue-router';
import routes from './routes';
import { useAuthStore } from 'stores/auth';

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
  //   - Already-authenticated users hitting /login go home.
  Router.beforeEach((to) => {
    const auth = useAuthStore();
    const isPublic = to.matched.some((r) => r.meta.public === true);
    const requiresAdmin = to.matched.some((r) => r.meta.requiresAdmin === true);

    if (!auth.isAuthenticated && !isPublic) {
      return { name: 'login', query: { redirect: to.fullPath } };
    }
    if (auth.isAuthenticated && to.name === 'login') {
      return { path: '/' };
    }
    if (requiresAdmin && !auth.isAdmin) {
      return { path: '/' };
    }
    return true;
  });

  return Router;
});
