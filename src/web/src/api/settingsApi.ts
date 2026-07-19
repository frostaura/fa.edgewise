import { edgewiseApi } from '@/api/edgewiseApi'
import type { User } from '@/api/types'

// ---------------------------------------------------------------------------
// Settings surface API — /me profile, password, TOTP enrolment and personal
// access tokens.
// ---------------------------------------------------------------------------

/** Full /api/me payload (the shared User type only carries the auth-relevant subset). */
export interface MeUser extends User {
  timezone: string
  llmOptOut: boolean
  settingsJson?: string | null
}

export interface UpdateMeRequest {
  baseCurrency?: string
  timezone?: string
  llmOptOut?: boolean
  settingsJson?: string
}

export interface ChangePasswordRequest {
  currentPassword: string
  newPassword: string
}

export interface TotpSetupResponse {
  secret: string
  otpauthUri: string
}

export interface ApiTokenItem {
  id: string
  name: string
  createdAt: string
  lastUsedAt?: string | null
}

export interface CreatedApiToken {
  id: string
  name: string
  /** Shown exactly once — never returned again. */
  token: string
  createdAt: string
}

export const settingsApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    getMe: build.query<MeUser, void>({
      query: () => '/me/',
      providesTags: ['Me'],
    }),
    updateMe: build.mutation<MeUser, UpdateMeRequest>({
      query: (body) => ({ url: '/me/', method: 'PATCH', body }),
      invalidatesTags: ['Me'],
    }),
    changePassword: build.mutation<void, ChangePasswordRequest>({
      query: (body) => ({ url: '/me/password', method: 'POST', body }),
    }),

    // ------------------------------------------------------------------ TOTP
    totpSetup: build.mutation<TotpSetupResponse, void>({
      query: () => ({ url: '/auth/totp/setup', method: 'POST', body: {} }),
    }),
    totpEnable: build.mutation<{ recoveryCodes: string[] }, { code: string }>({
      query: (body) => ({ url: '/auth/totp/enable', method: 'POST', body }),
      invalidatesTags: ['Me'],
    }),
    totpDisable: build.mutation<void, { code: string }>({
      query: (body) => ({ url: '/auth/totp/disable', method: 'POST', body }),
      invalidatesTags: ['Me'],
    }),

    // ------------------------------------------------------------------- PATs
    getApiTokens: build.query<ApiTokenItem[], void>({
      query: () => '/me/tokens',
      providesTags: ['ApiToken'],
    }),
    createApiToken: build.mutation<CreatedApiToken, { name: string }>({
      query: (body) => ({ url: '/me/tokens', method: 'POST', body }),
      invalidatesTags: ['ApiToken'],
    }),
    deleteApiToken: build.mutation<void, string>({
      query: (id) => ({ url: `/me/tokens/${id}`, method: 'DELETE' }),
      invalidatesTags: ['ApiToken'],
    }),
  }),
})

export const {
  useGetMeQuery,
  useUpdateMeMutation,
  useChangePasswordMutation,
  useTotpSetupMutation,
  useTotpEnableMutation,
  useTotpDisableMutation,
  useGetApiTokensQuery,
  useCreateApiTokenMutation,
  useDeleteApiTokenMutation,
} = settingsApi
