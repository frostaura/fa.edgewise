import { useState } from 'react'
import { format, isToday, isTomorrow } from 'date-fns'
import { CalendarClockIcon } from 'lucide-react'

import { useGetCatalystsQuery, type CatalystItem } from '@/api/cockpitApi'
import { EmptyState } from '@/components/domain/EmptyState'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'

function dayLabel(date: Date): string {
  if (isToday(date)) return 'Today'
  if (isTomorrow(date)) return 'Tomorrow'
  return format(date, 'EEEE d MMM')
}

/** 7-day catalyst calendar, grouped by day, red/amber dots, held/watched filter. */
export function CalendarSection() {
  const { data: events, isLoading } = useGetCatalystsQuery({ days: 7 })
  const [onlyMine, setOnlyMine] = useState(false)

  if (isLoading) {
    return <Skeleton className="h-40 rounded-xl" />
  }

  const filtered = (events ?? []).filter((e) => !onlyMine || e.held || e.watched)
  const byDay = new Map<string, CatalystItem[]>()
  for (const event of filtered) {
    const key = format(new Date(event.at), 'yyyy-MM-dd')
    byDay.set(key, [...(byDay.get(key) ?? []), event])
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center gap-2">
        <Button
          size="sm"
          variant={onlyMine ? 'default' : 'outline'}
          onClick={() => setOnlyMine((v) => !v)}
        >
          Held / watched only
        </Button>
      </div>

      {byDay.size === 0 ? (
        <EmptyState
          icon={CalendarClockIcon}
          title="No catalysts in the next 7 days"
          hint="Earnings, macro prints and unlocks touching your instruments will appear here."
        />
      ) : (
        [...byDay.entries()].map(([day, dayEvents]) => (
          <div key={day} className="flex flex-col gap-1">
            <h3 className="text-sm font-semibold text-muted-foreground">
              {dayLabel(new Date(`${day}T00:00:00`))}
            </h3>
            <ul className="flex flex-col divide-y rounded-lg border">
              {dayEvents.map((event) => (
                <li key={event.id} className="flex items-center gap-3 px-3 py-2 text-sm">
                  <span
                    className={cn(
                      'size-2 shrink-0 rounded-full',
                      event.severity === 'red' ? 'bg-destructive' : 'bg-warning',
                    )}
                    aria-label={event.severity}
                  />
                  <span className="w-12 shrink-0 font-mono text-xs text-muted-foreground">
                    {format(new Date(event.at), 'HH:mm')}
                  </span>
                  {event.symbol && (
                    <Badge variant="outline" className="shrink-0">
                      {event.symbol}
                    </Badge>
                  )}
                  <span className="min-w-0 flex-1 truncate">{event.title}</span>
                  <span className="text-xs text-muted-foreground capitalize">{event.kind}</span>
                  {(event.held || event.watched) && (
                    <Badge variant="secondary">{event.held ? 'held' : 'watched'}</Badge>
                  )}
                </li>
              ))}
            </ul>
          </div>
        ))
      )}
    </div>
  )
}
