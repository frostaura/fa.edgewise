import { Link } from 'react-router'
import { FlaskConicalIcon } from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { Button } from '@/components/ui/button'

export default function LabPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Lab"
        description="Strategies and backtests — test your edge before you trade it."
      />
      <EmptyState
        icon={FlaskConicalIcon}
        title="The Lab is coming"
        hint="Define strategies as explicit rules, backtest them over history and compare expectancy against your live execution."
        action={
          <Button asChild variant="outline" size="sm">
            <Link to="/lab/strategies">Browse strategies</Link>
          </Button>
        }
      />
    </div>
  )
}
