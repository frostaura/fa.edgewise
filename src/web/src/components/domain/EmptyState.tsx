import type { LucideIcon } from 'lucide-react'
import type { ReactNode } from 'react'

import { cn } from '@/lib/utils'

export interface EmptyStateProps {
  icon?: LucideIcon
  title: string
  hint?: string
  action?: ReactNode
  className?: string
}

/**
 * The app-wide empty state: icon, title, one-line hint and an optional call
 * to action. Used for empty lists, feature stubs and error fallbacks.
 */
export function EmptyState({ icon: Icon, title, hint, action, className }: EmptyStateProps) {
  return (
    <div
      data-slot="empty-state"
      className={cn(
        'flex min-h-64 flex-col items-center justify-center gap-3 rounded-xl border border-dashed bg-surface/50 px-6 py-12 text-center',
        className,
      )}
    >
      {Icon && (
        <div className="flex size-12 items-center justify-center rounded-full bg-muted">
          <Icon className="size-6 text-muted-foreground" aria-hidden />
        </div>
      )}
      <h2 className="text-base font-semibold">{title}</h2>
      {hint && <p className="max-w-md text-sm text-balance text-muted-foreground">{hint}</p>}
      {action && <div className="mt-2">{action}</div>}
    </div>
  )
}
