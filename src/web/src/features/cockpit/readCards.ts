import type { ComponentType } from 'react'
import {
  CalendarClockIcon,
  FlameIcon,
  HeartPulseIcon,
  TrendingDownIcon,
  WalletIcon,
} from 'lucide-react'

import type { CockpitStatus, ReadStatus } from '@/api/cockpitApi'

export interface ReadCard {
  key: string
  label: string
  icon: ComponentType<{ className?: string; 'aria-hidden'?: boolean }>
  value: string
  status: ReadStatus
  detail: string
}

function formatMinor(minor: number): string {
  const major = minor / 100
  return `${major > 0 ? '+' : ''}${major.toFixed(2)}`
}

/** Maps the cockpit status payload onto the five hero cards. */
export function buildReadCards(status: CockpitStatus): ReadCard[] {
  const { heat, dailyPnl, ladder, calendar, state } = status.reads
  return [
    {
      key: 'heat',
      label: 'Heat',
      icon: FlameIcon,
      value: heat.heatPct != null ? `${heat.heatPct}%` : heat.openTradesCount > 0 ? '?' : '0%',
      status: heat.status,
      detail: heat.detail,
    },
    {
      key: 'dailyPnl',
      label: 'Daily P&L',
      icon: WalletIcon,
      value: formatMinor(dailyPnl.pnlMinor),
      status: dailyPnl.status,
      detail: dailyPnl.detail,
    },
    {
      key: 'ladder',
      label: 'Ladder',
      icon: TrendingDownIcon,
      value: ladder.rung.charAt(0).toUpperCase() + ladder.rung.slice(1),
      status: ladder.status,
      detail: ladder.detail,
    },
    {
      key: 'calendar',
      label: 'Calendar',
      icon: CalendarClockIcon,
      value:
        calendar.events.length === 0
          ? 'Clear'
          : `${calendar.events.length} event${calendar.events.length === 1 ? '' : 's'}`,
      status: calendar.status,
      detail: calendar.detail,
    },
    {
      key: 'state',
      label: 'State',
      icon: HeartPulseIcon,
      value: state.state ? state.state.charAt(0).toUpperCase() + state.state.slice(1) : '—',
      status: state.status,
      detail: state.detail,
    },
  ]
}
