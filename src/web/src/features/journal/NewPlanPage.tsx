import { ClipboardListIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function NewPlanPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="New Plan"
        description="Commit to entries, exits and risk before you touch the buy button."
      />
      <EmptyState
        icon={ClipboardListIcon}
        title="The plan composer is coming"
        hint="Define instrument, thesis, entry zone, stop, targets and position size — Edgewise grades your execution against this plan afterwards."
      />
    </div>
  )
}
