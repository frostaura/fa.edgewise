import { edgewiseApi } from '@/api/edgewiseApi'
import type {
  AuthSession,
  AuthTokens,
  LoginRequest,
  LoginResponse,
  TotpLoginRequest,
} from '@/api/types'
import { sessionCleared, sessionEstablished, totpRequired } from '@/features/auth/authSlice'
import { getRefreshToken } from '@/lib/authStorage'

/**
 * Auth endpoints, injected into the shared API slice (see edgewiseApi.ts for
 * the injectEndpoints pattern feature agents should follow).
 */
export const authApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    register: build.mutation<AuthSession, LoginRequest>({
      query: (body) => ({ url: '/auth/register', method: 'POST', body }),
      async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
        const { data } = await queryFulfilled
        dispatch(sessionEstablished(data))
      },
    }),

    login: build.mutation<LoginResponse, LoginRequest>({
      query: (body) => ({ url: '/auth/login', method: 'POST', body }),
      async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
        const { data } = await queryFulfilled
        if (data.requiresTotp) {
          dispatch(totpRequired({ totpToken: data.totpToken }))
        } else {
          dispatch(sessionEstablished(data))
        }
      },
    }),

    loginTotp: build.mutation<AuthSession, TotpLoginRequest>({
      query: (body) => ({ url: '/auth/login/totp', method: 'POST', body }),
      async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
        const { data } = await queryFulfilled
        dispatch(sessionEstablished(data))
      },
    }),

    /** Used on app start to turn a stored refresh token back into a session. */
    refresh: build.mutation<AuthTokens, { refreshToken: string }>({
      query: (body) => ({ url: '/auth/refresh', method: 'POST', body }),
      async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
        try {
          const { data } = await queryFulfilled
          dispatch(sessionEstablished(data))
        } catch {
          dispatch(sessionCleared())
        }
      },
    }),

    logout: build.mutation<void, void>({
      query: () => ({
        url: '/auth/logout',
        method: 'POST',
        body: { refreshToken: getRefreshToken() },
      }),
      async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
        // Clear the local session whether or not the server call succeeds.
        try {
          await queryFulfilled
        } finally {
          dispatch(sessionCleared())
          dispatch(edgewiseApi.util.resetApiState())
        }
      },
    }),
  }),
})

export const {
  useRegisterMutation,
  useLoginMutation,
  useLoginTotpMutation,
  useRefreshMutation,
  useLogoutMutation,
} = authApi
