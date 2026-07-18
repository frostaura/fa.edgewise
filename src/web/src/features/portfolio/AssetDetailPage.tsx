import { useParams } from 'react-router'
import { LineChartIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function AssetDetailPage() {
  const { instrumentId } = useParams<{ instrumentId: string }>()

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={`Asset ${instrumentId ?? ''}`}
        description="Position, zones and trade history for a single instrument."
      />
      <EmptyState
        icon={LineChartIcon}
        title="Asset detail is coming"
        hint="Price chart with your zones and past trades, current position with open risk, and every journal entry that touches this instrument."
      />
    </div>
  )
}
