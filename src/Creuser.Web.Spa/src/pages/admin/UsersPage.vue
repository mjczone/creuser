<template>
  <q-page class="q-pa-lg">
    <div class="row items-baseline q-gutter-md q-mb-md">
      <h1 class="text-h5 q-ma-none">Users</h1>
      <span class="text-caption text-grey-6">Invite-only — admin creates accounts</span>
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
      <template #body-cell-mustChangePassword="props">
        <q-td :props="props">
          <q-chip
            v-if="props.row.mustChangePassword"
            dense
            outline
            color="warning"
            text-color="warning"
            >Pending first login</q-chip
          >
          <span v-else class="text-grey-6">—</span>
        </q-td>
      </template>
    </q-table>

    <q-dialog v-model="showCreate" persistent>
      <q-card style="min-width: 420px">
        <q-card-section>
          <div class="text-h6">Invite user</div>
          <div class="text-caption text-grey-6">
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
              <q-btn flat label="Cancel" color="grey-7" no-caps @click="reset" />
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

    <q-dialog v-model="showResult" persistent>
      <q-card style="min-width: 420px">
        <q-card-section>
          <div class="text-h6">User created</div>
          <div class="text-caption text-grey-6">
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
                @click="copyTemp"
                :aria-label="'Copy temp password'"
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

const $q = useQuasar();

interface NewUser {
  email: string;
  displayName: string;
  role: 'Admin' | 'User';
  temporaryPassword: string;
}

const users = ref<UserResult[]>([]);
const loading = ref(false);
const showCreate = ref(false);
const showResult = ref(false);
const created = ref<CreateUserResult | null>(null);
const submitting = ref(false);
const error = ref('');

const form = reactive<NewUser>({
  email: '',
  displayName: '',
  role: 'User',
  temporaryPassword: '',
});

const cols: QTableColumn<UserResult>[] = [
  { name: 'email', label: 'Email', field: 'email', align: 'left', sortable: true },
  {
    name: 'displayName',
    label: 'Name',
    field: 'displayName',
    align: 'left',
    sortable: true,
  },
  { name: 'role', label: 'Role', field: 'role', align: 'left', sortable: true },
  {
    name: 'mustChangePassword',
    label: 'Status',
    field: 'mustChangePassword',
    align: 'left',
  },
];

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
      error.value = 'Failed to create user.';
      return;
    }
    created.value = res.data.result;
    showCreate.value = false;
    showResult.value = true;
    void load();
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : 'Failed to create user.';
  } finally {
    submitting.value = false;
  }
}

function reset() {
  form.email = '';
  form.displayName = '';
  form.role = 'User';
  form.temporaryPassword = '';
  error.value = '';
  showCreate.value = false;
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

onMounted(() => void load());
</script>

<style lang="scss" scoped>
.cr-temp-pw :deep(input) {
  font-family: 'Roboto Mono', ui-monospace, monospace;
  font-weight: 600;
  letter-spacing: 0.04em;
}
</style>
