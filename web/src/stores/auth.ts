import { defineStore } from 'pinia'
import { authApi } from '@/api'
import { TOKEN_KEY, USER_KEY } from '@/api/http'
import type { LoginResult } from '@/api/types'

interface StoredUser { userId: number; username: string; displayName: string; role: string; roleLevel: number }

function readUser(): StoredUser | null {
  try {
    const raw = localStorage.getItem(USER_KEY)
    return raw ? JSON.parse(raw) as StoredUser : null
  } catch { return null }
}

export const useAuthStore = defineStore('auth', {
  state: () => ({
    token: localStorage.getItem(TOKEN_KEY) || '',
    user: readUser()
  }),

  getters: {
    isLoggedIn: (s) => !!s.token,
    role: (s) => s.user?.role ?? 'Observer',
    level: (s) => s.user?.roleLevel ?? 0,
    canWrite: (s) => (s.user?.roleLevel ?? 0) >= 2,
    canConfig: (s) => (s.user?.roleLevel ?? 0) >= 3,
    canAdmin: (s) => (s.user?.roleLevel ?? 0) >= 4,
    displayName: (s) => s.user?.displayName || s.user?.username || ''
  },

  actions: {
    async login(username: string, password: string) {
      const res = await authApi.login(username, password) as { ok: boolean; data: LoginResult; error?: string }
      if (!res?.ok) throw new Error(res?.error || '登录失败')
      const d = res.data
      this.token = d.token
      this.user = {
        userId: d.userId, username: d.username, displayName: d.displayName,
        role: d.role, roleLevel: d.roleLevel
      }
      localStorage.setItem(TOKEN_KEY, d.token)
      localStorage.setItem(USER_KEY, JSON.stringify(this.user))
      return d
    },

    logout() {
      this.token = ''
      this.user = null
      localStorage.removeItem(TOKEN_KEY)
      localStorage.removeItem(USER_KEY)
    },

    /** 刷新角色（管理员改权限后无需重新登录即可感知）。 */
    async refreshMe() {
      try {
        const me = await authApi.me()
        this.user = {
          userId: me.id, username: me.username, displayName: me.displayName,
          role: me.role, roleLevel: me.roleLevel
        }
        localStorage.setItem(USER_KEY, JSON.stringify(this.user))
      } catch { /* 令牌失效由 http 拦截器处理 */ }
    }
  }
})
