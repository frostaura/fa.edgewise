import { createSlice, type PayloadAction } from '@reduxjs/toolkit'

import type { AuthTokens, User } from '@/api/types'
import { getRefreshToken, getStoredUser } from '@/lib/authStorage'

export type AuthStatus =
  /** A refresh token exists and we are exchanging it for a session. */
  'restoring' | 'authenticated' | 'guest'

export interface AuthState {
  status: AuthStatus
  user: User | null
  /** Access token, memory-only (never persisted). */
  accessToken: string | null
  /** Short-lived token handed out when login answers requiresTotp: true. */
  totpToken: string | null
}

export function getInitialAuthState(): AuthState {
  const hasRefreshToken = getRefreshToken() !== null
  return {
    status: hasRefreshToken ? 'restoring' : 'guest',
    user: hasRefreshToken ? getStoredUser() : null,
    accessToken: null,
    totpToken: null,
  }
}

export type SessionPayload = AuthTokens & { user?: User }

const authSlice = createSlice({
  name: 'auth',
  initialState: getInitialAuthState,
  reducers: {
    /** Tokens (and optionally the user) arrived — we are signed in. */
    sessionEstablished(state, action: PayloadAction<SessionPayload>) {
      state.status = 'authenticated'
      state.accessToken = action.payload.accessToken
      if (action.payload.user) state.user = action.payload.user
      state.totpToken = null
    },
    /** Login answered with a TOTP challenge. */
    totpRequired(state, action: PayloadAction<{ totpToken: string }>) {
      state.totpToken = action.payload.totpToken
    },
    /** Logout, refresh failure, or expired session. */
    sessionCleared(state) {
      state.status = 'guest'
      state.user = null
      state.accessToken = null
      state.totpToken = null
    },
  },
})

export const { sessionEstablished, totpRequired, sessionCleared } = authSlice.actions
export default authSlice.reducer

/* ------------------------------- selectors ------------------------------- */

interface WithAuth {
  auth: AuthState
}

export const selectAuthStatus = (state: WithAuth) => state.auth.status
export const selectIsAuthenticated = (state: WithAuth) => state.auth.status === 'authenticated'
export const selectCurrentUser = (state: WithAuth) => state.auth.user
export const selectAccessToken = (state: WithAuth) => state.auth.accessToken
export const selectTotpToken = (state: WithAuth) => state.auth.totpToken
