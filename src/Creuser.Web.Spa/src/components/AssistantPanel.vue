<template>
  <q-drawer
    v-model="isOpen"
    side="right"
    bordered
    :width="420"
    :breakpoint="0"
    class="cr-assistant-drawer"
  >
    <div class="cr-assistant">
      <header class="cr-assistant-header">
        <div class="cr-assistant-title">
          <q-icon name="auto_awesome" size="18px" />
          <span>Assistant</span>
        </div>
        <q-space />
        <q-btn
          v-if="assistant.hasHistory"
          flat
          dense
          round
          icon="delete_sweep"
          size="sm"
          aria-label="Clear conversation"
          @click="onClear"
        >
          <q-tooltip>Clear conversation</q-tooltip>
        </q-btn>
        <q-btn
          flat
          dense
          round
          icon="close"
          size="sm"
          aria-label="Close assistant"
          @click="assistant.close"
        />
      </header>

      <main ref="listEl" class="cr-assistant-messages">
        <div v-if="!assistant.hasHistory" class="cr-assistant-empty">
          <q-icon name="auto_awesome" size="32px" class="cr-assistant-empty-icon" />
          <p class="cr-assistant-empty-title">How can I help?</p>
          <p class="cr-assistant-empty-hint">
            Ask anything. Currently I only see what you type — no per-screen
            context yet.
          </p>
        </div>

        <article
          v-for="msg in assistant.messages"
          :key="msg.id"
          class="cr-assistant-msg"
          :class="`cr-assistant-msg--${msg.role}`"
        >
          <div class="cr-assistant-msg-bubble">
            <div v-if="msg.error" class="cr-assistant-msg-error">
              <q-icon name="error" size="14px" />
              <span>{{ msg.error }}</span>
            </div>
            <div v-else class="cr-assistant-msg-body">
              <template v-for="(part, i) in parseMessageBody(msg.content)" :key="i">
                <a
                  v-if="part.kind === 'link'"
                  :href="part.url"
                  class="cr-assistant-msg-link"
                  @click.prevent="onLinkClick(part.url)"
                >{{ part.text }}</a>
                <span v-else>{{ part.text }}</span>
              </template>
            </div>
          </div>
          <div v-if="msg.role === 'assistant' && msg.model" class="cr-assistant-msg-meta">
            {{ msg.model }}
          </div>
        </article>

        <div v-if="assistant.isThinking" class="cr-assistant-typing">
          <span class="cr-assistant-typing-dot" />
          <span class="cr-assistant-typing-dot" />
          <span class="cr-assistant-typing-dot" />
        </div>
      </main>

      <footer class="cr-assistant-input">
        <q-input
          v-model="draft"
          type="textarea"
          autogrow
          dense
          outlined
          placeholder="Ask the assistant..."
          input-class="cr-assistant-input-textarea"
          :disable="assistant.isThinking"
          @keydown.enter.exact.prevent="onSend"
          @keydown.enter.shift.exact="() => {}"
        />
        <q-btn
          color="primary"
          unelevated
          no-caps
          icon-right="send"
          label="Send"
          :loading="assistant.isThinking"
          :disable="!draft.trim() || assistant.isThinking"
          @click="onSend"
        />
        <p class="cr-assistant-input-hint">
          <kbd>Enter</kbd> to send · <kbd>Shift+Enter</kbd> for newline
        </p>
      </footer>
    </div>
  </q-drawer>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, useTemplateRef, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useAssistantStore } from 'stores/assistant';

const assistant = useAssistantStore();
const route = useRoute();
const router = useRouter();
const draft = ref('');
const listEl = useTemplateRef<HTMLElement>('listEl');

interface MessagePart {
  kind: 'text' | 'link';
  text: string;
  url: string;
}

/**
 * Tiny markdown-link parser. Supports `[label](/path)` and
 * `[label](/path?query)` shapes — only internal routes (must start with `/`),
 * never absolute URLs. The assistant is instructed to emit only this shape;
 * if the model goes off-script and emits an external URL, we fall through
 * and render it as plain text rather than turning it into a clickable link.
 */
function parseMessageBody(text: string): MessagePart[] {
  const parts: MessagePart[] = [];
  const re = /\[([^\]]+)\]\((\/[^)\s]*)\)/g;
  let lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = re.exec(text)) !== null) {
    if (match.index > lastIndex) {
      parts.push({ kind: 'text', text: text.slice(lastIndex, match.index), url: '' });
    }
    parts.push({ kind: 'link', text: match[1] ?? '', url: match[2] ?? '' });
    lastIndex = match.index + match[0].length;
  }
  if (lastIndex < text.length) {
    parts.push({ kind: 'text', text: text.slice(lastIndex), url: '' });
  }
  return parts;
}

function onLinkClick(url: string) {
  // Use the router so SPA navigation kicks in (no full reload). The
  // destination page reads `?expand=` from its own onMounted and unfurls
  // the relevant section.
  void router.push(url);
  // Auto-close the assistant on mobile so the destination page is visible;
  // keep it open on larger screens since there's room for both.
  if (window.matchMedia('(max-width: 800px)').matches) {
    assistant.close();
  }
}

// Two-way bind to the store's isOpen so the drawer's outside-tap dismiss
// works (q-drawer flips its model-value, store gets the update).
const isOpen = computed({
  get: () => assistant.isOpen,
  set: (v: boolean) => {
    if (v) assistant.open();
    else assistant.close();
  },
});

async function onSend() {
  const text = draft.value;
  draft.value = '';
  await assistant.send(text, route.path);
}

function onClear() {
  assistant.clear();
}

// Keep the message list pinned to the bottom as new messages arrive
// (initial open, after send, on streaming response in the future).
async function scrollToBottom() {
  await nextTick();
  if (listEl.value) listEl.value.scrollTop = listEl.value.scrollHeight;
}

watch(() => assistant.messages.length, scrollToBottom);
watch(() => assistant.isThinking, scrollToBottom);
watch(
  () => assistant.isOpen,
  (open) => {
    if (open) void scrollToBottom();
  },
);
</script>

<style lang="scss" scoped>
.cr-assistant-drawer {
  background: var(--cr-bg-sidebar);
}

.cr-assistant {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: var(--cr-bg-sidebar);
  color: var(--cr-fg-primary);
}

.cr-assistant-header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 8px 8px 14px;
  height: var(--cr-header-height);
  border-bottom: 1px solid var(--cr-border-header);
}

.cr-assistant-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  font-weight: 600;
  color: var(--cr-fg-primary);
  letter-spacing: 0.04em;

  .q-icon {
    color: var(--q-primary);
  }
}

.cr-assistant-messages {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.cr-assistant-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
  margin: auto;
  padding: 32px 24px;
  color: var(--cr-fg-secondary);
}

.cr-assistant-empty-icon {
  color: var(--q-primary);
  margin-bottom: 12px;
}

.cr-assistant-empty-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--cr-fg-primary);
  margin: 0 0 4px;
}

.cr-assistant-empty-hint {
  font-size: 12px;
  color: var(--cr-fg-tertiary);
  margin: 0;
  max-width: 280px;
  line-height: 1.5;
}

.cr-assistant-msg {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.cr-assistant-msg--user {
  align-items: flex-end;

  .cr-assistant-msg-bubble {
    background: var(--cr-brand-tint-medium);
    color: var(--cr-fg-primary);
    border-color: transparent;
    max-width: 90%;
  }
}

.cr-assistant-msg--assistant {
  align-items: flex-start;

  .cr-assistant-msg-bubble {
    background: var(--cr-bg-elevated);
    color: var(--cr-fg-primary);
    max-width: 100%;
  }
}

.cr-assistant-msg-bubble {
  padding: 8px 12px;
  border-radius: 10px;
  border: 1px solid var(--cr-border-subtle);
  font-size: 13px;
  line-height: 1.5;
  word-break: break-word;
}

.cr-assistant-msg-body {
  white-space: pre-wrap;
}

.cr-assistant-msg-link {
  display: inline-block;
  color: var(--cr-link);
  font-weight: 600;
  text-decoration: underline;
  text-decoration-color: color-mix(in srgb, var(--cr-link) 60%, transparent);
  text-underline-offset: 2px;
  cursor: pointer;
  padding: 0 1px;
  border-radius: 2px;

  &:hover {
    color: var(--cr-link-hover);
    background: color-mix(in srgb, var(--cr-link) 12%, transparent);
    text-decoration-color: var(--cr-link-hover);
  }
}

.cr-assistant-msg-error {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  color: var(--q-negative);
  font-size: 12px;
}

.cr-assistant-msg-meta {
  font-size: 10px;
  color: var(--cr-fg-tertiary);
  letter-spacing: 0.02em;
  font-family: var(--cr-font-family-mono);
}

.cr-assistant-typing {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 8px 12px;
  background: var(--cr-bg-elevated);
  border: 1px solid var(--cr-border-subtle);
  border-radius: 10px;
  width: fit-content;
}

.cr-assistant-typing-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--cr-fg-tertiary);
  animation: cr-typing 1.4s ease-in-out infinite;

  &:nth-child(2) {
    animation-delay: 0.16s;
  }
  &:nth-child(3) {
    animation-delay: 0.32s;
  }
}

@keyframes cr-typing {
  0%,
  60%,
  100% {
    transform: scale(0.6);
    opacity: 0.4;
  }
  30% {
    transform: scale(1);
    opacity: 1;
  }
}

.cr-assistant-input {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 12px 16px 16px;
  border-top: 1px solid var(--cr-border-subtle);
}

:deep(.cr-assistant-input-textarea) {
  font-family: var(--cr-font-family);
  font-size: 13px;
  max-height: 160px;
}

.cr-assistant-input-hint {
  margin: 0;
  font-size: 10px;
  color: var(--cr-fg-tertiary);

  kbd {
    font-family: var(--cr-font-family-mono);
    font-size: 10px;
    background: var(--cr-bg-elevated);
    border: 1px solid var(--cr-border-subtle);
    border-radius: 3px;
    padding: 0 4px;
  }
}
</style>
