import { InfoIcon } from 'lucide-react'

import { cn } from '@/lib/utils'

export interface ProvenanceChipProps {
  /** What a number or insight is derived from, e.g. "42 trades · last 90d". */
  basedOn: string
  className?: string
}

/** Small "based on: …" chip that keeps derived numbers honest. */
export function ProvenanceChip({ basedOn, className }: ProvenanceChipProps) {
  return (
    <span
      data-slot="provenance-chip"
      className={cn(
        'inline-flex items-center gap-1 rounded-full border border-border bg-muted/50 px-2 py-0.5 text-xs text-muted-foreground',
        className,
      )}
    >
      <InfoIcon className="size-3 shrink-0" aria-hidden />
      <span>
        based on: <span className="font-medium text-foreground/80">{basedOn}</span>
      </span>
    </span>
  )
}
