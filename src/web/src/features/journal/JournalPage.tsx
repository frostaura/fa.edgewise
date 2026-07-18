import { Link } from 'react-router'
import { NotebookPenIcon, PlusIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { Button } from '@/components/ui/button'

export default function JournalPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Journal"
        description="Plans, decisions and executed trades — your process on the record."
        actions={
          <Button asChild size="sm">
            <Link to="/journal/plans/new">
              <PlusIcon aria-hidden />
              New Plan
            </Link>
          </Button>
        }
      />
      <EmptyState
        icon={NotebookPenIcon}
        title="No journal entries yet"
        hint="Trade logs with R-multiples and adherence grades, decision notes and plan history will live here. Start by writing your first plan."
        action={
          <Button asChild variant="outline" size="sm">
            <Link to="/journal/import">Import trade history</Link>
          </Button>
        }
      />
    </div>
  )
}
