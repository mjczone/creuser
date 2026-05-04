<template>
  <div class="cr-w-md">
    <div v-if="rendered" class="cr-w-md-body" v-html="rendered" />
    <div v-else class="cr-w-md-empty">
      <q-icon name="sticky_note_2" size="32px" class="cr-w-md-icon" />
      <p>Empty markdown widget. Edit the widget to add content.</p>
    </div>
  </div>
</template>

<script setup lang="ts">
/**
 * MarkdownWidget — renders the `source` prop as HTML using marked. v1
 * trusts the source because dashboards are workspace-admin-authored
 * (single-tenant threat model — admin-against-self isn't a security
 * boundary). Future v0.2 may add DOMPurify when JsWidget arrives and
 * untrusted JS execution gets sandboxed; markdown HTML is the lighter
 * analog.
 */
import { computed } from 'vue';
import { marked } from 'marked';

defineOptions({ name: 'MarkdownWidget' });

const props = defineProps<{
  widgetType: string;
  propsData: { source?: string };
}>();

const rendered = computed<string>(() => {
  const src = props.propsData?.source ?? '';
  if (!src.trim()) return '';
  // marked.parse with async:false returns a sync string.
  return marked.parse(src, { async: false });
});
</script>

<style lang="scss" scoped>
.cr-w-md {
  height: 100%;
  display: flex;
  flex-direction: column;
  background: var(--cr-bg-elevated, #1a1a1d);
  overflow: auto;
}

.cr-w-md-body {
  padding: 16px 20px;
  color: var(--cr-fg-primary, #f0f0f0);
  font-size: 14px;
  line-height: 1.6;

  :deep(h1),
  :deep(h2),
  :deep(h3) {
    color: var(--cr-fg-primary, #f0f0f0);
    margin: 1em 0 0.5em;
  }
  :deep(h1) { font-size: 1.5em; }
  :deep(h2) { font-size: 1.25em; }
  :deep(h3) { font-size: 1.1em; }
  :deep(p) { margin: 0.5em 0; }
  :deep(code) {
    background: var(--cr-bg-subtle, #1f1f22);
    padding: 1px 5px;
    border-radius: 3px;
    font-family: var(--cr-font-mono, ui-monospace, monospace);
    font-size: 0.9em;
  }
  :deep(pre) {
    background: var(--cr-bg-subtle, #1f1f22);
    padding: 12px;
    border-radius: 4px;
    overflow-x: auto;
  }
  :deep(pre code) {
    background: transparent;
    padding: 0;
  }
  :deep(a) {
    color: var(--cr-link, #60a5fa);
    text-decoration: none;
  }
  :deep(a:hover) { text-decoration: underline; }
  :deep(ul),
  :deep(ol) { padding-left: 1.5em; }
  :deep(blockquote) {
    border-left: 3px solid var(--cr-border-subtle, rgba(255, 255, 255, 0.12));
    padding-left: 12px;
    color: var(--cr-fg-secondary, #ccc);
    margin: 0.5em 0;
  }
}

.cr-w-md-empty {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--cr-fg-tertiary, #888);
  text-align: center;
  padding: 24px;
}

.cr-w-md-icon {
  color: var(--cr-fg-tertiary, #888);
}
</style>
