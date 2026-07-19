import { beforeEach, describe, expect, it, vi } from 'vitest'

import { notificationsApi } from '@/api/notificationsApi'
import { makeStore } from '@/app/store'
import { sessionEstablished } from '@/features/auth/authSlice'

interface MockCall {
  url: string
  authorization: string | null
  body: unknown
}

/** Install a fetch mock that answers from a queue and records each request. */
function mockFetchQueue(responses: { status: number; json: unknown }[]) {
  const calls: MockCall[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = input instanceof Request ? input : new Request(input, init)
      const text = await request.clone().text()
      calls.push({
        url: new URL(request.url).pathname,
        authorization: request.headers.get('authorization'),
        body: text ? JSON.parse(text) : null,
      })
      const next = responses.shift() ?? {
        status: 500,
        json: { error: { code: 'exhausted', message: 'no more mocked responses' } },
      }
      return new Response(JSON.stringify(next.json), {
        status: next.status,
        headers: { 'content-type': 'application/json' },
      })
    }),
  )
  return calls
}

describe('baseQueryWithReauth (401 → refresh → retry once → logout)', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.unstubAllGlobals()
  })

  it('refreshes the token on 401, retries the request and stores the new tokens', async () => {
    localStorage.setItem('edgewise.refreshToken', 'old-refresh')
    const store = makeStore()
    store.dispatch(
      sessionEstablished({ accessToken: 'expired-access', refreshToken: 'old-refresh' }),
    )

    const calls = mockFetchQueue([
      { status: 401, json: { error: { code: 'token_expired', message: 'Access token expired' } } },
      { status: 200, json: { accessToken: 'new-access', refreshToken: 'new-refresh' } },
      { status: 200, json: [{ id: 'n1', title: 'Alert hit' }] },
    ])

    const result = await store.dispatch(
      notificationsApi.endpoints.getNotifications.initiate({ unread: true }),
    )

    // Original call → refresh → retried call.
    expect(calls.map((c) => c.url)).toEqual([
      '/api/notifications',
      '/api/auth/refresh',
      '/api/notifications',
    ])
    // Refresh sent the stored refresh token.
    expect(calls[1].body).toEqual({ refreshToken: 'old-refresh' })
    // Retry used the fresh access token.
    expect(calls[2].authorization).toBe('Bearer new-access')

    // The retried data made it through.
    expect(result.data).toEqual([{ id: 'n1', title: 'Alert hit' }])

    // New tokens landed in the store and localStorage.
    expect(store.getState().auth.accessToken).toBe('new-access')
    expect(store.getState().auth.status).toBe('authenticated')
    expect(localStorage.getItem('edgewise.refreshToken')).toBe('new-refresh')
  })

  it('logs out when the refresh itself fails', async () => {
    localStorage.setItem('edgewise.refreshToken', 'stale-refresh')
    const store = makeStore()
    store.dispatch(
      sessionEstablished({ accessToken: 'expired-access', refreshToken: 'stale-refresh' }),
    )

    const calls = mockFetchQueue([
      { status: 401, json: { error: { code: 'token_expired', message: 'Access token expired' } } },
      {
        status: 401,
        json: { error: { code: 'invalid_refresh', message: 'Refresh token invalid' } },
      },
    ])

    const result = await store.dispatch(
      notificationsApi.endpoints.getNotifications.initiate({ unread: true }),
    )

    expect(calls.map((c) => c.url)).toEqual(['/api/notifications', '/api/auth/refresh'])
    expect(result.error).toBeDefined()
    expect(store.getState().auth.status).toBe('guest')
    expect(store.getState().auth.accessToken).toBeNull()
    expect(localStorage.getItem('edgewise.refreshToken')).toBeNull()
  })

  it('does not attempt a refresh when no refresh token is stored', async () => {
    const store = makeStore()

    const calls = mockFetchQueue([
      { status: 401, json: { error: { code: 'unauthorized', message: 'Missing token' } } },
    ])

    const result = await store.dispatch(
      notificationsApi.endpoints.getNotifications.initiate({ unread: true }),
    )

    expect(calls.map((c) => c.url)).toEqual(['/api/notifications'])
    expect(result.error).toBeDefined()
    expect(store.getState().auth.status).toBe('guest')
  })
})
