import { fetchBaseQuery } from '@reduxjs/toolkit/query/react'
import type { BaseQueryFn, FetchArgs, FetchBaseQueryError } from '@reduxjs/toolkit/query'

import type { AuthTokens } from '@/api/types'
import { sessionCleared, sessionEstablished, type AuthState } from '@/features/auth/authSlice'
import { getRefreshToken } from '@/lib/authStorage'

/**
 * Base query for every Edgewise endpoint:
 *  - relative "/api" base URL (the Vite dev server proxies it to the backend)
 *  - attaches the in-memory access token as a Bearer header
 *  - on 401: exchanges the stored refresh token for new tokens, retries the
 *    original request ONCE, and logs out if the refresh fails.
 */
const rawBaseQuery = fetchBaseQuery({
  baseUrl: '/api',
  prepareHeaders: (headers, { getState }) => {
    const token = (getState() as { auth: AuthState }).auth.accessToken
    if (token) headers.set('authorization', `Bearer ${token}`)
    return headers
  },
})

/** Auth endpoints must never trigger the reauth loop themselves. */
function isAuthUrl(args: string | FetchArgs): boolean {
  const url = typeof args === 'string' ? args : args.url
  return url.startsWith('/auth/') || url.startsWith('auth/')
}

/** Deduplicates concurrent refresh attempts across parallel 401s. */
let refreshPromise: Promise<Awaited<ReturnType<typeof rawBaseQuery>>> | null = null

export const baseQueryWithReauth: BaseQueryFn<
  string | FetchArgs,
  unknown,
  FetchBaseQueryError
> = async (args, api, extraOptions) => {
  let result = await rawBaseQuery(args, api, extraOptions)

  if (result.error?.status === 401 && !isAuthUrl(args)) {
    const refreshToken = getRefreshToken()
    if (!refreshToken) {
      api.dispatch(sessionCleared())
      return result
    }

    if (!refreshPromise) {
      refreshPromise = (async () => {
        try {
          return await rawBaseQuery(
            { url: '/auth/refresh', method: 'POST', body: { refreshToken } },
            api,
            extraOptions,
          )
        } finally {
          refreshPromise = null
        }
      })()
    }
    const refreshResult = await refreshPromise

    if (refreshResult.data) {
      api.dispatch(sessionEstablished(refreshResult.data as AuthTokens))
      // Retry the original request once with the fresh access token.
      result = await rawBaseQuery(args, api, extraOptions)
    } else {
      api.dispatch(sessionCleared())
    }
  }

  return result
}
