import { defineStore, acceptHMRUpdate } from 'pinia';
import { computed, ref } from 'vue';
import { Auth } from 'src/api';

export interface AuthUser {
  userId: string;
  email: string;
  displayName: string;
  role: 'Admin' | 'User';
  mustChangePassword: boolean;
}

export const useAuthStore = defineStore('auth', () => {
  const user = ref<AuthUser | null>(null);
  // Starts true so route guards wait for /me to resolve before deciding.
  const isLoading = ref(true);

  const isAuthenticated = computed(() => user.value !== null);
  const isAdmin = computed(() => user.value?.role === 'Admin');
  const needsPasswordChange = computed(() => user.value?.mustChangePassword === true);

  function setUser(next: AuthUser) {
    user.value = next;
  }

  function clearUser() {
    user.value = null;
  }

  /** Boot-time call: try /api/auth/me; treat 401 as anonymous. */
  async function load() {
    try {
      const res = await Auth.getCurrentUser();
      const result = res.data?.result;
      if (result) {
        user.value = toAuthUser(result);
      } else {
        user.value = null;
      }
    } catch {
      user.value = null;
    } finally {
      isLoading.value = false;
    }
  }

  async function login(email: string, password: string) {
    const res = await Auth.login({ body: { email, password } });
    if (res.error) {
      throw new Error('Invalid email or password.');
    }
    const result = res.data?.result;
    if (!result) {
      throw new Error('Login failed.');
    }
    user.value = toAuthUser(result);
    return user.value;
  }

  async function logout() {
    try {
      await Auth.logout();
    } catch {
      // ignore — cookie may already be cleared
    }
    user.value = null;
  }

  async function changePassword(
    currentPassword: string,
    newPassword: string,
    confirmPassword: string,
  ) {
    const res = await Auth.changePassword({
      body: { currentPassword, newPassword, confirmPassword },
    });
    if (res.error) {
      throw new Error('Failed to change password.');
    }
    // Refresh from /me so mustChangePassword reflects server state.
    await load();
  }

  return {
    user,
    isLoading,
    isAuthenticated,
    isAdmin,
    needsPasswordChange,
    setUser,
    clearUser,
    load,
    login,
    logout,
    changePassword,
  };
});

function toAuthUser(r: {
  userId: string;
  email: string;
  displayName: string;
  role: string;
  mustChangePassword: boolean;
}): AuthUser {
  return {
    userId: r.userId,
    email: r.email,
    displayName: r.displayName,
    role: r.role === 'Admin' ? 'Admin' : 'User',
    mustChangePassword: r.mustChangePassword,
  };
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useAuthStore, import.meta.hot));
}
