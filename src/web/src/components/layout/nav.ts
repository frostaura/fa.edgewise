import {
  FlaskConicalIcon,
  HomeIcon,
  MessageSquareTextIcon,
  NotebookPenIcon,
  PieChartIcon,
  RadarIcon,
  SettingsIcon,
  type LucideIcon,
} from 'lucide-react'

export interface NavItem {
  to: string
  label: string
  icon: LucideIcon
  /** Matches the root path exactly (used for the Cockpit). */
  end?: boolean
}

/** Primary pillars — left rail top section and the mobile tab bar. */
export const primaryNav: NavItem[] = [
  { to: '/', label: 'Home', icon: HomeIcon, end: true },
  { to: '/journal', label: 'Journal', icon: NotebookPenIcon },
  { to: '/portfolio', label: 'Portfolio', icon: PieChartIcon },
  { to: '/radar', label: 'Radar', icon: RadarIcon },
  { to: '/lab', label: 'Lab', icon: FlaskConicalIcon },
]

/** Bottom section of the left rail. */
export const secondaryNav: NavItem[] = [
  { to: '/coach/chat', label: 'Coach chat', icon: MessageSquareTextIcon },
  { to: '/settings', label: 'Settings', icon: SettingsIcon },
]
