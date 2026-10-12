import axios from 'axios'
import { ElMessage } from 'element-plus'

export const api = axios.create({
  baseURL: '/api/panel',
  timeout: 30_000,
  withCredentials: true,
})

// 每次获取当前身份的令牌，避免登录或修改用户名后复用旧身份令牌。
api.interceptors.request.use(async (config) => {
  const protectedPaths = ['/auth/login', '/settings/password', '/settings/username']
  if (config.method?.toLowerCase() === 'post' && protectedPaths.includes(config.url || '')) {
    const response = await api.get<{ token: string }>('/auth/csrf')
    config.headers.set('X-TelegramPanel-CSRF', response.data.token)
  }
  return config
})

/** 从 ASP.NET OperationResult/ProblemDetails/验证错误中提取可读消息。 */
export function extractApiErrorMessage(data: unknown): string {
  if (typeof data === 'string') return data.trim()
  if (!data || typeof data !== 'object') return ''

  const body = data as Record<string, unknown>
  for (const key of ['message', 'detail', 'error']) {
    const value = body[key]
    if (typeof value === 'string' && value.trim()) return value.trim()
  }

  const errors = body.errors
  if (errors && typeof errors === 'object') {
    const messages = Object.values(errors as Record<string, unknown>)
      .flatMap((value) => Array.isArray(value) ? value : [value])
      .filter((value): value is string => typeof value === 'string' && value.trim().length > 0)
      .map((value) => value.trim())
    if (messages.length > 0) return messages.join('；')
  }

  const title = body.title
  if (typeof title === 'string' && title.trim()) return title.trim()

  return ''
}

api.interceptors.response.use(
  (res) => res,
  (err) => {
    if (err?.response?.data?.code === 'PASSWORD_CHANGE_REQUIRED') {
      window.location.href = '/ui/admin/password'
      return Promise.reject(err)
    }
    if (err?.response?.status === 401 && window.location.pathname !== '/ui/login') {
      window.location.href = `/ui/login?redirect=${encodeURIComponent(window.location.pathname + window.location.search)}`
      return Promise.reject(err)
    }

    const message = extractApiErrorMessage(err?.response?.data) || err?.message
    const code = err?.response?.data?.code
    // 需要用户确认的风控响应由业务页面弹窗处理，不重复显示全局错误提示。
    if (message && code !== 'ACCOUNT_RISK_CONFIRMATION_REQUIRED') ElMessage.error(message)
    return Promise.reject(err)
  },
)
