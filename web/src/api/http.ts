import axios, { type AxiosInstance } from 'axios'
import { ElMessage } from 'element-plus'

export const TOKEN_KEY = 'sfms.token'
export const USER_KEY = 'sfms.user'

const http: AxiosInstance = axios.create({
  baseURL: '/api',
  timeout: 30000
})

http.interceptors.request.use((cfg) => {
  const token = localStorage.getItem(TOKEN_KEY)
  if (token) cfg.headers.Authorization = `Bearer ${token}`
  return cfg
})

http.interceptors.response.use(
  (res) => res,
  (err) => {
    const status = err.response?.status
    const body: any = err.response?.data
    // 后端统一把 { ok:false, error } 或 ProblemDetails 返回
    const msg: string =
      body?.error ?? body?.title ?? (typeof body === 'string' ? body : err.message) ?? '请求失败'

    if (status === 401) {
      localStorage.removeItem(TOKEN_KEY)
      localStorage.removeItem(USER_KEY)
      if (!location.hash.includes('/login')) location.hash = '#/login'
      ElMessage.error('登录已失效，请重新登录')
    } else if (status === 403) {
      ElMessage.error(msg || '权限不足')
    } else if (status === 423) {
      ElMessage.warning(msg)
    } else if (status === 400 || status === 404) {
      ElMessage.warning(msg)
    } else if (status >= 500) {
      ElMessage.error('服务器错误：' + msg)
    }
    return Promise.reject(new Error(msg))
  }
)

export default http

/** 从 ApiResult 包装里取 data，失败则抛出 error。 */
export function unwrap<T>(r: { data: { ok: boolean; data: T; error?: string } }): T {
  if (!r.data.ok) throw new Error(r.data.error || '操作失败')
  return r.data.data
}
