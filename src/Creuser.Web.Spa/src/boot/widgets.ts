import { boot } from 'quasar/wrappers';
import { registerWidget } from 'src/widgets/registry';
import PlaceholderWidget from 'src/widgets/PlaceholderWidget.vue';
import RunsList from 'src/widgets/RunsList.vue';
import RunInspector from 'src/widgets/RunInspector.vue';
import ScheduleList from 'src/widgets/ScheduleList.vue';
import JobScriptList from 'src/widgets/JobScriptList.vue';
import Markdown from 'src/widgets/Markdown.vue';
import ProjectionReport from 'src/widgets/ProjectionReport.vue';

/**
 * Registers the v1 widget set against the global widget registry. Each
 * built-in widget self-registers here at boot time so DashboardPage can
 * resolve `widgetType` -> component without per-page imports.
 *
 * Real implementations land per-commit. Status:
 *  - RunsList            (real)
 *  - RunInspector        (real)
 *  - JobScriptList       (real)
 *  - ScheduleList        (real)
 *  - Markdown            (real)
 *  - ProjectionReport    (real)
 *  - JobScriptEditor     (placeholder — needs Monaco)
 *  - WorkspaceMembers    (placeholder — member API surface needed first)
 */
export default boot(() => {
  registerWidget({
    type: 'RunsList',
    name: 'Runs',
    description: 'Recent runs across this workspace.',
    icon: 'play_circle',
    component: RunsList,
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
    component: RunInspector,
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
    component: JobScriptList,
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
    component: ScheduleList,
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
    description: 'Conventions, load errors, on-demand projection sync results.',
    icon: 'account_tree',
    component: ProjectionReport,
    propsSchema: { type: 'object', properties: {} },
    defaultProps: {},
    defaultDockview: { minWidth: 320, preferredPosition: 'tab' },
  });

  registerWidget({
    type: 'Markdown',
    name: 'Notes',
    description: 'Workspace-authored markdown tile (READMEs, free-form notes).',
    icon: 'sticky_note_2',
    component: Markdown,
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
