import { defineStore, acceptHMRUpdate } from 'pinia';
import { computed, ref } from 'vue';
import { useLocalStorage } from '@vueuse/core';
import { Agents } from 'src/api';

/**
 * Pinia store for the in-app AI assistant. v0 scope:
 *   - One conversation, persisted to localStorage so refreshes / page
 *     navigation keep the history visible.
 *   - Plain chat round-trip — no system prompt, no per-screen context, no
 *     tools. The server only sends what we explicitly put in the body.
 *   - Single-turn at a time (no streaming) — `isThinking` flips while a
 *     response is in flight; the UI shows a typing indicator.
 *
 * Future: streaming via SignalR, per-screen context attachment (whitelisted),
 * tool registry the assistant can call, and a clear history-redaction pass
 * for persisted messages.
 */

export type AssistantRole = 'user' | 'assistant';

export interface AssistantMessage {
  id: string;
  role: AssistantRole;
  content: string;
  createdAt: number;
  /** When the assistant turn errored, the reply is empty and `error` carries the reason. */
  error?: string;
  /** Provider/model that produced the assistant turn — useful for "which AI said this?" later. */
  provider?: string | null;
  model?: string | null;
}

const STORAGE_KEY = 'creuser.assistant.history';

function newId() {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

export const useAssistantStore = defineStore('assistant', () => {
  const messages = useLocalStorage<AssistantMessage[]>(STORAGE_KEY, []);
  // Persist the open/closed state too so a refresh doesn't dismiss the
  // panel mid-conversation. Same per-browser scoping as the message
  // history; promotes to server-side user prefs together when those land.
  const isOpen = useLocalStorage<boolean>('creuser.assistant.open', false);
  const isThinking = ref(false);

  const hasHistory = computed(() => messages.value.length > 0);

  function open() {
    isOpen.value = true;
  }

  function close() {
    isOpen.value = false;
  }

  function toggle() {
    isOpen.value = !isOpen.value;
  }

  function clear() {
    messages.value = [];
  }

  /**
   * Append a user message + an assistant reply by round-tripping through
   * the configured provider. Any error from the provider is captured on
   * the assistant turn so the UI can render it inline rather than as a
   * top-of-page toast.
   *
   * `currentScreen` is the SPA route the user is on — gets passed to the
   * server as explicit context so the assistant knows where they are and
   * can suggest relevant capabilities.
   */
  async function send(text: string, currentScreen?: string | null) {
    const trimmed = text.trim();
    if (!trimmed || isThinking.value) return;

    const userMsg: AssistantMessage = {
      id: newId(),
      role: 'user',
      content: trimmed,
      createdAt: Date.now(),
    };
    messages.value = [...messages.value, userMsg];
    isThinking.value = true;

    try {
      const res = await Agents.agentChat({
        body: {
          message: trimmed,
          currentScreen: currentScreen ?? null,
          history: messages.value
            .filter((m) => m.id !== userMsg.id && !m.error)
            .map((m) => ({ role: m.role, content: m.content })),
        },
      });

      const result = res.data?.result;
      const assistantMsg: AssistantMessage = {
        id: newId(),
        role: 'assistant',
        content: result?.reply ?? '',
        createdAt: Date.now(),
        error: result?.ok ? undefined : result?.error ?? 'AI request failed.',
        provider: result?.provider,
        model: result?.model,
      };
      messages.value = [...messages.value, assistantMsg];
    } catch (e) {
      const assistantMsg: AssistantMessage = {
        id: newId(),
        role: 'assistant',
        content: '',
        createdAt: Date.now(),
        error: e instanceof Error ? e.message : 'AI request failed.',
      };
      messages.value = [...messages.value, assistantMsg];
    } finally {
      isThinking.value = false;
    }
  }

  return {
    messages,
    isOpen,
    isThinking,
    hasHistory,
    open,
    close,
    toggle,
    clear,
    send,
  };
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useAssistantStore, import.meta.hot));
}
