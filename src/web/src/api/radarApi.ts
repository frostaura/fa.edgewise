import { edgewiseApi } from '@/api/edgewiseApi'

// ------------------------------------------------------------------ types

export interface Quote {
  price: number
  asOf: string
  provider: string
  stale: boolean
}

export interface WatchlistItem {
  id: string
  watchlistId: string
  instrumentId: string
  symbol?: string | null
  instrumentName?: string | null
  note?: string | null
  whyWatching?: string | null
  alertLevelsJson?: string | null
  quote?: Quote | null
  alertCount: number
}

export interface Watchlist {
  id: string
  name: string
  items: WatchlistItem[]
}

export type AlertKind =
  | 'priceCross'
  | 'pctMove'
  | 'zoneTouch'
  | 'fundingRate'
  | 'fgExtreme'
  | 'catalystT24'

export interface Alert {
  id: string
  kind: AlertKind
  instrumentId?: string | null
  symbol?: string | null
  params?: Record<string, unknown> | null
  enabled: boolean
  lastTriggeredAt?: string | null
  cooldownMinutes: number
}

export interface SaveAlertRequest {
  kind: AlertKind
  instrumentId?: string
  params?: Record<string, unknown>
  enabled?: boolean
  cooldownMinutes?: number
}

export interface AppNotification {
  id: string
  alertId?: string | null
  title: string
  body: string
  deepLink?: string | null
  channels?: string | null
  createdAt: string
  readAt?: string | null
}

export interface PromotePayload {
  instrumentId: string
  symbol?: string | null
  note?: string | null
}

export interface InstrumentSummary {
  id: string
  symbol: string
  name: string
  assetClass?: string
}

export interface NewsItem {
  id: string
  title: string
  url: string
  source: string
  publishedAt: string
  summary?: string | null
}

export interface SentimentReading {
  kind: string
  date: string
  value: number
  label: string
}

// -------------------------------------------------------------- endpoints

export const radarApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    // Watchlists
    getWatchlists: build.query<Watchlist[], void>({
      query: () => '/watchlists',
      providesTags: ['Watchlist'],
    }),
    createWatchlist: build.mutation<Watchlist, { name: string }>({
      query: (body) => ({ url: '/watchlists', method: 'POST', body }),
      invalidatesTags: ['Watchlist'],
    }),
    deleteWatchlist: build.mutation<void, string>({
      query: (id) => ({ url: `/watchlists/${id}`, method: 'DELETE' }),
      invalidatesTags: ['Watchlist'],
    }),
    addWatchlistItem: build.mutation<
      WatchlistItem,
      { watchlistId: string; instrumentId: string; note?: string; whyWatching?: string }
    >({
      query: ({ watchlistId, ...body }) => ({
        url: `/watchlists/${watchlistId}/items`,
        method: 'POST',
        body,
      }),
      invalidatesTags: ['Watchlist'],
    }),
    deleteWatchlistItem: build.mutation<void, string>({
      query: (itemId) => ({ url: `/watchlists/items/${itemId}`, method: 'DELETE' }),
      invalidatesTags: ['Watchlist'],
    }),
    promoteWatchlistItem: build.mutation<PromotePayload, string>({
      query: (itemId) => ({ url: `/watchlists/items/${itemId}/promote-to-plan`, method: 'POST' }),
    }),

    // Alerts
    getAlerts: build.query<Alert[], void>({
      query: () => '/alerts',
      providesTags: ['Alert'],
    }),
    createAlert: build.mutation<Alert, SaveAlertRequest>({
      query: (body) => ({ url: '/alerts', method: 'POST', body }),
      invalidatesTags: ['Alert', 'Watchlist'],
    }),
    updateAlert: build.mutation<Alert, { id: string; patch: Partial<SaveAlertRequest> }>({
      query: ({ id, patch }) => ({ url: `/alerts/${id}`, method: 'PATCH', body: patch }),
      invalidatesTags: ['Alert'],
    }),
    deleteAlert: build.mutation<void, string>({
      query: (id) => ({ url: `/alerts/${id}`, method: 'DELETE' }),
      invalidatesTags: ['Alert', 'Watchlist'],
    }),

    // Notifications
    markNotificationRead: build.mutation<AppNotification, string>({
      query: (id) => ({ url: `/notifications/${id}/read`, method: 'POST' }),
      invalidatesTags: ['Notification'],
    }),
    markAllNotificationsRead: build.mutation<{ marked: number }, void>({
      query: () => ({ url: '/notifications/read-all', method: 'POST' }),
      invalidatesTags: ['Notification'],
    }),

    // Web push
    getVapidKey: build.query<{ publicKey: string }, void>({
      query: () => '/notifications/push/vapid',
    }),
    subscribePush: build.mutation<{ subscribed: boolean }, { subscription: unknown }>({
      query: (body) => ({ url: '/notifications/push/subscribe', method: 'POST', body }),
    }),
    unsubscribePush: build.mutation<{ removed: number }, void>({
      query: () => ({ url: '/notifications/push/subscribe', method: 'DELETE' }),
    }),

    // Defensive reads against endpoints owned by other verticals — every
    // consumer handles isError by hiding/degrading the section.
    getInstruments: build.query<InstrumentSummary[], void>({
      // The market vertical exposes the shared catalogue under /lab/instruments;
      // a bare /instruments route does not exist (it 404s and every consumer
      // silently degraded to a raw-UUID input).
      query: () => '/lab/instruments',
      transformResponse: (response: unknown): InstrumentSummary[] => {
        if (Array.isArray(response)) return response as InstrumentSummary[]
        if (response && typeof response === 'object') {
          const items = (response as { items?: unknown }).items
          if (Array.isArray(items)) return items as InstrumentSummary[]
        }
        return []
      },
      providesTags: ['Instrument'],
    }),
    getNews: build.query<NewsItem[], void>({
      query: () => '/news',
      transformResponse: (response: unknown): NewsItem[] => {
        if (Array.isArray(response)) return response as NewsItem[]
        if (response && typeof response === 'object') {
          const items = (response as { items?: unknown }).items
          if (Array.isArray(items)) return items as NewsItem[]
        }
        return []
      },
    }),
    getSentiment: build.query<SentimentReading[], void>({
      query: () => '/market/sentiment',
      transformResponse: (response: unknown): SentimentReading[] => {
        if (Array.isArray(response)) return response as SentimentReading[]
        if (response && typeof response === 'object') {
          const items = (response as { items?: unknown; readings?: unknown })
          if (Array.isArray(items.items)) return items.items as SentimentReading[]
          if (Array.isArray(items.readings)) return items.readings as SentimentReading[]
        }
        return []
      },
    }),
  }),
})

export const {
  useGetWatchlistsQuery,
  useCreateWatchlistMutation,
  useDeleteWatchlistMutation,
  useAddWatchlistItemMutation,
  useDeleteWatchlistItemMutation,
  usePromoteWatchlistItemMutation,
  useGetAlertsQuery,
  useCreateAlertMutation,
  useUpdateAlertMutation,
  useDeleteAlertMutation,
  useMarkNotificationReadMutation,
  useMarkAllNotificationsReadMutation,
  useSubscribePushMutation,
  useUnsubscribePushMutation,
  useGetInstrumentsQuery,
  useGetNewsQuery,
  useGetSentimentQuery,
} = radarApi
