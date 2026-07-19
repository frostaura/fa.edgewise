import { NavLink } from 'react-router'

import { cn } from '@/lib/utils'
import { LogoMark } from '@/components/brand/LogoMark'
import { primaryNav, secondaryNav, type NavItem } from '@/components/layout/nav'

function RailLink({ item }: { item: NavItem }) {
  const Icon = item.icon
  return (
    <NavLink
      to={item.to}
      end={item.end}
      className={({ isActive }) =>
        cn(
          'flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors',
          isActive
            ? 'bg-accent text-accent-foreground'
            : 'text-muted-foreground hover:bg-muted hover:text-foreground',
        )
      }
    >
      <Icon className="size-4 shrink-0" aria-hidden />
      <span>{item.label}</span>
    </NavLink>
  )
}

/** Desktop-only left navigation rail (hidden below md). */
export function LeftRail() {
  return (
    <aside className="fixed inset-y-0 left-0 z-40 hidden w-52 flex-col border-r bg-surface md:flex">
      <div className="flex h-14 items-center gap-2 border-b px-4">
        <LogoMark className="size-7" />
        <span className="text-sm font-semibold tracking-tight">Edgewise</span>
      </div>
      <nav aria-label="Primary" className="flex flex-1 flex-col gap-1 p-3">
        {primaryNav.map((item) => (
          <RailLink key={item.to} item={item} />
        ))}
      </nav>
      <nav aria-label="Secondary" className="flex flex-col gap-1 border-t p-3">
        {secondaryNav.map((item) => (
          <RailLink key={item.to} item={item} />
        ))}
      </nav>
    </aside>
  )
}
