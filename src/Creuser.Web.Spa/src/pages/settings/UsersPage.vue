<template>
  <q-page class="q-pa-lg">
    <div class="row items-baseline q-gutter-md q-mb-md">
      <h1 class="text-h5 q-ma-none">Users</h1>
      <span class="text-caption" :style="{ color: 'var(--cr-fg-secondary)' }">
        Invite-only — admin creates accounts
      </span>
    </div>

    <div class="row q-mb-lg">
      <q-btn
        color="primary"
        unelevated
        no-caps
        icon="person_add"
        label="Invite user"
        @click="showCreate = true"
      />
    </div>

    <q-table
      :rows="users"
      :columns="cols"
      row-key="userId"
      :loading="loading"
      flat
      bordered
      dense
    >
      <template #body-cell-role="props">
        <q-td :props="props">
          <q-chip
            dense
            outline
            :color="props.row.role === 'Admin' ? 'primary' : 'grey-7'"
            :text-color="props.row.role === 'Admin' ? 'primary' : 'grey-7'"
          >
            {{ props.row.role }}
          </q-chip>
        </q-td>
      </template>

      <template #body-cell-status="props">
        <q-td :props="props">
          <div class="cr-status-cell">
            <q-chip
              v-if="!props.row.isActive"
              dense
              outline
              color="negative"
              text-color="negative"
            >
              Inactive
            </q-chip>
            <q-chip
              v-if="props.row.mustChangePassword"
              dense
              outline
              color="warning"
              text-color="warning"
            >
              Pending first login
            </q-chip>
            <span
              v-if="props.row.isActive && !props.row.mustChangePassword"
              :style="{ color: 'var(--cr-fg-tertiary)' }"
            >
              —
            </span>
          </div>
        </q-td>
      </template>

      <template #body-cell-lastLoginAt="props">
        <q-td :props="props">
          <span :style="{ color: 'var(--cr-fg-secondary)' }">
            {{ formatLastLogin(props.row.lastLoginAt) }}
          </span>
        </q-td>
      </template>

      <template #body-cell-actions="props">
        <q-td :props="props" auto-width>
          <q-btn
            flat
            dense
            round
            icon="more_vert"
            :aria-label="`Actions for ${props.row.email}`"
          >
            <q-menu auto-close>
              <q-list dense style="min-width: 200px">
                <q-item
                  clickable
                  :disable="isSelf(props.row)"
                  @click="onResetPassword(props.row)"
                >
                  <q-item-section avatar>
                    <q-icon name="lock_reset" size="18px" />
                  </q-item-section>
                  <q-item-section>Reset password</q-item-section>
                </q-item>

                <q-item
                  clickable
                  :disable="isSelf(props.row)"
                  @click="onToggleRole(props.row)"
                >
                  <q-item-section avatar>
                    <q-icon
                      :name="props.row.role === 'Admin' ? 'person' : 'admin_panel_settings'"
                      size="18px"
                    />
                  </q-item-section>
                  <q-item-section>
                    {{ props.row.role === 'Admin' ? 'Demote to User' : 'Promote to Admin' }}
                  </q-item-section>
                </q-item>

                <q-item
                  clickable
                  :disable="isSelf(props.row)"
                  @click="onToggleActive(props.row)"
                >
                  <q-item-section avatar>
                    <q-icon
                      :name="props.row.isActive ? 'person_off' : 'person'"
                      size="18px"
                    />
                  </q-item-section>
                  <q-item-section>
                    {{ props.row.isActive ? 'Deactivate' : 'Activate' }}
                  </q-item-section>
                </q-item>

                <q-separator />

                <q-item
                  clickable
                  :disable="isSelf(props.row)"
                  class="cr-action-danger"
                  @click="onDelete(props.row)"
                >
                  <q-item-section avatar>
                    <q-icon name="delete_forever" size="18px" />
                  </q-item-section>
                  <q-item-section>Delete</q-item-section>
                </q-item>
              </q-list>
            </q-menu>
          </q-btn>
        </q-td>
      </template>
    </q-table>

    <!-- Invite -->
    <q-dialog v-model="showCreate" persistent>
      <q-card style="min-width: 420px">
        <q-card-section>
          <div class="text-h6">Invite user</div>
          <div class="text-caption" :style="{ color: 'var(--cr-fg-secondary)' }">
            A temporary password is generated (or you can supply one). It will be shown once —
            send it to the user out of band.
          </div>
        </q-card-section>
        <q-card-section>
          <q-form class="q-gutter-md" @submit.prevent="onCreate">
            <q-input v-model="form.email" type="email" label="Email" outlined dense />
            <q-input v-model="form.displayName" label="Display name" outlined dense />
            <q-select
              v-model="form.role"
              :options="['User', 'Admin']"
              label="Role"
              outlined
              dense
            />
            <q-input
              v-model="form.temporaryPassword"
              label="Temporary password (optional — leave blank to auto-generate)"
              outlined
              dense
              hint="At least 8 characters when supplied"
            />
            <div v-if="error" class="text-negative text-caption">{{ error }}</div>
            <div class="row justify-end q-gutter-sm">
              <q-btn flat label="Cancel" no-caps @click="resetCreate" />
              <q-btn
                type="submit"
                label="Create"
                color="primary"
                unelevated
                no-caps
                :loading="submitting"
              />
            </div>
          </q-form>
        </q-card-section>
      </q-card>
    </q-dialog>

    <!-- Reset password -->
    <q-dialog v-model="showReset" persistent>
      <q-card style="min-width: 420px">
        <q-card-section>
          <div class="text-h6">Reset password</div>
          <div class="text-caption" :style="{ color: 'var(--cr-fg-secondary)' }">
            Reset the password for <strong>{{ resetTarget?.email }}</strong
            >. The user will be forced to change it on next sign-in. The new temp password is
            shown once after submit.
          </div>
        </q-card-section>
        <q-card-section>
          <q-form class="q-gutter-md" @submit.prevent="submitReset">
            <q-input
              v-model="resetForm.temporaryPassword"
              label="Temporary password (optional — leave blank to auto-generate)"
              outlined
              dense
              hint="At least 8 characters when supplied"
            />
            <div v-if="error" class="text-negative text-caption">{{ error }}</div>
            <div class="row justify-end q-gutter-sm">
              <q-btn flat label="Cancel" no-caps @click="resetResetForm" />
              <q-btn
                type="submit"
                label="Reset"
                color="primary"
                unelevated
                no-caps
                :loading="submitting"
              />
            </div>
          </q-form>
        </q-card-section>
      </q-card>
    </q-dialog>

    <!-- Generated temp password (one-time view) -->
    <q-dialog v-model="showResult" persistent>
      <q-card style="min-width: 420px">
        <q-card-section>
          <div class="text-h6">{{ resultTitle }}</div>
          <div class="text-caption" :style="{ color: 'var(--cr-fg-secondary)' }">
            Send this temporary password to <b>{{ created?.email }}</b> directly. They will be
            forced to change it on first login. This is the only time it will be shown.
          </div>
        </q-card-section>
        <q-card-section>
          <q-input
            :model-value="created?.temporaryPassword"
            readonly
            outlined
            dense
            class="cr-temp-pw"
          >
            <template #append>
              <q-btn
                flat
                dense
                icon="content_copy"
                :aria-label="'Copy temp password'"
                @click="copyTemp"
              />
            </template>
          </q-input>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat label="Done" color="primary" no-caps @click="closeResult" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </q-page>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { useQuasar, type QTableColumn } from 'quasar';
import { Admin, type CreateUserResult, type UserResult } from 'src/api';
import { useAuthStore } from 'stores/auth';

const $q = useQuasar();
const auth = useAuthStore();

interface NewUser {
  email: string;
  displayName: string;
  role: 'Admin' | 'User';
  temporaryPassword: string;
}

const users = ref<UserResult[]>([]);
const loading = ref(false);

// Invite-user dialog
const showCreate = ref(false);
const submitting = ref(false);
const error = ref('');
const form = reactive<NewUser>({
  email: '',
  displayName: '',
  role: 'User',
  temporaryPassword: '',
});

// Reset-password dialog
const showReset = ref(false);
const resetTarget = ref<UserResult | null>(null);
const resetForm = reactive({ temporaryPassword: '' });

// Generated-temp-password result dialog (shared by create + reset)
const showResult = ref(false);
const created = ref<CreateUserResult | null>(null);
const resultTitle = ref('User created');

const cols: QTableColumn<UserResult>[] = [
  { name: 'email', label: 'Email', field: 'email', align: 'left', sortable: true },
  { name: 'displayName', label: 'Name', field: 'displayName', align: 'left', sortable: true },
  { name: 'role', label: 'Role', field: 'role', align: 'left', sortable: true },
  { name: 'status', label: 'Status', field: 'mustChangePassword', align: 'left' },
  {
    name: 'lastLoginAt',
    label: 'Last login',
    field: 'lastLoginAt',
    align: 'left',
    sortable: true,
  },
  { name: 'actions', label: '', field: () => '', align: 'right' },
];

function isSelf(row: UserResult): boolean {
  return auth.user?.userId === row.userId;
}

function formatLastLogin(when: string | null | undefined): string {
  if (!when) return 'Never';
  const d = new Date(when);
  if (Number.isNaN(d.getTime())) return '—';
  // Short relative format for recent, full date otherwise.
  const diffMs = Date.now() - d.getTime();
  const diffMins = Math.round(diffMs / 60000);
  if (diffMins < 1) return 'Just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  const diffHours = Math.round(diffMins / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  return d.toLocaleDateString();
}

async function load() {
  loading.value = true;
  try {
    const res = await Admin.listUsers();
    users.value = res.data?.result ?? [];
  } finally {
    loading.value = false;
  }
}

async function onCreate() {
  error.value = '';
  submitting.value = true;
  try {
    const res = await Admin.createUser({
      body: {
        email: form.email,
        displayName: form.displayName,
        role: form.role,
        temporaryPassword: form.temporaryPassword || null,
      },
    });
    if (res.error || !res.data?.result) {
      error.value = problemMessage(res.error) ?? 'Failed to create user.';
      return;
    }
    created.value = res.data.result;
    resultTitle.value = 'User created';
    showCreate.value = false;
    showResult.value = true;
    void load();
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : 'Failed to create user.';
  } finally {
    submitting.value = false;
  }
}

function resetCreate() {
  form.email = '';
  form.displayName = '';
  form.role = 'User';
  form.temporaryPassword = '';
  error.value = '';
  showCreate.value = false;
}

function onResetPassword(user: UserResult) {
  resetTarget.value = user;
  resetForm.temporaryPassword = '';
  error.value = '';
  showReset.value = true;
}

async function submitReset() {
  if (!resetTarget.value) return;
  error.value = '';
  submitting.value = true;
  try {
    const res = await Admin.resetUserPassword({
      path: { id: resetTarget.value.userId },
      body: { temporaryPassword: resetForm.temporaryPassword || null },
    });
    if (res.error || !res.data?.result) {
      error.value = problemMessage(res.error) ?? 'Failed to reset password.';
      return;
    }
    created.value = res.data.result;
    resultTitle.value = 'Password reset';
    showReset.value = false;
    showResult.value = true;
    void load();
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : 'Failed to reset password.';
  } finally {
    submitting.value = false;
  }
}

function resetResetForm() {
  resetForm.temporaryPassword = '';
  error.value = '';
  showReset.value = false;
}

function onToggleRole(user: UserResult) {
  const nextRole = user.role === 'Admin' ? 'User' : 'Admin';
  $q
    .dialog({
      title: nextRole === 'Admin' ? 'Promote to Admin?' : 'Demote to User?',
      message:
        nextRole === 'Admin'
          ? `Grant ${user.email} full admin access to platform settings, users, and workspaces.`
          : `Remove admin privileges from ${user.email}. They'll only see workspaces they're explicitly granted membership to.`,
      ok: { label: nextRole === 'Admin' ? 'Promote' : 'Demote', color: 'primary', unelevated: true, noCaps: true },
      cancel: { flat: true, noCaps: true },
      persistent: true,
    })
    // onOk's callback runs synchronously from Quasar's perspective; we kick
    // off the async work and let any thrown errors surface via the
    // try/catch + $q.notify below.
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
    .onOk(async () => {
      try {
        const res = await Admin.setUserRole({
          path: { id: user.userId },
          body: { role: nextRole },
        });
        if (res.error) {
          $q.notify({
            type: 'negative',
            message: problemMessage(res.error) ?? 'Failed to change role.',
            position: 'top',
          });
          return;
        }
        $q.notify({
          type: 'positive',
          message: `${user.email} is now ${nextRole}.`,
          position: 'top',
        });
        void load();
      } catch (e) {
        $q.notify({
          type: 'negative',
          message: e instanceof Error ? e.message : 'Failed to change role.',
          position: 'top',
        });
      }
    });
}

function onToggleActive(user: UserResult) {
  const nextActive = !user.isActive;
  $q
    .dialog({
      title: nextActive ? 'Activate user?' : 'Deactivate user?',
      message: nextActive
        ? `Re-enable sign-in for ${user.email}.`
        : `Block sign-in for ${user.email}. Existing sessions stay active until they expire (14 days). The account is preserved and can be re-activated.`,
      ok: {
        label: nextActive ? 'Activate' : 'Deactivate',
        color: nextActive ? 'primary' : 'negative',
        unelevated: true,
        noCaps: true,
      },
      cancel: { flat: true, noCaps: true },
      persistent: true,
    })
    // onOk's callback runs synchronously from Quasar's perspective; we kick
    // off the async work and let any thrown errors surface via the
    // try/catch + $q.notify below.
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
    .onOk(async () => {
      try {
        const res = await Admin.setUserActive({
          path: { id: user.userId },
          body: { isActive: nextActive },
        });
        if (res.error) {
          $q.notify({
            type: 'negative',
            message: problemMessage(res.error) ?? 'Failed to update active state.',
            position: 'top',
          });
          return;
        }
        $q.notify({
          type: 'positive',
          message: `${user.email} ${nextActive ? 'activated' : 'deactivated'}.`,
          position: 'top',
        });
        void load();
      } catch (e) {
        $q.notify({
          type: 'negative',
          message: e instanceof Error ? e.message : 'Failed to update active state.',
          position: 'top',
        });
      }
    });
}

function onDelete(user: UserResult) {
  $q
    .dialog({
      title: 'Permanently delete user?',
      message:
        `<p><strong>This is irreversible.</strong> ${user.email}'s account will be removed ` +
        `from the database, along with their workspace memberships.</p>` +
        `<p>For most cases — "user left the team" — <strong>Deactivate</strong> is safer ` +
        `(it preserves their audit trail).</p>`,
      html: true,
      ok: {
        label: 'Delete forever',
        color: 'negative',
        unelevated: true,
        noCaps: true,
      },
      cancel: { flat: true, noCaps: true, label: 'Cancel' },
      persistent: true,
    })
    // onOk's callback runs synchronously from Quasar's perspective; we kick
    // off the async work and let any thrown errors surface via the
    // try/catch + $q.notify below.
    // eslint-disable-next-line @typescript-eslint/no-misused-promises
    .onOk(async () => {
      try {
        const res = await Admin.deleteUser({ path: { id: user.userId } });
        if (res.error) {
          $q.notify({
            type: 'negative',
            message: problemMessage(res.error) ?? 'Failed to delete user.',
            position: 'top',
          });
          return;
        }
        $q.notify({
          type: 'positive',
          message: `${user.email} deleted.`,
          position: 'top',
        });
        void load();
      } catch (e) {
        $q.notify({
          type: 'negative',
          message: e instanceof Error ? e.message : 'Failed to delete user.',
          position: 'top',
        });
      }
    });
}

function closeResult() {
  showResult.value = false;
  created.value = null;
}

async function copyTemp() {
  if (!created.value?.temporaryPassword) return;
  try {
    await navigator.clipboard.writeText(created.value.temporaryPassword);
    $q.notify({ message: 'Copied', color: 'positive', timeout: 1200 });
  } catch {
    $q.notify({ message: 'Copy failed', color: 'negative' });
  }
}

/**
 * Extract a human message from a ProblemDetails response. Surfaces the
 * server's `detail` (e.g. "You can't demote the last remaining active
 * admin") so the admin sees the actual reason their action was rejected.
 */
function problemMessage(err: unknown): string | undefined {
  if (err && typeof err === 'object') {
    const e = err as { detail?: unknown; title?: unknown };
    if (typeof e.detail === 'string' && e.detail.length) return e.detail;
    if (typeof e.title === 'string' && e.title.length) return e.title;
  }
  return undefined;
}

onMounted(() => void load());
</script>

<style lang="scss" scoped>
.cr-temp-pw :deep(input) {
  font-family: var(--cr-font-family-mono);
  font-weight: 600;
  letter-spacing: 0.04em;
}

.cr-status-cell {
  display: flex;
  gap: 4px;
  flex-wrap: wrap;
}

.cr-action-danger {
  color: var(--q-negative);

  .q-icon {
    color: var(--q-negative);
  }
}
</style>
