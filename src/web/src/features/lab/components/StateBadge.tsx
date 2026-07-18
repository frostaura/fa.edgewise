import type { StrategyState } from '@/api/labApi'
import { Badge } from '@/components/ui/badge'
import { STATE_BADGE_VARIANT, STATE_LABELS } from '@/features/lab/ruleTree'

/** Strategy pipeline state chip with the Draft -> Live color ramp. */
export function StateBadge({ state }: { state: StrategyState }) {
  return (
    <Badge variant={STATE_BADGE_VARIANT[state] ?? 'outline'} data-state={state}>
      {STATE_LABELS[state] ?? state}
    </Badge>
  )
}
