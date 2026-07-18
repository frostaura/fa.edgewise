import { BookMarkedIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function StrategiesPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Strategies"
        description="Your playbook: named setups with explicit rules and stats."
      />
      <EmptyState
        icon={BookMarkedIcon}
        title="No strategies yet"
        hint="Codify each setup you trade — entry criteria, invalidation, management — and Edgewise tracks live performance per strategy."
      />
    </div>
  )
}
