import type { RouteRecordRaw } from 'vue-router';

const placeholder = () => import('pages/PlaceholderPage.vue');

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
        name: 'dashboard',
        component: placeholder,
        meta: { title: 'Dashboard', description: 'Operator overview and live run status.' },
      },
      {
        path: 'workspaces',
        name: 'workspaces',
        component: placeholder,
        meta: {
          title: 'Workspaces',
          description: 'Configured repository connections (git, S3, local).',
        },
      },
      {
        path: 'workflows',
        name: 'workflows',
        component: placeholder,
        meta: {
          title: 'Workflows',
          description: 'Workflow definitions — sagas with static and agentic steps.',
        },
      },
      {
        path: 'runs',
        name: 'runs',
        component: placeholder,
        meta: { title: 'Runs', description: 'Workflow execution history with full audit trail.' },
      },
      {
        path: 'scripts',
        name: 'scripts',
        component: placeholder,
        meta: { title: 'Scripts', description: 'Job scripts library — frontmatter + body.' },
      },
      {
        path: 'agents',
        name: 'agents',
        component: placeholder,
        meta: {
          title: 'Agents',
          description: 'Agent traces, providers, and tool registries.',
        },
      },
      {
        path: 'plugins',
        name: 'plugins',
        component: placeholder,
        meta: { title: 'Plugins', description: 'Installed plugins and their declared extensions.' },
      },
      {
        path: 'settings',
        name: 'settings',
        component: placeholder,
        meta: { title: 'Settings', description: 'Branding, secrets, and platform configuration.' },
      },
      {
        path: 'admin/users',
        name: 'admin-users',
        component: () => import('pages/admin/UsersPage.vue'),
        meta: { title: 'Users', description: 'User accounts, sessions, and roles.' },
      },
    ],
  },

  {
    path: '/:catchAll(.*)*',
    component: () => import('pages/ErrorNotFound.vue'),
  },
];

export default routes;
