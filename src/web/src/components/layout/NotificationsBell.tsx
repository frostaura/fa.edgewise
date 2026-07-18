import { BellIcon } from 'lucide-react'

import { useGetNotificationsQuery } from '@/api/notificationsApi'
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
 * Unread-notifications bell. Wired to GET /api/notifications?unread=true and
 * degrades gracefully (no badge, quiet message) while the API is absent.
 */
export function NotificationsBell() {
  const { data, isError } = useGetNotificationsQuery({ unread: true }, { pollingInterval: 60_000 })
  const count = data?.length ?? 0

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
        <DropdownMenuLabel>Notifications</DropdownMenuLabel>
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
          data?.slice(0, 8).map((n) => (
            <DropdownMenuItem key={n.id} className="flex flex-col items-start gap-0.5">
              <span className="font-medium">{n.title ?? 'Notification'}</span>
              {n.message && (
                <span className="line-clamp-2 text-xs text-muted-foreground">{n.message}</span>
              )}
            </DropdownMenuItem>
          ))
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
