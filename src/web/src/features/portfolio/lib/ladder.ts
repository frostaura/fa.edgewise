import type { LadderState } from '@/api/portfolioApi'

export interface LadderBadge {
  label: string
  /** Badge variant from @/components/ui/badge. */
  variant: 'success' | 'warning' | 'destructive' | 'secondary'
  description: string
}

/** Colored-chip mapping for the trading ladder state machine. */
export function ladderBadge(state: LadderState | undefined): LadderBadge {
  switch (state) {
    case 'riskHalved':
      return {
        label: 'Risk halved',
        variant: 'warning',
        description: 'Drawdown ≥ 5% below high-water mark — position risk is halved.',
      }
    case 'paused':
      return {
        label: 'Paused',
        variant: 'destructive',
        description: 'Drawdown ≥ 10% — trading is paused and the Cockpit locks new entries.',
      }
    case 'paperProposed':
      return {
        label: 'Paper proposed',
        variant: 'secondary',
        description: 'Drawdown ≥ 15% — switch to paper trading until the curve recovers.',
      }
    case 'normal':
      return { label: 'Normal', variant: 'success', description: 'Trading within normal risk limits.' }
    default:
      return { label: '—', variant: 'secondary', description: 'Ladder state unavailable.' }
  }
}
