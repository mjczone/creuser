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

  // Auth guard: any route except those with `meta.public` requires
  // an authenticated session. Anonymous users get bounced to /login.
  // Already-authenticated users hitting /login get redirected home.
  Router.beforeEach((to) => {
    const auth = useAuthStore();
    const isPublic = to.matched.some((r) => r.meta.public === true);

    if (!auth.isAuthenticated && !isPublic) {
      return { name: 'login', query: { redirect: to.fullPath } };
    }
    if (auth.isAuthenticated && to.name === 'login') {
      return { path: '/' };
    }
    return true;
  });

  return Router;
});
