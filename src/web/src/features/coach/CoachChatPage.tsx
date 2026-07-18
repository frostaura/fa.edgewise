import { MessageSquareTextIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function CoachChatPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Coach"
        description="An AI coach that knows your process — and holds you to it."
      />
      <EmptyState
        icon={MessageSquareTextIcon}
        title="Your coach is on the way"
        hint="Chat about setups, get pre-trade checklists and post-trade debriefs. The coach cites your own journal and stats, with provenance on every claim."
      />
    </div>
  )
}
