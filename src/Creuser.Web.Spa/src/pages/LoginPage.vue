<template>
  <div class="login-page">
    <div class="login-grid" />
    <div class="login-center">
      <div class="login-card">
        <img v-if="logoUrl" :src="logoUrl" :alt="productName" class="login-logo-image" />
        <div v-else class="login-logo">{{ productInitial }}</div>
        <div class="login-title">{{ productName.toUpperCase() }}</div>
        <div class="login-subtitle">{{ tagline }}</div>

        <q-form v-if="!showPasswordChange" class="login-form" @submit.prevent="onSubmit">
          <q-input
            v-model="form.email"
            type="email"
            label="Email"
            dense
            outlined
            color="primary"
            lazy-rules
            :rules="[(v: string) => !!v || 'Required']"
            class="login-input"
          />
          <q-input
            v-model="form.password"
            :type="showPassword ? 'text' : 'password'"
            label="Password"
            dense
            outlined
            color="primary"
            lazy-rules
            :rules="[(v: string) => !!v || 'Required']"
            class="login-input"
          >
            <template #append>
              <q-icon
                :name="showPassword ? 'visibility_off' : 'visibility'"
                class="cursor-pointer"
                size="18px"
                @click="showPassword = !showPassword"
              />
            </template>
          </q-input>

          <div v-if="error" class="login-error">{{ error }}</div>

          <q-btn
            type="submit"
            label="Sign In"
            color="primary"
            unelevated
            no-caps
            :loading="submitting"
            class="login-btn"
          />
        </q-form>

        <q-form v-else class="login-form" @submit.prevent="onPasswordChange">
          <div class="password-change-notice">You must change your password before continuing.</div>

          <q-input
            v-model="passwordForm.newPassword"
            :type="showNewPassword ? 'text' : 'password'"
            label="New Password"
            dense
            outlined
            color="primary"
            lazy-rules
            :rules="[
              (v: string) => !!v || 'Required',
              (v: string) => v.length >= 8 || 'Must be at least 8 characters',
            ]"
            class="login-input"
          >
            <template #append>
              <q-icon
                :name="showNewPassword ? 'visibility_off' : 'visibility'"
                class="cursor-pointer"
                size="18px"
                @click="showNewPassword = !showNewPassword"
              />
            </template>
          </q-input>

          <q-input
            v-model="passwordForm.confirmPassword"
            :type="showConfirmPassword ? 'text' : 'password'"
            label="Confirm Password"
            dense
            outlined
            color="primary"
            lazy-rules
            :rules="[
              (v: string) => !!v || 'Required',
              (v: string) => v === passwordForm.newPassword || 'Passwords do not match',
            ]"
            class="login-input"
          >
            <template #append>
              <q-icon
                :name="showConfirmPassword ? 'visibility_off' : 'visibility'"
                class="cursor-pointer"
                size="18px"
                @click="showConfirmPassword = !showConfirmPassword"
              />
            </template>
          </q-input>

          <div v-if="error" class="login-error">{{ error }}</div>

          <q-btn
            type="submit"
            label="Change Password"
            color="primary"
            unelevated
            no-caps
            :loading="submitting"
            class="login-btn"
          />
        </q-form>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue';
import { useRouter } from 'vue-router';
import { useMeta } from 'quasar';
import { useAuthStore } from 'stores/auth';
import { useBrandingStore } from 'stores/branding';

const router = useRouter();
const auth = useAuthStore();
const branding = useBrandingStore();

const productName = computed(() => branding.productName);
const logoUrl = computed(() => branding.effectiveLogoUrl);
const productInitial = computed(() => branding.productName.charAt(0).toUpperCase() || 'C');
const tagline = computed(
  () => branding.config.loginTagline?.trim() || 'Workflow & agent orchestration',
);

useMeta(() => ({ title: `Sign in · ${productName.value}` }));

const form = reactive({ email: '', password: '' });
const showPassword = ref(false);
const submitting = ref(false);
const error = ref('');

const showPasswordChange = ref(false);
const tempPassword = ref('');
const passwordForm = reactive({ newPassword: '', confirmPassword: '' });
const showNewPassword = ref(false);
const showConfirmPassword = ref(false);

async function onSubmit() {
  error.value = '';
  submitting.value = true;
  try {
    const user = await auth.login(form.email, form.password);
    if (user.mustChangePassword) {
      tempPassword.value = form.password;
      showPasswordChange.value = true;
    } else {
      await router.push('/');
    }
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : 'Invalid email or password.';
  } finally {
    submitting.value = false;
  }
}

async function onPasswordChange() {
  error.value = '';
  submitting.value = true;
  try {
    await auth.changePassword(
      tempPassword.value,
      passwordForm.newPassword,
      passwordForm.confirmPassword,
    );
    await router.push('/');
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : 'Failed to change password.';
  } finally {
    submitting.value = false;
  }
}
</script>

<style lang="scss" scoped>
.login-page {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100vh;
  background: var(--cr-bg-page);
  overflow: hidden;
}

.login-grid {
  position: absolute;
  inset: 0;
  background-image:
    linear-gradient(var(--cr-border-subtle) 1px, transparent 1px),
    linear-gradient(90deg, var(--cr-border-subtle) 1px, transparent 1px);
  background-size: 40px 40px;
  mask-image: radial-gradient(ellipse at center, black 0%, transparent 70%);
  -webkit-mask-image: radial-gradient(ellipse at center, black 0%, transparent 70%);
}

.login-center {
  position: relative;
  z-index: 1;
  width: 100%;
  max-width: 360px;
  padding: 24px;
}

.login-card {
  background: var(--cr-bg-surface);
  border: 1px solid var(--cr-border-subtle);
  padding: 32px 28px 28px;
}

.login-logo {
  display: flex;
  justify-content: center;
  align-items: center;
  width: 56px;
  height: 56px;
  margin: 0 auto 16px;
  border-radius: 50%;
  background: var(--q-primary);
  color: var(--cr-fg-on-brand);
  font-size: 26px;
  font-weight: 700;
  letter-spacing: 0.05em;
}

.login-logo-image {
  display: block;
  width: 56px;
  height: 56px;
  object-fit: contain;
  margin: 0 auto 16px;
  border-radius: 6px;
}

.login-title {
  text-align: center;
  font-size: 14px;
  font-weight: 700;
  letter-spacing: 0.2em;
  color: var(--cr-fg-primary);
  margin-bottom: 2px;
}

.login-subtitle {
  text-align: center;
  font-size: 11px;
  letter-spacing: 0.06em;
  color: var(--cr-fg-tertiary);
  margin-bottom: 28px;
}

.login-form {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.login-input {
  :deep(.q-field__control) {
    border-radius: 0;
  }
}

.password-change-notice {
  font-size: 12px;
  color: var(--cr-fg-secondary);
  text-align: center;
  padding: 8px 12px;
  margin-bottom: 12px;
  background: var(--cr-brand-tint-soft);
  border-left: 2px solid var(--q-primary);
}

.login-error {
  font-size: 12px;
  color: #e53935;
  text-align: center;
  padding: 4px 0;
}

.login-btn {
  margin-top: 8px;
  border-radius: 0;
  font-size: 13px;
  font-weight: 600;
  letter-spacing: 0.04em;
  height: 40px;
}
</style>
