import { NavLink, Outlet } from 'react-router'

import { cn } from '@/lib/utils'
import { PageHeader } from '@/components/domain/PageHeader'

const tabs = [
  { to: '/settings/profile', label: 'Profile' },
  { to: '/settings/risk', label: 'Risk' },
  { to: '/settings/buckets', label: 'Buckets' },
  { to: '/settings/integrations', label: 'Integrations' },
  { to: '/settings/llm', label: 'LLM' },
  { to: '/settings/security', label: 'Security' },
  { to: '/settings/export', label: 'Export' },
]

/** Settings shell: route-driven tab strip with nested section outlet. */
export default function SettingsPage() {
  return (
    <div className="flex flex-col gap-6">
      <PageHeader title="Settings" description="Your account, risk rules and integrations." />
      <nav
        aria-label="Settings sections"
        className="flex w-full gap-1 overflow-x-auto rounded-lg bg-muted p-1"
      >
        {tabs.map((tab) => (
          <NavLink
            key={tab.to}
            to={tab.to}
            className={({ isActive }) =>
              cn(
                'inline-flex h-7 items-center justify-center rounded-md px-3 text-sm font-medium whitespace-nowrap transition-colors',
                isActive
                  ? 'bg-surface text-foreground shadow-sm'
                  : 'text-muted-foreground hover:text-foreground',
              )
            }
          >
            {tab.label}
          </NavLink>
        ))}
      </nav>
      <Outlet />
    </div>
  )
}
