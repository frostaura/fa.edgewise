import { PieChartIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function PortfolioPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Portfolio"
        description="Holdings, buckets and exposure across all your accounts."
      />
      <EmptyState
        icon={PieChartIcon}
        title="No holdings yet"
        hint="Connect accounts or log positions to see holdings by bucket, live exposure, concentration warnings and equity-curve history here."
      />
    </div>
  )
}
