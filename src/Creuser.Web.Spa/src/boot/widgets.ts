import { boot } from 'quasar/wrappers';
import { registerWidget } from 'src/widgets/registry';
import PlaceholderWidget from 'src/widgets/PlaceholderWidget.vue';

/**
 * Registers the v1 widget set against the global widget registry. Each
 * built-in widget self-registers here at boot time so DashboardPage can
 * resolve `widgetType` -> component without per-page imports.
 *
 * Today every type points at `PlaceholderWidget` to prove the dockview-vue
 * + registry loop is wired correctly. Each follow-up commit replaces one
 * `component: PlaceholderWidget` with the real implementation:
 *  - RunsList            (run history table, filterable)
 *  - RunInspector        (single-run detail w/ steps + logs)
 *  - JobScriptList       (workspace's job scripts, run buttons)
 *  - JobScriptEditor     (Monaco YAML+markdown editor)
 *  - ScheduleList        (cron + sync schedules with next-due chips)
 *  - ProjectionReport    (last sync's report)
 *  - Markdown            (workspace-authored markdown tile)
 *  - WorkspaceMembers    (read-only member roster)
 */
export default boot(() => {
  registerWidget({
    type: 'RunsList',
    name: 'Runs',
    description: 'Recent runs across this workspace.',
    icon: 'play_circle',
    component: PlaceholderWidget,
    propsSchema: {
      type: 'object',
      properties: {
        limit: { type: 'integer', default: 25, minimum: 1, maximum: 200 },
        statusFilter: {
          type: 'string',
          enum: ['all', 'succeeded', 'failed', 'running'],
          default: 'all',
        },
      },
    },
    defaultProps: { limit: 25, statusFilter: 'all' },
    defaultDockview: { minWidth: 280, preferredPosition: 'right' },
  });

  registerWidget({
    type: 'RunInspector',
    name: 'Run Inspector',
    description: 'Single-run detail with step transitions + logs.',
    icon: 'troubleshoot',
    component: PlaceholderWidget,
    propsSchema: {
      type: 'object',
      properties: {
        runId: { type: 'string', format: 'uuid' },
      },
    },
    defaultProps: {},
    defaultDockview: { minWidth: 360, preferredPosition: 'right' },
  });

  registerWidget({
    type: 'JobScriptList',
    name: 'Scripts',
    description: 'Job scripts in this workspace, with run buttons.',
    icon: 'description',
    component: PlaceholderWidget,
    propsSchema: {
      type: 'object',
      properties: {
        limit: { type: 'integer', default: 50, minimum: 1, maximum: 500 },
      },
    },
    defaultProps: { limit: 50 },
    defaultDockview: { minWidth: 280, preferredPosition: 'tab' },
  });

  registerWidget({
    type: 'JobScriptEditor',
    name: 'Script Editor',
    description: 'Monaco-based YAML + markdown editor for one job script.',
    icon: 'edit_note',
    component: PlaceholderWidget,
    propsSchema: {
      type: 'object',
      properties: {
        scriptSlug: { type: 'string' },
      },
    },
    defaultProps: {},
    defaultDockview: { minWidth: 480, minHeight: 320, preferredPosition: 'tab' },
  });

  registerWidget({
    type: 'ScheduleList',
    name: 'Schedules',
    description: 'Cron + post-sync schedules with next-due chips.',
    icon: 'schedule',
    component: PlaceholderWidget,
    propsSchema: {
      type: 'object',
      properties: {
        limit: { type: 'integer', default: 50, minimum: 1, maximum: 500 },
      },
    },
    defaultProps: { limit: 50 },
    defaultDockview: { minWidth: 280, preferredPosition: 'below' },
  });

  registerWidget({
    type: 'ProjectionReport',
    name: 'Projection Report',
    description: 'Last sync\'s entities-by-kind, unresolved refs, schema failures.',
    icon: 'account_tree',
    component: PlaceholderWidget,
    propsSchema: { type: 'object', properties: {} },
    defaultProps: {},
    defaultDockview: { minWidth: 320, preferredPosition: 'tab' },
  });

  registerWidget({
    type: 'Markdown',
    name: 'Notes',
    description: 'Workspace-authored markdown tile (READMEs, free-form notes).',
    icon: 'sticky_note_2',
    component: PlaceholderWidget,
    propsSchema: {
      type: 'object',
      properties: {
        source: { type: 'string', description: 'Markdown source.' },
      },
    },
    defaultProps: { source: '' },
    defaultDockview: { minWidth: 240, preferredPosition: 'tab' },
  });

  registerWidget({
    type: 'WorkspaceMembers',
    name: 'Members',
    description: 'Workspace members and their roles (read-only).',
    icon: 'group',
    component: PlaceholderWidget,
    propsSchema: { type: 'object', properties: {} },
    defaultProps: {},
    defaultDockview: { minWidth: 240, preferredPosition: 'tab' },
  });
});
