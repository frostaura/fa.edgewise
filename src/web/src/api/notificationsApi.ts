import { edgewiseApi } from '@/api/edgewiseApi'
import type { NotificationItem } from '@/api/types'

/**
 * Notifications endpoints (also a minimal reference implementation of the
 * injectEndpoints pattern documented in edgewiseApi.ts).
 */
export const notificationsApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    getNotifications: build.query<NotificationItem[], { unread?: boolean } | void>({
      query: (args) => ({
        url: '/notifications',
        params: args && args.unread !== undefined ? { unread: args.unread } : undefined,
      }),
      // Tolerate either a bare array or an { items: [...] } envelope.
      transformResponse: (response: unknown): NotificationItem[] => {
        if (Array.isArray(response)) return response as NotificationItem[]
        if (response && typeof response === 'object') {
          const items = (response as { items?: unknown }).items
          if (Array.isArray(items)) return items as NotificationItem[]
        }
        return []
      },
      providesTags: ['Notification'],
    }),
  }),
})

export const { useGetNotificationsQuery } = notificationsApi
