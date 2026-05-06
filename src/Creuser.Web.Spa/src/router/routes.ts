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
      // Workspace-scoped routes share the same MainLayout as platform routes;
      // the layout reads `meta.workspaceScoped` to decide whether to render
      // the workspace icon-bar variant. Nested route metadata bubbles up via
      // `route.matched`, so children inherit `workspaceScoped: true` for
      // free.
      {
        path: 'w/:workspaceSlug',
        name: 'workspace-home',
        component: () => import('pages/workspace/WorkspaceHomePage.vue'),
        meta: {
          title: 'Home',
          description: 'Workspace overview.',
          workspaceScoped: true,
        },
      },
      {
        path: 'w/:workspaceSlug/d/:dashboardSlug',
        name: 'workspace-dashboard',
        component: () => import('pages/workspace/DashboardPage.vue'),
        meta: { title: 'Dashboard', workspaceScoped: true },
      },
      {
        path: 'w/:workspaceSlug/g/:groupSlug',
        name: 'workspace-dashboard-group',
        component: () => import('pages/workspace/WorkspaceGroupPage.vue'),
        meta: { title: 'Group', workspaceScoped: true },
      },
      {
        path: 'w/:workspaceSlug/d/:dashboardSlug/edit',
        name: 'workspace-dashboard-edit',
        component: () => import('pages/workspace/DashboardPage.vue'),
        meta: {
          title: 'Edit dashboard',
          workspaceScoped: true,
          requiresWorkspaceEditor: true,
        },
      },
      {
        path: 'w/:workspaceSlug/settings',
        component: () => import('pages/workspace/WorkspaceSettingsPage.vue'),
        meta: {
          title: 'Workspace settings',
          workspaceScoped: true,
          requiresWorkspaceEditor: true,
        },
        children: [
          {
            path: '',
            redirect: (to) => `/w/${String(to.params.workspaceSlug)}/settings/general`,
          },
          {
            path: 'general',
            name: 'workspace-settings-general',
            component: () => import('pages/workspace/WorkspaceSettingsGeneralPage.vue'),
            meta: { title: 'General', workspaceScoped: true, requiresWorkspaceEditor: true },
          },
          {
            path: 'plugins',
            name: 'workspace-settings-plugins',
            component: () => import('pages/workspace/WorkspaceSettingsPluginsPage.vue'),
            meta: { title: 'Plugins', workspaceScoped: true, requiresWorkspaceEditor: true },
          },
          {
            path: 'jobs',
            name: 'workspace-settings-jobs',
            component: () => import('pages/workspace/WorkspaceSettingsJobsPage.vue'),
            meta: { title: 'Jobs', workspaceScoped: true, requiresWorkspaceEditor: true },
          },
          {
            path: 'schedules',
            name: 'workspace-settings-schedules',
            component: () => import('pages/workspace/WorkspaceSettingsSchedulesPage.vue'),
            meta: { title: 'Schedules', workspaceScoped: true, requiresWorkspaceEditor: true },
          },
          {
            path: 'conventions',
            name: 'workspace-settings-conventions',
            component: () => import('pages/workspace/WorkspaceSettingsConventionsPage.vue'),
            meta: { title: 'Conventions', workspaceScoped: true, requiresWorkspaceEditor: true },
          },
          {
            path: 'dashboards',
            name: 'workspace-settings-dashboards',
            component: () => import('pages/workspace/WorkspaceSettingsDashboardsPage.vue'),
            meta: { title: 'Dashboards', workspaceScoped: true, requiresWorkspaceEditor: true },
          },
        ],
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
