import type { RouteRecordRaw } from 'vue-router';

const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('pages/LoginPage.vue'),
    meta: { public: true },
  },
  {
    path: '/',
    component: () => import('layouts/MainLayout.vue'),
    meta: { requiresAuth: true },
    children: [
      {
        path: '',
        name: 'home',
        component: () => import('pages/HomePage.vue'),
        meta: {
          title: 'Home',
          description: 'Workspace picker — pick a workspace to enter.',
        },
      },
      {
        path: 'settings',
        component: () => import('pages/settings/SettingsPage.vue'),
        meta: {
          title: 'Settings',
          description: 'Branding, users, environment, and workspace configuration.',
          requiresAdmin: true,
        },
        children: [
          { path: '', redirect: '/settings/branding' },
          {
            path: 'branding',
            name: 'settings-branding',
            component: () => import('pages/settings/BrandingPage.vue'),
            meta: { title: 'Branding', description: 'Logo, product name, color palette.' },
          },
          {
            path: 'users',
            name: 'settings-users',
            component: () => import('pages/settings/UsersPage.vue'),
            meta: { title: 'Users', description: 'User accounts, sessions, and roles.' },
          },
          {
            path: 'environment',
            name: 'settings-environment',
            component: () => import('pages/settings/EnvironmentPage.vue'),
            meta: {
              title: 'Environment',
              description: 'SMTP, AI provider keys, base URL.',
            },
          },
          {
            path: 'workspaces',
            name: 'settings-workspaces',
            component: () => import('pages/settings/WorkspacesPage.vue'),
            meta: {
              title: 'Workspaces',
              description: 'Connected git and S3 sources.',
            },
          },
        ],
      },
    ],
  },

  {
    path: '/:catchAll(.*)*',
    component: () => import('pages/ErrorNotFound.vue'),
  },
];

export default routes;
