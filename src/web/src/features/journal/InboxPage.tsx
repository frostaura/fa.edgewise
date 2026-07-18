import { InboxIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function InboxPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Inbox"
        description="Unreviewed fills and imports waiting to be matched to plans."
      />
      <EmptyState
        icon={InboxIcon}
        title="Inbox zero"
        hint="Fills from your broker imports land here until you match them to a plan or log them as unplanned. Nothing is waiting on you right now."
      />
    </div>
  )
}
