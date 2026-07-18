import { differenceInMinutes } from 'date-fns'
import { ClockIcon } from 'lucide-react'

import { cn } from '@/lib/utils'

/** Compact age label: 45s → "<1m", 12m → "12m", 3h → "3h", 2d → "2d". */
export function ageLabel(updatedAt: Date | string | number, now: Date = new Date()): string {
  const then = new Date(updatedAt)
  const minutes = Math.max(0, differenceInMinutes(now, then))
  if (minutes < 1) return '<1m'
  if (minutes < 60) return `${minutes}m`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h`
  return `${Math.floor(hours / 24)}d`
}

export interface StaleBadgeProps {
  /** When the underlying data was last refreshed. */
  updatedAt: Date | string | number
  className?: string
}

/** Amber "stale · 12m" chip for data that hasn't refreshed recently. */
export function StaleBadge({ updatedAt, className }: StaleBadgeProps) {
  return (
    <span
      data-slot="stale-badge"
      className={cn(
        'inline-flex items-center gap-1 rounded-full bg-warning px-2 py-0.5 text-xs font-medium text-warning-foreground',
        className,
      )}
    >
      <ClockIcon className="size-3" aria-hidden />
      stale · {ageLabel(updatedAt)}
    </span>
  )
}
