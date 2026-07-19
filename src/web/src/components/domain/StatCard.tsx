import type { ReactNode } from 'react'
import { ArrowDownRightIcon, ArrowUpRightIcon } from 'lucide-react'

import { cn } from '@/lib/utils'
import { Card, CardContent } from '@/components/ui/card'

export interface StatCardProps {
  label: string
  /** Main value; pass "—" while data is unavailable. */
  value: ReactNode
  /** Signed change, rendered green/red with a direction arrow. */
  delta?: { value: number; label?: string }
  /** Slot for a small sparkline (e.g. an EquityCurveChart in sparkline mode). */
  sparkline?: ReactNode
  className?: string
}

/** Compact metric card: label, value, delta and an optional sparkline slot. */
export function StatCard({ label, value, delta, sparkline, className }: StatCardProps) {
  const deltaTone =
    delta === undefined
      ? ''
      : delta.value > 0
        ? 'text-success'
        : delta.value < 0
          ? 'text-destructive'
          : 'text-muted-foreground'

  return (
    <Card data-slot="stat-card" className={cn('gap-0 py-4', className)}>
      <CardContent className="flex flex-col gap-1 px-4">
        <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
          {label}
        </span>
        <div className="flex items-end justify-between gap-2">
          <span className="text-2xl font-semibold tabular-nums">{value}</span>
          {delta && (
            <span className={cn('inline-flex items-center gap-0.5 text-sm font-medium', deltaTone)}>
              {delta.value > 0 ? (
                <ArrowUpRightIcon className="size-3.5" aria-hidden />
              ) : delta.value < 0 ? (
                <ArrowDownRightIcon className="size-3.5" aria-hidden />
              ) : null}
              {delta.label ?? `${delta.value > 0 ? '+' : ''}${delta.value}`}
            </span>
          )}
        </div>
        {sparkline && <div className="mt-2 h-10">{sparkline}</div>}
      </CardContent>
    </Card>
  )
}
