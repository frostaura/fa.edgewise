import { InfoIcon } from 'lucide-react'

import type { CockpitStatus } from '@/api/cockpitApi'
import { statusTone } from '@/features/cockpit/cockpitLock'
import { buildReadCards } from '@/features/cockpit/readCards'
import { Card, CardContent } from '@/components/ui/card'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { cn } from '@/lib/utils'

/** The hero five-reads row: 5 status cards with green/amber/red border + dot. */
export function StatusCards({ status }: { status: CockpitStatus }) {
  const cards = buildReadCards(status)
  return (
    <div className="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-5">
      {cards.map((card) => {
        const tone = statusTone(card.status)
        const Icon = card.icon
        const body = (
          <Card
            data-slot="cockpit-read"
            data-status={card.status}
            className={cn('gap-0 border-2 py-3 transition-colors', tone.border)}
          >
            <CardContent className="flex flex-col gap-1 px-3">
              <div className="flex items-center justify-between gap-2">
                <span className="inline-flex items-center gap-1.5 text-xs font-medium tracking-wide text-muted-foreground uppercase">
                  <Icon className="size-3.5" aria-hidden />
                  {card.label}
                </span>
                <span
                  className={cn('size-2 shrink-0 rounded-full', tone.dot)}
                  role="status"
                  aria-label={`${card.label}: ${tone.label.toLowerCase()}`}
                />
              </div>
              <div className="flex items-center justify-between gap-1">
                <span className="text-lg font-semibold tabular-nums">{card.value}</span>
                {card.status !== 'green' && (
                  <InfoIcon className="size-3.5 text-muted-foreground" aria-hidden />
                )}
              </div>
            </CardContent>
          </Card>
        )

        // Non-green cards explain themselves on tap/click.
        return card.status === 'green' ? (
          <div key={card.key} title={card.detail}>
            {body}
          </div>
        ) : (
          <Popover key={card.key}>
            <PopoverTrigger asChild>
              <button
                type="button"
                className="cursor-pointer text-left"
                aria-label={`Explain ${card.label} status`}
              >
                {body}
              </button>
            </PopoverTrigger>
            <PopoverContent align="start" className="w-72 text-sm">
              <p className={cn('mb-1 font-semibold', tone.text)}>
                {card.label}: {tone.label}
              </p>
              <p className="text-muted-foreground">{card.detail}</p>
            </PopoverContent>
          </Popover>
        )
      })}
    </div>
  )
}
