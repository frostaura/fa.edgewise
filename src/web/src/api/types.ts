/** Shared API contract types (JSON camelCase, Bearer auth). */

export interface User {
  id: string
  email: string
  baseCurrency: string
  totpEnabled: boolean
  isAdmin: boolean
  createdAt: string
}

export interface AuthTokens {
  accessToken: string
  refreshToken: string
}

export interface AuthSession extends AuthTokens {
  user: User
}

/** POST /api/auth/login — either a full session or a TOTP challenge. */
export type LoginResponse =
  | ({ requiresTotp: false } & AuthSession)
  | { requiresTotp: true; totpToken: string }

export interface LoginRequest {
  email: string
  password: string
}

export interface TotpLoginRequest {
  totpToken: string
  code: string
}

/** Error envelope returned by the backend for all failures. */
export interface ApiErrorBody {
  error: {
    code: string
    message: string
  }
}

export interface NotificationItem {
  id: string
  title?: string
  message?: string
  createdAt?: string
  readAt?: string | null
}

/** Extract a human-readable message from an RTK Query error. */
export function getApiErrorMessage(err: unknown, fallback = 'Something went wrong'): string {
  if (err && typeof err === 'object') {
    const data = (err as { data?: unknown }).data
    if (data && typeof data === 'object') {
      const inner = (data as Partial<ApiErrorBody>).error
      if (inner?.message) return inner.message
    }
    const status = (err as { status?: unknown }).status
    if (status === 'FETCH_ERROR') return 'Cannot reach the server. Check your connection.'
    if (status === 'TIMEOUT_ERROR') return 'The server took too long to respond.'
  }
  return fallback
}
