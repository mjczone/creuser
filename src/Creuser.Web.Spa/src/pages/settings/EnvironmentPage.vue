<template>
  <div class="cr-env-page">
    <header class="cr-env-header">
      <h1 class="text-h5 q-ma-none">Environment</h1>
      <p class="cr-env-subhead">
        Platform-level configuration: SMTP, AI providers, base URL.
        Secrets are stored on disk under <code>/data/secrets/</code> and never
        returned by the API.
      </p>
    </header>

    <q-form class="cr-env-form" @submit.prevent="onSave">
      <q-expansion-item
        v-model="expanded.general"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="General"
        caption="Base URL and timezone"
        header-class="cr-env-section-header"
        class="cr-env-section"
        data-section-key="general"
      >
        <div class="cr-env-section-body">
          <p class="cr-env-section-hint">
            <code>BaseUrl</code> shows up in outbound emails and webhook
            payloads — set this to the URL operators see in their browsers
            (e.g. <code>https://creuser.example.com</code>). Timezone is used
            for human-readable timestamps in emails and reports.
          </p>

          <q-input
            v-model="generalBaseUrl"
            label="Base URL"
            placeholder="https://creuser.example.com"
            dense
            outlined
            class="cr-env-input"
          />
          <q-input
            v-model="generalTimezone"
            label="Timezone (IANA)"
            placeholder="UTC"
            hint="e.g. America/New_York, Europe/Berlin, UTC"
            dense
            outlined
            class="cr-env-input"
          />
        </div>
      </q-expansion-item>

      <q-expansion-item
        v-model="expanded.smtp"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="SMTP"
        caption="Outgoing email"
        header-class="cr-env-section-header"
        class="cr-env-section"
        data-section-key="smtp"
      >
        <div class="cr-env-section-body">
          <p class="cr-env-section-hint">
            Used by password-reset emails, run-failure notifications, and
            invite flows once those land.
          </p>

          <q-input v-model="smtpHost" label="Host" dense outlined class="cr-env-input" />
          <q-input
            v-model.number="smtpPort"
            label="Port"
            type="number"
            dense
            outlined
            class="cr-env-input"
          />
          <q-select
            v-model="smtpEncryption"
            :options="encryptionOptions"
            label="Encryption"
            dense
            outlined
            emit-value
            map-options
            class="cr-env-input"
          />
          <q-input
            v-model="smtpUsername"
            label="Username"
            dense
            outlined
            class="cr-env-input"
            autocomplete="off"
          />
          <SecretInput
            label="Password"
            :name="smtpPasswordSecret || 'smtp.password'"
            :present="!!secretsPresent[smtpPasswordSecret || 'smtp.password']"
            placeholder="SMTP password"
            @saved="onSecretSaved"
            @cleared="onSecretCleared"
          />
          <q-input
            v-model="smtpFromAddress"
            label="From address"
            placeholder="no-reply@creuser.example.com"
            dense
            outlined
            class="cr-env-input"
          />
          <q-input
            v-model="smtpFromName"
            label="From name"
            placeholder="Creuser"
            dense
            outlined
            class="cr-env-input"
          />
        </div>
      </q-expansion-item>

      <q-expansion-item
        v-model="expanded.aiProviders"
        dense
        switch-toggle-side
        expand-icon-toggle
        label="AI providers"
        caption="Default provider + models"
        header-class="cr-env-section-header"
        class="cr-env-section"
        data-section-key="aiProviders"
      >
        <div class="cr-env-section-body">
          <p class="cr-env-section-hint">
            Configure at least one provider to enable in-app AI assistance,
            agent runs, and <code>llm-*</code> job types. The default provider
            is what unspecified chats and agentic jobs route to.
          </p>

          <div class="cr-env-subsection">
            <h3 class="cr-env-subsection-title">Default provider</h3>
            <q-btn-toggle
              v-model="aiDefaultProvider"
              :options="defaultProviderOptions"
              unelevated
              no-caps
              toggle-color="primary"
              class="cr-env-toggle"
            />
          </div>

          <CollapsibleSection
            v-model="expanded.aiAnthropic"
            title="Anthropic"
            caption="Cloud Claude"
            section-key="aiAnthropic"
          >
            <template #action>
              <q-btn
                flat
                dense
                no-caps
                size="sm"
                icon="check_circle_outline"
                label="Test connection"
                :loading="testing.anthropic"
                @click.stop="onTestProvider('anthropic')"
              />
            </template>
            <SecretInput
              label="API key"
              :name="anthropicKeySecret || 'anthropic.key'"
              :present="!!secretsPresent[anthropicKeySecret || 'anthropic.key']"
              placeholder="sk-ant-..."
              @saved="onSecretSaved"
              @cleared="onSecretCleared"
            />
            <q-select
              v-model="anthropicDefaultModel"
              :options="anthropicModelOptions"
              label="Default model"
              dense
              outlined
              emit-value
              map-options
              class="cr-env-input"
            />
            <q-input
              v-model="anthropicBaseUrl"
              label="Base URL (optional)"
              placeholder="Leave blank for the standard Anthropic endpoint"
              hint="Override for Bedrock, corporate proxies, etc."
              dense
              outlined
              class="cr-env-input"
            />
            <HealthBanner :result="health.anthropic" @dismiss="health.anthropic = null" />
          </CollapsibleSection>

          <CollapsibleSection
            v-model="expanded.aiOpenAI"
            title="OpenAI"
            caption="GPT family / Azure / OpenAI-compatible cloud"
            section-key="aiOpenAI"
          >
            <template #action>
              <q-btn
                flat
                dense
                no-caps
                size="sm"
                icon="check_circle_outline"
                label="Test connection"
                :loading="testing.openai"
                @click.stop="onTestProvider('openai')"
              />
            </template>
            <SecretInput
              label="API key"
              :name="openaiKeySecret || 'openai.key'"
              :present="!!secretsPresent[openaiKeySecret || 'openai.key']"
              placeholder="sk-..."
              @saved="onSecretSaved"
              @cleared="onSecretCleared"
            />
            <q-select
              v-model="openaiDefaultModel"
              :options="openaiModelOptions"
              label="Default model"
              dense
              outlined
              emit-value
              map-options
              class="cr-env-input"
            />
            <q-input
              v-model="openaiBaseUrl"
              label="Base URL (optional)"
              placeholder="Leave blank for the standard OpenAI endpoint"
              hint="For Azure OpenAI / corporate proxies"
              dense
              outlined
              class="cr-env-input"
            />
            <q-input
              v-show="!!openaiBaseUrl"
              v-model="openaiAzureDeployment"
              label="Azure deployment name (optional)"
              dense
              outlined
              class="cr-env-input"
            />
            <HealthBanner :result="health.openai" @dismiss="health.openai = null" />
          </CollapsibleSection>

          <CollapsibleSection
            v-model="expanded.aiLocal"
            title="Local"
            caption="Ollama / LM Studio / OpenAI-compatible local server"
            section-key="aiLocal"
          >
            <template #action>
              <q-btn
                flat
                dense
                no-caps
                size="sm"
                icon="check_circle_outline"
                label="Test connection"
                :loading="testing.local"
                @click.stop="onTestProvider('local')"
              />
            </template>

            <p class="cr-env-section-hint">
              Any OpenAI-compatible local server. Quick presets fill in the
              defaults; tweak afterward to taste.
              <br />
              <strong>Networking:</strong> when Creuser runs in Docker, use
              <code>host.docker.internal</code> (auto-mapped in the bundled
              compose) to reach a server on the host machine, or
              <code>http://&lt;service-name&gt;:&lt;port&gt;/v1</code> for
              an Ollama / LM Studio sibling container in the same compose
              stack. The presets below pick the right one based on how
              you're running.
            </p>

            <div class="cr-env-local-presets">
              <q-btn
                flat
                no-caps
                size="sm"
                icon="bolt"
                label="Ollama defaults"
                @click="applyLocalPreset('ollama')"
              />
              <q-btn
                flat
                no-caps
                size="sm"
                icon="bolt"
                label="LM Studio defaults"
                @click="applyLocalPreset('lmstudio')"
              />
            </div>

            <q-input
              v-model="localBaseUrl"
              label="Endpoint URL"
              placeholder="http://localhost:11434/v1"
              hint="Ollama: localhost:11434/v1 · LM Studio: localhost:1234/v1"
              dense
              outlined
              class="cr-env-input"
            />
            <q-input
              v-model="localDefaultModel"
              label="Model"
              placeholder="llama3.1, qwen2.5-coder:32b, gpt-oss-120b, ..."
              hint="Free text — must match a model the local server has loaded."
              dense
              outlined
              class="cr-env-input"
            />
            <SecretInput
              label="API key"
              optional
              :name="localKeySecret || 'local.key'"
              :present="!!secretsPresent[localKeySecret || 'local.key']"
              placeholder="Most local servers don't authenticate — leave blank"
              hint="Set if your server enforces an API key (vLLM, OpenRouter-style proxies, etc.)."
              @saved="onSecretSaved"
              @cleared="onSecretCleared"
            />
            <HealthBanner :result="health.local" @dismiss="health.local = null" />
          </CollapsibleSection>
        </div>
      </q-expansion-item>

      <footer class="cr-env-actions">
        <q-btn
          flat
          no-caps
          label="Reset to saved"
          :disable="!isDirty || saving"
          @click="onResetToSaved"
        />
        <q-space />
        <q-btn
          type="submit"
          color="primary"
          unelevated
          no-caps
          label="Save"
          :loading="saving"
          :disable="!isDirty"
        />
      </footer>
    </q-form>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref } from 'vue';
import { useRoute } from 'vue-router';
import { useQuasar } from 'quasar';
import { useLocalStorage } from '@vueuse/core';
import {
  Agents,
  Environment,
  type AgentHealthResult,
  type EnvironmentConfig,
  type EnvironmentConfigView,
  type GeneralConfig,
  type SmtpConfig,
  type AiProvidersConfig,
  type AnthropicConfig,
  // hey-api lowercases the trailing capital so OpenAIConfig becomes OpenAiConfig
  // on the wire — the C# type's serialized property `openAI` keeps the original
  // casing because it's at a different layer.
  type OpenAiConfig,
  type LocalProviderConfig,
} from 'src/api';
import CollapsibleSection from 'components/CollapsibleSection.vue';
import HealthBanner from 'components/environment/HealthBanner.vue';
import SecretInput from 'components/environment/SecretInput.vue';

const $q = useQuasar();

// Bumped key (`.v2`) so anyone whose browser remembers `aiAnthropic: true`
// from the previous default gets a fresh closed-by-default state. Future
// changes that flip a default should bump this again — or we can promote
// to a server-side `cr.user_preferences` schema that has its own migration
// story.
const expanded = useLocalStorage<Record<string, boolean>>('creuser.environment.expanded.v2', {
  general: true,
  smtp: false,
  aiProviders: false,
  aiAnthropic: false,
  aiOpenAI: false,
  aiLocal: false,
});

const encryptionOptions = [
  { label: 'None', value: 'none' },
  { label: 'STARTTLS', value: 'starttls' },
  { label: 'TLS', value: 'tls' },
];

// Hardcoded for v1. The architecture explicitly recommends pinning to the
// latest Claude family + GPT-5; admins can edit `defaultModel` via Custom
// CSS — er, via direct backend API call — if they need a model not listed.
const anthropicModelOptions = [
  { label: 'Claude Opus 4.7 (latest)', value: 'claude-opus-4-7' },
  { label: 'Claude Sonnet 4.6', value: 'claude-sonnet-4-6' },
  { label: 'Claude Haiku 4.5', value: 'claude-haiku-4-5-20251001' },
];

const openaiModelOptions = [
  { label: 'GPT-5', value: 'gpt-5' },
  { label: 'GPT-5 mini', value: 'gpt-5-mini' },
  { label: 'GPT-5 nano', value: 'gpt-5-nano' },
  { label: 'GPT-4o (legacy)', value: 'gpt-4o' },
  { label: 'GPT-4o mini (legacy)', value: 'gpt-4o-mini' },
];

const defaultProviderOptions = [
  { label: 'Anthropic', value: 'anthropic' },
  { label: 'OpenAI', value: 'openai' },
  { label: 'Local', value: 'local' },
];

// Server-canonical state — what's persisted, used for dirty-tracking.
const saved = ref<EnvironmentConfig | null>(null);
const secretsPresent = ref<Record<string, boolean>>({});
const saving = ref(false);

// Per-provider Test connection state.
const testing = reactive<Record<string, boolean>>({
  anthropic: false,
  openai: false,
  local: false,
});
const health = reactive<Record<string, AgentHealthResult | null>>({
  anthropic: null,
  openai: null,
  local: null,
});

// Draft fields. We unpack the nested EnvironmentConfig shape into flat
// reactive fields for ergonomic v-model binding, then re-pack into the
// nested record on save.
const draft = reactive({
  // general
  baseUrl: '',
  timezone: '',
  // smtp
  smtpHost: '',
  smtpPort: null as number | null,
  smtpEncryption: 'none',
  smtpUsername: '',
  smtpPasswordSecret: 'smtp.password',
  smtpFromAddress: '',
  smtpFromName: '',
  // ai
  aiDefaultProvider: 'anthropic',
  anthropicKeySecret: 'anthropic.key',
  anthropicDefaultModel: 'claude-opus-4-7',
  anthropicBaseUrl: '',
  openaiKeySecret: 'openai.key',
  openaiDefaultModel: 'gpt-5',
  openaiBaseUrl: '',
  openaiAzureDeployment: '',
  // local
  localKeySecret: 'local.key',
  localBaseUrl: '',
  localDefaultModel: '',
  localKind: 'custom',
});

// Bidirectional refs that v-model binds to and write back into draft.
const generalBaseUrl = computed({
  get: () => draft.baseUrl,
  set: (v) => (draft.baseUrl = v),
});
const generalTimezone = computed({
  get: () => draft.timezone,
  set: (v) => (draft.timezone = v),
});
const smtpHost = computed({
  get: () => draft.smtpHost,
  set: (v) => (draft.smtpHost = v),
});
const smtpPort = computed<number | null>({
  get: () => draft.smtpPort,
  // q-input + v-model.number can hand us a string ("") on clear or a real
  // number on type. Coerce to number | null so packDraft sees the right
  // shape on save.
  set: (v: unknown) => {
    if (v === null || v === undefined || v === '') {
      draft.smtpPort = null;
      return;
    }
    const n = typeof v === 'number' ? v : Number(v);
    draft.smtpPort = Number.isNaN(n) ? null : n;
  },
});
const smtpEncryption = computed({
  get: () => draft.smtpEncryption,
  set: (v) => (draft.smtpEncryption = v),
});
const smtpUsername = computed({
  get: () => draft.smtpUsername,
  set: (v) => (draft.smtpUsername = v),
});
const smtpPasswordSecret = computed(() => draft.smtpPasswordSecret);
const smtpFromAddress = computed({
  get: () => draft.smtpFromAddress,
  set: (v) => (draft.smtpFromAddress = v),
});
const smtpFromName = computed({
  get: () => draft.smtpFromName,
  set: (v) => (draft.smtpFromName = v),
});
const aiDefaultProvider = computed({
  get: () => draft.aiDefaultProvider,
  set: (v) => (draft.aiDefaultProvider = v),
});
const anthropicKeySecret = computed(() => draft.anthropicKeySecret);
const anthropicDefaultModel = computed({
  get: () => draft.anthropicDefaultModel,
  set: (v) => (draft.anthropicDefaultModel = v),
});
const anthropicBaseUrl = computed({
  get: () => draft.anthropicBaseUrl,
  set: (v) => (draft.anthropicBaseUrl = v),
});
const openaiKeySecret = computed(() => draft.openaiKeySecret);
const openaiDefaultModel = computed({
  get: () => draft.openaiDefaultModel,
  set: (v) => (draft.openaiDefaultModel = v),
});
const openaiBaseUrl = computed({
  get: () => draft.openaiBaseUrl,
  set: (v) => (draft.openaiBaseUrl = v),
});
const openaiAzureDeployment = computed({
  get: () => draft.openaiAzureDeployment,
  set: (v) => (draft.openaiAzureDeployment = v),
});
const localKeySecret = computed(() => draft.localKeySecret);
const localBaseUrl = computed({
  get: () => draft.localBaseUrl,
  set: (v) => (draft.localBaseUrl = v),
});
const localDefaultModel = computed({
  get: () => draft.localDefaultModel,
  set: (v) => (draft.localDefaultModel = v),
});

/**
 * Pick the right hostname to reach a local LLM server:
 *   - Browser pointed at localhost / 127.0.0.1 → backend is also on the host
 *     (running `dotnet watch`), so `localhost:<port>` reaches the local server.
 *   - Browser pointed at any other hostname → Creuser is in a container,
 *     so `host.docker.internal:<port>` is needed to escape to the host.
 *     (The compose file maps host.docker.internal to the host gateway, which
 *     is automatic on Docker Desktop and explicit on Linux.)
 *
 * Admins can still override the URL after picking the preset — this is just
 * a sensible default for the most common deployment shape.
 */
function localHostnameForBrowser(): string {
  if (typeof window === 'undefined') return 'localhost';
  const h = window.location.hostname;
  return h === 'localhost' || h === '127.0.0.1' || h === '::1' ? 'localhost' : 'host.docker.internal';
}

function applyLocalPreset(kind: 'ollama' | 'lmstudio') {
  // Quick-fill the endpoint URL + a sensible default model for the picked
  // local server. Doesn't touch the API key (most local setups don't use one)
  // or wipe an existing custom model the admin already typed.
  const host = localHostnameForBrowser();
  if (kind === 'ollama') {
    draft.localBaseUrl = `http://${host}:11434/v1`;
    if (!draft.localDefaultModel) draft.localDefaultModel = 'llama3.1';
  } else {
    draft.localBaseUrl = `http://${host}:1234/v1`;
    if (!draft.localDefaultModel) draft.localDefaultModel = 'local-model';
  }
  draft.localKind = kind;
}

const isDirty = computed(() => {
  if (!saved.value) return false;
  return JSON.stringify(packDraft()) !== JSON.stringify(saved.value);
});

function unpackToDraft(c: EnvironmentConfig) {
  draft.baseUrl = c.general.baseUrl ?? '';
  draft.timezone = c.general.timezone ?? '';

  draft.smtpHost = c.smtp.host ?? '';
  // Generated TS treats nullable C# int as `number | string | null` because
  // some JSON pipelines accept either. Coerce on read.
  draft.smtpPort =
    typeof c.smtp.port === 'number'
      ? c.smtp.port
      : typeof c.smtp.port === 'string' && c.smtp.port !== ''
        ? Number(c.smtp.port)
        : null;
  draft.smtpEncryption = c.smtp.encryption ?? 'none';
  draft.smtpUsername = c.smtp.username ?? '';
  draft.smtpPasswordSecret = c.smtp.passwordSecret ?? 'smtp.password';
  draft.smtpFromAddress = c.smtp.fromAddress ?? '';
  draft.smtpFromName = c.smtp.fromName ?? '';

  draft.aiDefaultProvider =
    c.aiProviders.defaultProvider === 'openai' ? 'openai' : 'anthropic';

  const anthro = c.aiProviders.anthropic ?? {};
  draft.anthropicKeySecret = anthro.apiKeySecret ?? 'anthropic.key';
  draft.anthropicDefaultModel = anthro.defaultModel ?? 'claude-opus-4-7';
  draft.anthropicBaseUrl = anthro.baseUrl ?? '';

  const oai = c.aiProviders.openAI ?? {};
  draft.openaiKeySecret = oai.apiKeySecret ?? 'openai.key';
  draft.openaiDefaultModel = oai.defaultModel ?? 'gpt-5';
  draft.openaiBaseUrl = oai.baseUrl ?? '';
  draft.openaiAzureDeployment = oai.azureDeployment ?? '';

  const local = c.aiProviders.local ?? {};
  draft.localKeySecret = local.apiKeySecret ?? 'local.key';
  draft.localBaseUrl = local.baseUrl ?? '';
  draft.localDefaultModel = local.defaultModel ?? '';
  draft.localKind = local.kind ?? 'custom';
}

function packDraft(): EnvironmentConfig {
  const general: GeneralConfig = {
    baseUrl: emptyToNull(draft.baseUrl),
    timezone: emptyToNull(draft.timezone),
  };
  const smtp: SmtpConfig = {
    host: emptyToNull(draft.smtpHost),
    port: typeof draft.smtpPort === 'number' ? draft.smtpPort : null,
    username: emptyToNull(draft.smtpUsername),
    passwordSecret: emptyToNull(draft.smtpPasswordSecret),
    encryption: emptyToNull(draft.smtpEncryption),
    fromAddress: emptyToNull(draft.smtpFromAddress),
    fromName: emptyToNull(draft.smtpFromName),
  };
  const anthropic: AnthropicConfig = {
    apiKeySecret: emptyToNull(draft.anthropicKeySecret),
    defaultModel: emptyToNull(draft.anthropicDefaultModel),
    baseUrl: emptyToNull(draft.anthropicBaseUrl),
  };
  const openAI: OpenAiConfig = {
    apiKeySecret: emptyToNull(draft.openaiKeySecret),
    defaultModel: emptyToNull(draft.openaiDefaultModel),
    baseUrl: emptyToNull(draft.openaiBaseUrl),
    azureDeployment: emptyToNull(draft.openaiAzureDeployment),
  };
  const local: LocalProviderConfig = {
    apiKeySecret: emptyToNull(draft.localKeySecret),
    defaultModel: emptyToNull(draft.localDefaultModel),
    baseUrl: emptyToNull(draft.localBaseUrl),
    kind: emptyToNull(draft.localKind),
  };
  const aiProviders: AiProvidersConfig = {
    defaultProvider: draft.aiDefaultProvider,
    anthropic,
    openAI,
    local,
  };
  return { general, smtp, aiProviders };
}

function emptyToNull(s: string | null | undefined): string | null {
  if (s === null || s === undefined) return null;
  const trimmed = String(s).trim();
  return trimmed.length === 0 ? null : trimmed;
}

async function load() {
  const res = await Environment.getEnvironment();
  const view: EnvironmentConfigView | undefined | null = res.data?.result;
  if (!view) return;
  saved.value = view.config;
  secretsPresent.value = view.secretsPresent ?? {};
  unpackToDraft(view.config);
}

async function onSave() {
  saving.value = true;
  try {
    const body = packDraft();
    const res = await Environment.updateEnvironment({ body });
    if (res.error || !res.data?.result) {
      $q.notify({
        type: 'negative',
        position: 'top',
        message: problemMessage(res.error) ?? 'Failed to save environment.',
      });
      return;
    }
    saved.value = res.data.result.config;
    secretsPresent.value = res.data.result.secretsPresent ?? {};
    $q.notify({ type: 'positive', position: 'top', message: 'Environment saved.' });
  } finally {
    saving.value = false;
  }
}

function onResetToSaved() {
  if (saved.value) unpackToDraft(saved.value);
}

async function refreshSecretsPresent() {
  // After SecretInput emits saved/cleared, fetch the env view fresh so the
  // SecretsPresent map updates without re-loading the entire form.
  const res = await Environment.getEnvironment();
  if (res.data?.result) {
    secretsPresent.value = res.data.result.secretsPresent ?? {};
  }
}

function onSecretSaved() {
  void refreshSecretsPresent();
}

function onSecretCleared() {
  void refreshSecretsPresent();
}

async function onTestProvider(provider: 'anthropic' | 'openai' | 'local') {
  testing[provider] = true;
  health[provider] = null;
  try {
    // If the admin tweaked the form (picked a preset, changed URL/model)
    // and clicked Test before Save, persist the draft first — otherwise
    // the server-side test would read the still-stale saved config and
    // report "not configured".
    if (isDirty.value) {
      await onSave();
    }
    const res = await Agents.checkAgentHealth({ query: { provider } });
    if (res.error) {
      health[provider] = {
        ok: false,
        provider,
        model: null,
        latencyMs: null,
        reply: null,
        error: problemMessage(res.error) ?? 'Health check failed.',
      };
      return;
    }
    health[provider] = res.data?.result ?? null;
  } catch (e) {
    health[provider] = {
      ok: false,
      provider,
      model: null,
      latencyMs: null,
      reply: null,
      error: e instanceof Error ? e.message : 'Health check failed.',
    };
  } finally {
    testing[provider] = false;
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

const route = useRoute();

// Map of nested section → its parent's key. When the assistant deep-links
// to `aiAnthropic`, we also expand `aiProviders` so the inner section is
// actually visible. Hardcoded for v1; future: derive from the section
// hierarchy automatically.
const sectionParents: Record<string, string> = {
  aiAnthropic: 'aiProviders',
  aiOpenAI: 'aiProviders',
  aiLocal: 'aiProviders',
};

/**
 * Honor `?expand=<sectionKey>` from the URL so the assistant's navigation
 * links can deep-link to the relevant subsection. Expands the section
 * (and any parents) and scrolls it into view after the next paint.
 */
function honorExpandQuery() {
  const key = route.query.expand;
  if (typeof key !== 'string' || !(key in expanded.value)) return;

  const updates: Record<string, boolean> = { [key]: true };
  const parent = sectionParents[key];
  if (parent && parent in expanded.value) updates[parent] = true;
  expanded.value = { ...expanded.value, ...updates };

  void nextTick(() => {
    const el = document.querySelector(`[data-section-key="${key}"]`);
    el?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  });
}

onMounted(() => {
  void load();
  honorExpandQuery();
});
</script>

<style lang="scss" scoped>
.cr-env-page {
  padding: 32px 40px 96px;
  max-width: 880px;
}

.cr-env-header {
  margin-bottom: 24px;
}

.cr-env-subhead {
  margin: 8px 0 0;
  font-size: 13px;
  color: var(--cr-fg-secondary);

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}

.cr-env-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.cr-env-section {
  border: 1px solid var(--cr-border-subtle);
  border-radius: 6px;
  background: var(--cr-bg-surface);
  overflow: hidden;
}

:deep(.cr-env-section-header) {
  padding: 10px 12px;
  min-height: 0;

  .q-item__label {
    font-size: 13px;
    font-weight: 600;
    color: var(--cr-fg-primary);
    line-height: 1.3;
  }

  .q-item__label--caption {
    font-size: 11px;
    color: var(--cr-fg-tertiary);
    margin-top: 2px;
  }

  .q-icon {
    color: var(--cr-fg-tertiary);
  }
}

.cr-env-section-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 4px 16px 20px;
  border-top: 1px solid var(--cr-border-subtle);
}

.cr-env-section-hint {
  font-size: 12px;
  color: var(--cr-fg-secondary);
  margin: 0 0 4px;

  code {
    font-family: var(--cr-font-family-mono);
    font-size: 11px;
    background: var(--cr-bg-elevated);
    padding: 1px 4px;
    border-radius: 3px;
  }
}

.cr-env-subsection {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px 0;

  & + & {
    border-top: 1px solid var(--cr-border-subtle);
  }
}

.cr-env-subsection-title {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--cr-fg-tertiary);
  margin: 0;
}


.cr-env-input {
  max-width: 520px;
}

.cr-env-toggle {
  align-self: flex-start;
}

.cr-env-local-presets {
  display: flex;
  gap: 4px;
  flex-wrap: wrap;
}

.cr-env-actions {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
  margin-top: 16px;
  border-top: 1px solid var(--cr-border-subtle);
  position: sticky;
  bottom: 0;
  background: var(--cr-bg-page);
}
</style>
