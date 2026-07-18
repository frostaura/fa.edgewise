import { RadarIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function RadarPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Radar"
        description="Watchlists, zones and alerts for the setups you're stalking."
      />
      <EmptyState
        icon={RadarIcon}
        title="Nothing on the radar yet"
        hint="Add instruments to a watchlist, draw entry zones and set alerts. When price approaches a zone, Radar nudges you to write a plan first."
      />
    </div>
  )
}
