import { useNavigate } from 'react-router'
import { BellIcon, CheckCheckIcon } from 'lucide-react'

import { useGetNotificationsQuery } from '@/api/notificationsApi'
import {
  useMarkAllNotificationsReadMutation,
  useMarkNotificationReadMutation,
} from '@/api/radarApi'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'

/**
 * Unread-notifications bell: unread list, mark-read (single + all) and
 * deep-link navigation. Degrades gracefully while the API is absent.
 */
export function NotificationsBell() {
  const navigate = useNavigate()
  const { data, isError } = useGetNotificationsQuery({ unread: true }, { pollingInterval: 60_000 })
  const [markRead] = useMarkNotificationReadMutation()
  const [markAllRead] = useMarkAllNotificationsReadMutation()
  const count = data?.length ?? 0

  const openNotification = (n: { id: string }) => {
    markRead(n.id)
    const deepLink = (n as { deepLink?: string }).deepLink
    if (deepLink?.startsWith('/')) {
      navigate(deepLink)
    }
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          className="relative"
          aria-label={count > 0 ? `Notifications, ${count} unread` : 'Notifications'}
        >
          <BellIcon aria-hidden />
          {count > 0 && (
            <span
              aria-hidden
              className="absolute top-1 right-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-1 text-xs font-semibold text-primary-foreground"
            >
              {count > 9 ? '9+' : count}
            </span>
          )}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-72">
        <DropdownMenuLabel className="flex items-center justify-between">
          Notifications
          {count > 0 && (
            <Button
              variant="ghost"
              size="sm"
              className="h-6 gap-1 px-2 text-xs"
              onClick={() => markAllRead()}
            >
              <CheckCheckIcon className="size-3" aria-hidden />
              Mark all read
            </Button>
          )}
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {isError ? (
          <p className="px-2 py-4 text-center text-sm text-muted-foreground">
            Notifications are unavailable right now.
          </p>
        ) : count === 0 ? (
          <p className="px-2 py-4 text-center text-sm text-muted-foreground">
            You&rsquo;re all caught up.
          </p>
        ) : (
          data?.slice(0, 8).map((n) => {
            const body =
              (n as { body?: string }).body ?? (n as { message?: string }).message ?? undefined
            return (
              <DropdownMenuItem
                key={n.id}
                className="flex cursor-pointer flex-col items-start gap-0.5"
                onSelect={() => openNotification(n)}
              >
                <span className="font-medium">{n.title ?? 'Notification'}</span>
                {body && <span className="line-clamp-2 text-xs text-muted-foreground">{body}</span>}
              </DropdownMenuItem>
            )
          })
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
