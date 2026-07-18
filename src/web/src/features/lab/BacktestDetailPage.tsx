import { useParams } from 'react-router'
import { HistoryIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function BacktestDetailPage() {
  const { id } = useParams<{ id: string }>()

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={`Backtest ${id ?? ''}`}
        description="Results and trade list for one backtest run."
      />
      <EmptyState
        icon={HistoryIcon}
        title="Backtest results are coming"
        hint="Equity curve, drawdown profile, per-trade list and parameter set for this run — comparable side-by-side with your live results."
      />
    </div>
  )
}
