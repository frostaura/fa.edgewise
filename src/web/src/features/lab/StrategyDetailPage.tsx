import { useParams } from 'react-router'
import { BookMarkedIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function StrategyDetailPage() {
  const { id } = useParams<{ id: string }>()

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={`Strategy ${id ?? ''}`}
        description="Rules, live stats and backtests for one strategy."
      />
      <EmptyState
        icon={BookMarkedIcon}
        title="Strategy detail is coming"
        hint="Rule definition, expectancy and adherence stats from live trades, plus every backtest run against this strategy."
      />
    </div>
  )
}
