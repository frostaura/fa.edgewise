import type { User } from '@/api/types'

/**
 * Persistence for the auth session. The ACCESS token lives only in Redux
 * memory; only the refresh token (plus the non-sensitive user profile, for
 * instant rehydration on reload) is stored in localStorage.
 */
const REFRESH_TOKEN_KEY = 'edgewise.refreshToken'
const USER_KEY = 'edgewise.user'

export function getRefreshToken(): string | null {
  try {
    return localStorage.getItem(REFRESH_TOKEN_KEY)
  } catch {
    return null
  }
}

export function setRefreshToken(token: string): void {
  try {
    localStorage.setItem(REFRESH_TOKEN_KEY, token)
  } catch {
    /* storage unavailable — session will not survive reload */
  }
}

export function getStoredUser(): User | null {
  try {
    const raw = localStorage.getItem(USER_KEY)
    return raw ? (JSON.parse(raw) as User) : null
  } catch {
    return null
  }
}

export function setStoredUser(user: User): void {
  try {
    localStorage.setItem(USER_KEY, JSON.stringify(user))
  } catch {
    /* no-op */
  }
}

export function clearAuthStorage(): void {
  try {
    localStorage.removeItem(REFRESH_TOKEN_KEY)
    localStorage.removeItem(USER_KEY)
  } catch {
    /* no-op */
  }
}
