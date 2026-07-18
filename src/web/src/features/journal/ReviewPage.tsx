import { CalendarCheckIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function ReviewPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Weekly Review"
        description="A guided retro over last week's process, not just its P&L."
      />
      <EmptyState
        icon={CalendarCheckIcon}
        title="Weekly reviews are coming"
        hint="Every week Edgewise assembles your trades, adherence grades and journal notes into a guided review flow with prompts from your coach."
      />
    </div>
  )
}
