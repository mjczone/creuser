<template>
  <section class="cr-conv-test">
    <header class="cr-conv-test-header">
      <q-icon name="play_circle" size="14px" />
      <span>Test against a file</span>
      <q-tooltip anchor="top middle" self="bottom middle">
        Dry-run this convention against one path in the working tree. Shows the resolved entity,
        slug, computed metadata, and which refs would resolve to peers.
      </q-tooltip>
    </header>

    <div class="cr-conv-test-row">
      <q-input
        v-model="againstPath"
        dense
        outlined
        placeholder="docs/foo/bar.md"
        label="Workspace-relative path"
        :disable="!conventionId || running"
        @keydown.enter="run"
      />
      <q-btn
        unelevated
        no-caps
        dense
        color="primary"
        icon="play_arrow"
        label="Run"
        :loading="running"
        :disable="!conventionId || !againstPath.trim()"
        @click="run"
      />
    </div>

    <div v-if="!result && !running && !errorMessage" class="cr-conv-test-empty">
      Pick a path and run to see what this convention would project.
    </div>

    <div v-if="errorMessage" class="cr-conv-test-error">
      <q-icon name="error_outline" size="14px" />
      {{ errorMessage }}
    </div>

    <div v-if="result?.matched" class="cr-conv-test-result">
      <header class="cr-conv-test-result-header">
        <q-icon name="check_circle" size="14px" class="cr-conv-test-result-ok" />
        <code>{{ result.entity?.kind }}/{{ result.entity?.slug }}</code>
        <span class="cr-conv-test-result-path">{{ result.entity?.path }}</span>
      </header>

      <details class="cr-conv-test-meta">
        <summary>Metadata</summary>
        <pre>{{ formattedMetadata }}</pre>
      </details>

      <div class="cr-conv-test-refs">
        <div class="cr-conv-test-refs-title">
          <strong>Refs ({{ result.refs.length }})</strong>
        </div>
        <ul v-if="result.refs.length > 0">
          <li v-for="(r, i) in result.refs" :key="i">
            <code>{{ r.relationship }}</code>
            <span class="cr-conv-test-arrow">→</span>
            <code v-if="r.targetKind">{{ r.targetKind }}/{{ r.targetSlug ?? '?' }}</code>
            <code v-else>{{ r.targetSlug ?? '?' }}</code>
            <span :class="['cr-conv-test-status', r.toEntityId ? 'is-resolved' : 'is-unresolved']">
              {{ r.toEntityId ? 'resolved' : 'unresolved' }}
            </span>
          </li>
        </ul>
        <div v-else class="cr-conv-test-empty">(no refs)</div>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
/**
 * Dry-run a convention against one workspace-relative path. Calls the
 * `testConvention` op and renders the resolved entity + refs. Live in the
 * conventions settings page so authors can iterate on a rule without
 * waiting for a full projection sync.
 */
import { computed, ref, watch } from 'vue';
import { Projections } from 'src/api';
import type { TestConventionResultDto } from 'src/api';

const props = defineProps<{
  workspaceSlug: string;
  conventionId: string | null;
}>();

const againstPath = ref('');
const running = ref(false);
const result = ref<TestConventionResultDto | null>(null);
const errorMessage = ref<string | null>(null);

const formattedMetadata = computed(() => {
  if (!result.value?.entity?.metadataJson) return '(no metadata)';
  try {
    return JSON.stringify(JSON.parse(result.value.entity.metadataJson), null, 2);
  } catch {
    return result.value.entity.metadataJson;
  }
});

watch(
  () => props.conventionId,
  () => {
    result.value = null;
    errorMessage.value = null;
  },
);

async function run() {
  if (!props.conventionId || !againstPath.value.trim()) return;
  running.value = true;
  result.value = null;
  errorMessage.value = null;
  try {
    const res = await Projections.testConvention({
      path: { slug: props.workspaceSlug, id: props.conventionId },
      body: { againstPath: againstPath.value.trim() },
    });
    if (res.error) {
      errorMessage.value = problemMessage(res.error) ?? 'Test failed.';
      return;
    }
    const t = res.data?.result ?? null;
    if (t && !t.matched) {
      errorMessage.value = t.error ?? `'${againstPath.value.trim()}' was not matched.`;
      return;
    }
    result.value = t;
  } finally {
    running.value = false;
  }
}

function problemMessage(err: unknown): string | undefined {
  if (err && typeof err === 'object') {
    const e = err as { detail?: unknown; title?: unknown };
    if (typeof e.detail === 'string' && e.detail.length) return e.detail;
    if (typeof e.title === 'string' && e.title.length) return e.title;
  }
  return undefined;
}
</script>

<style lang="scss" scoped>
.cr-conv-test {
  border: 1px solid var(--cr-border-subtle);
  background: var(--cr-bg-surface);
  border-radius: 4px;
  padding: 10px 12px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.cr-conv-test-header {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  cursor: help;
}

.cr-conv-test-row {
  display: flex;
  align-items: center;
  gap: 8px;

  > .q-input {
    flex: 1;
  }
}

.cr-conv-test-empty {
  font-size: 11px;
  color: var(--cr-fg-tertiary);
  font-style: italic;
}

.cr-conv-test-error {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--q-negative);
}

.cr-conv-test-result {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.cr-conv-test-result-header {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 5px;
    border-radius: 3px;
    color: var(--cr-fg-primary);
  }
}

.cr-conv-test-result-ok {
  color: var(--q-positive);
}

.cr-conv-test-result-path {
  font-family: var(--cr-font-family-mono);
  font-size: 10px;
  color: var(--cr-fg-tertiary);
}

.cr-conv-test-meta {
  font-size: 11px;

  > summary {
    cursor: pointer;
    color: var(--cr-fg-secondary);
    font-weight: 500;
  }

  > pre {
    font-family: var(--cr-font-family-mono);
    font-size: 10px;
    background: var(--cr-bg-elevated);
    padding: 6px 8px;
    margin-top: 4px;
    border-radius: 3px;
    overflow-x: auto;
    max-height: 240px;
  }
}

.cr-conv-test-refs {
  font-size: 11px;
}

.cr-conv-test-refs-title {
  margin-bottom: 4px;
  color: var(--cr-fg-secondary);
}

.cr-conv-test-refs ul {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.cr-conv-test-refs li {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 10px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}

.cr-conv-test-arrow {
  color: var(--cr-fg-tertiary);
  font-family: var(--cr-font-family-mono);
}

.cr-conv-test-status {
  font-size: 9px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  padding: 1px 5px;
  border-radius: 3px;

  &.is-resolved {
    color: var(--q-positive);
    background: var(--cr-bg-elevated);
  }

  &.is-unresolved {
    color: var(--q-warning);
    background: var(--cr-bg-elevated);
  }
}
</style>
