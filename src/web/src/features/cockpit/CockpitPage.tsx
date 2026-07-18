import { Link } from 'react-router'
import { GaugeIcon, PlusIcon } from 'lucide-react'

import { useAppSelector } from '@/app/hooks'
import { selectCurrentUser } from '@/features/auth/authSlice'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { StatCard } from '@/components/domain/StatCard'
import { Button } from '@/components/ui/button'

/** Home: the daily cockpit — status, adherence, exposure, next actions. */
export default function CockpitPage() {
  const user = useAppSelector(selectCurrentUser)

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Cockpit"
        description={
          user ? `Good to see you, ${user.email}. Here's your process at a glance.` : undefined
        }
        actions={
          <Button asChild size="sm">
            <Link to="/journal/plans/new">
              <PlusIcon aria-hidden />
              New Plan
            </Link>
          </Button>
        }
      />

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <StatCard label="Adherence (30d)" value="—" />
        <StatCard label="Net P&L (30d)" value="—" />
        <StatCard label="Open risk" value="—" />
        <StatCard label="Plans this week" value="—" />
      </div>

      <EmptyState
        icon={GaugeIcon}
        title="Your cockpit is warming up"
        hint="Once trades and plans start flowing in, this page shows today's status, open risk, adherence trends and what needs your attention next."
        action={
          <Button asChild variant="outline" size="sm">
            <Link to="/journal">Open the Journal</Link>
          </Button>
        }
      />
    </div>
  )
}
