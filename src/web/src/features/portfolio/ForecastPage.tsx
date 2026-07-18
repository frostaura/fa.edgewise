import { TrendingUpIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function ForecastPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Forecast"
        description="Where the portfolio could go under your own assumptions."
      />
      <EmptyState
        icon={TrendingUpIcon}
        title="Forecasting is coming"
        hint="Scenario-based projections built from your holdings, contribution schedule and expected-return assumptions — with provenance chips on every number."
      />
    </div>
  )
}
