import { edgewiseApi } from '@/api/edgewiseApi'

/** Mirrors AccountSyncStats (Account.LastSyncStatsJson) from the backend. */
export interface AccountSyncStats {
  lastRunAt?: string
  lastRunStatus?: 'ok' | 'error'
  error?: string
  warning?: string
  syncing?: boolean
  lastRunFills?: number
  totalFills?: number
  symbolsTotal?: number
  symbolsDone?: number
  currentSymbol?: string
  lastTradeIdPerSymbol?: Record<string, number>
  polymarketOffset?: number
  coinbaseLastSequenceTimestamp?: string
}

export type AccountVenue =
  | 'manual'
  | 'csv'
  | 'binance'
  | 'coinbase'
  | 'polymarket'
  | 'easyEquities'
  | 'generic'

export type AccountStatus = 'connected' | 'error' | 'revoked'

export interface IntegrationAccount {
  id: string
  venue: AccountVenue
  name: string
  bucketId: string
  status: AccountStatus
  /** Masked, e.g. "0x56687b…5839" — the API never returns the full address. */
  walletAddress?: string
  /** Last four characters of the stored API key / key name; never the key. */
  keyLastFour?: string
  lastSyncAt?: string
  lastSyncStats?: AccountSyncStats
}

export interface ConnectAccountRequest {
  venue: 'polymarket' | 'binance' | 'coinbase'
  bucketId: string
  name?: string
  walletAddress?: string
  apiKey?: string
  apiSecret?: string
  keyName?: string
  privateKeyPem?: string
}

export interface ConnectAccountResponse {
  account: IntegrationAccount
  warning?: string
}

export interface SyncAccountResponse {
  queued: boolean
  account?: IntegrationAccount
}

export interface AccountHealth {
  id: string
  status: AccountStatus
  lastSyncAt?: string
  lastSyncStats?: AccountSyncStats
  provider?: { lastSuccessAt?: string; lastErrorAt?: string; errorNote?: string }
  recentErrors: string[]
}

export interface BucketSummary {
  id: string
  name: string
}

export const accountsApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    getAccounts: build.query<IntegrationAccount[], void>({
      query: () => ({ url: '/accounts/' }),
      providesTags: (result) =>
        result
          ? [...result.map(({ id }) => ({ type: 'Account' as const, id })), 'Account']
          : ['Account'],
    }),
    connectAccount: build.mutation<ConnectAccountResponse, ConnectAccountRequest>({
      query: (body) => ({ url: '/accounts/connect', method: 'POST', body }),
      invalidatesTags: ['Account'],
    }),
    syncAccount: build.mutation<SyncAccountResponse, string>({
      query: (id) => ({ url: `/accounts/${id}/sync`, method: 'POST' }),
      invalidatesTags: (_res, _err, id) => [{ type: 'Account', id }, 'Account', 'Fill'],
    }),
    revokeAccount: build.mutation<void, string>({
      query: (id) => ({ url: `/accounts/${id}`, method: 'DELETE' }),
      invalidatesTags: (_res, _err, id) => [{ type: 'Account', id }, 'Account'],
    }),
    getAccountHealth: build.query<AccountHealth, string>({
      query: (id) => ({ url: `/accounts/${id}/health` }),
      providesTags: (_res, _err, id) => [{ type: 'Account', id }],
    }),
    /** Bucket picker for the connect forms; tolerates array or {items} envelopes. */
    getBucketSummaries: build.query<BucketSummary[], void>({
      query: () => ({ url: '/buckets' }),
      transformResponse: (response: unknown): BucketSummary[] => {
        const list = Array.isArray(response)
          ? response
          : response && typeof response === 'object' && Array.isArray((response as { items?: unknown }).items)
            ? ((response as { items: unknown[] }).items)
            : []
        return list
          .filter((b): b is { id: string; name?: string } =>
            !!b && typeof b === 'object' && typeof (b as { id?: unknown }).id === 'string')
          .map((b) => ({ id: b.id, name: typeof b.name === 'string' ? b.name : b.id }))
      },
      providesTags: ['Bucket'],
    }),
  }),
})

export const {
  useGetAccountsQuery,
  useConnectAccountMutation,
  useSyncAccountMutation,
  useRevokeAccountMutation,
  useGetAccountHealthQuery,
  useGetBucketSummariesQuery,
} = accountsApi
