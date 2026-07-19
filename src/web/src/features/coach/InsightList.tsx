import { SparklesIcon } from 'lucide-react'

import { useGeneratePostMortemMutation, useGetInsightsQuery } from '@/api/coachApi'
import { EmptyState } from '@/components/domain/EmptyState'
import { InsightCard } from '@/components/domain/InsightCard'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'

export interface InsightListProps {
  /** Filter to one insight type (camelCase, e.g. "postMortem", "bias"). */
  type?: string
  /** Filter to one trade; also enables per-trade regeneration. */
  tradeId?: string
  limit?: number
  className?: string
  emptyHint?: string
}

/**
 * Reusable list of non-superseded coach insights — embed on trade detail pages
 * (`<InsightList tradeId={id} />`), the cockpit, or anywhere else.
 */
export function InsightList({ type, tradeId, limit, className, emptyHint }: InsightListProps) {
  const { data, isLoading } = useGetInsightsQuery({ type, tradeId, limit })
  const [generatePostMortem] = useGeneratePostMortemMutation()

  if (isLoading) {
    return (
      <div className={cn('flex flex-col gap-3', className)}>
        <Skeleton className="h-36 w-full" />
        <Skeleton className="h-36 w-full" />
      </div>
    )
  }

  if (!data || data.length === 0) {
    return (
      <EmptyState
        icon={SparklesIcon}
        title="No insights yet"
        hint={
          emptyHint ??
          'The coach reviews closed trades nightly — or generate a post-mortem on demand from a trade page.'
        }
        className={cn('min-h-40', className)}
      />
    )
  }

  return (
    <div className={cn('flex flex-col gap-3', className)}>
      {data.map((insight) => (
        <InsightCard
          key={insight.id}
          insight={insight}
          onRegenerate={
            tradeId && insight.type === 'postMortem'
              ? () => void generatePostMortem({ tradeId })
              : undefined
          }
        />
      ))}
    </div>
  )
}
