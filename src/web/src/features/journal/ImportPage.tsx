import { UploadIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'

export default function ImportPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Import"
        description="Bring in fills and history from brokers and CSV exports."
      />
      <EmptyState
        icon={UploadIcon}
        title="Importers are coming"
        hint="Drop broker CSVs or connect integrations to pull your trade history. Imported fills land in the Inbox for matching."
      />
    </div>
  )
}
