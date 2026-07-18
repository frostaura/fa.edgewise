import { createApi } from '@reduxjs/toolkit/query/react'

import { baseQueryWithReauth } from '@/api/baseQuery'

/**
 * The single RTK Query API slice for the whole app.
 *
 * ## How feature agents add endpoints — the injectEndpoints pattern
 *
 * Do NOT add endpoints to this file. Create `src/api/<feature>Api.ts` and
 * inject them, so each feature stays code-split friendly and independently
 * owned:
 *
 * ```ts
 * // src/api/tradesApi.ts
 * import { edgewiseApi } from '@/api/edgewiseApi'
 *
 * export const tradesApi = edgewiseApi.injectEndpoints({
 *   endpoints: (build) => ({
 *     getTrades: build.query<Trade[], { status?: string } | void>({
 *       query: (params) => ({ url: '/trades', params: params ?? undefined }),
 *       providesTags: (result) =>
 *         result
 *           ? [...result.map(({ id }) => ({ type: 'Trade' as const, id })), 'Trade']
 *           : ['Trade'],
 *     }),
 *     updateTrade: build.mutation<Trade, { id: string; patch: Partial<Trade> }>({
 *       query: ({ id, patch }) => ({ url: `/trades/${id}`, method: 'PATCH', body: patch }),
 *       invalidatesTags: (_res, _err, { id }) => [{ type: 'Trade', id }],
 *     }),
 *   }),
 * })
 *
 * export const { useGetTradesQuery, useUpdateTradeMutation } = tradesApi
 * ```
 *
 * Rules:
 *  - Only use tag types from the list below (add new ones here first).
 *  - Errors arrive as `{ error: { code, message } }` — surface them with
 *    `getApiErrorMessage` from `@/api/types`.
 *  - The base query already handles Bearer auth + 401 refresh/retry/logout.
 */
export const edgewiseApi = createApi({
  reducerPath: 'edgewiseApi',
  baseQuery: baseQueryWithReauth,
  tagTypes: [
    'Trade',
    'Plan',
    'Fill',
    'Holding',
    'Bucket',
    'Snapshot',
    'Insight',
    'Alert',
    'Strategy',
    'Backtest',
    'Watchlist',
    'Notification',
    'CockpitStatus',
    'RiskProfile',
    'Instrument',
    'Zone',
    'Forecast',
    'WeeklyReview',
    'Account',
  ],
  endpoints: () => ({}),
})
