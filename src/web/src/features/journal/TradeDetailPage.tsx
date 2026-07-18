import { useParams } from 'react-router'
import { CandlestickChartIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function TradeDetailPage() {
  const { id } = useParams<{ id: string }>()

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={`Trade ${id ?? ''}`}
        description="Full lifecycle of a single trade: plan, fills, exits and review."
      />
      <EmptyState
        icon={CandlestickChartIcon}
        title="Trade detail is coming"
        hint="This page will show the entry/exit chart with plan overlays, the fill timeline, the R-multiple result and the adherence grade with a breakdown."
      />
    </div>
  )
}
