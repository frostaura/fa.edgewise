import type { ComponentType } from 'react'
import { createBrowserRouter, Link, redirect } from 'react-router'

import { AppShell } from '@/components/layout/AppShell'
import { RequireAuth } from '@/features/auth/RequireAuth'
import { EmptyState } from '@/components/domain/EmptyState'
import { Button } from '@/components/ui/button'
import { CompassIcon } from 'lucide-react'

/** Adapt a default-export page module to a react-router lazy route. */
const page = (load: () => Promise<{ default: ComponentType }>) => async () => ({
  Component: (await load()).default,
})

function NotFound() {
  return (
    <EmptyState
      icon={CompassIcon}
      title="Page not found"
      hint="The page you are looking for does not exist or has moved."
      action={
        <Button asChild size="sm">
          <Link to="/">Back to Cockpit</Link>
        </Button>
      }
    />
  )
}

export const router = createBrowserRouter([
  {
    path: '/',
    element: (
      <RequireAuth>
        <AppShell />
      </RequireAuth>
    ),
    children: [
      {
        index: true,
        handle: { title: 'Cockpit' },
        lazy: page(() => import('@/features/cockpit/CockpitPage')),
      },

      {
        path: 'journal',
        handle: { title: 'Journal' },
        lazy: page(() => import('@/features/journal/JournalPage')),
      },
      {
        path: 'journal/inbox',
        handle: { title: 'Inbox' },
        lazy: page(() => import('@/features/journal/InboxPage')),
      },
      {
        path: 'journal/trades/:id',
        handle: { title: 'Trade' },
        lazy: page(() => import('@/features/journal/TradeDetailPage')),
      },
      {
        path: 'journal/plans/new',
        handle: { title: 'New Plan' },
        lazy: page(() => import('@/features/journal/NewPlanPage')),
      },
      {
        path: 'journal/review',
        handle: { title: 'Weekly Review' },
        lazy: page(() => import('@/features/journal/ReviewPage')),
      },
      {
        path: 'journal/import',
        handle: { title: 'Import' },
        lazy: page(() => import('@/features/journal/ImportPage')),
      },

      {
        path: 'portfolio',
        handle: { title: 'Portfolio' },
        lazy: page(() => import('@/features/portfolio/PortfolioPage')),
      },
      {
        path: 'portfolio/assets/:instrumentId',
        handle: { title: 'Asset' },
        lazy: page(() => import('@/features/portfolio/AssetDetailPage')),
      },
      {
        path: 'portfolio/forecast',
        handle: { title: 'Forecast' },
        lazy: page(() => import('@/features/portfolio/ForecastPage')),
      },

      {
        path: 'radar',
        handle: { title: 'Radar' },
        lazy: page(() => import('@/features/radar/RadarPage')),
      },

      { path: 'lab', handle: { title: 'Lab' }, lazy: page(() => import('@/features/lab/LabPage')) },
      {
        path: 'lab/strategies',
        handle: { title: 'Strategies' },
        lazy: page(() => import('@/features/lab/StrategiesPage')),
      },
      {
        path: 'lab/strategies/:id',
        handle: { title: 'Strategy' },
        lazy: page(() => import('@/features/lab/StrategyDetailPage')),
      },
      {
        path: 'lab/backtests/:id',
        handle: { title: 'Backtest' },
        lazy: page(() => import('@/features/lab/BacktestDetailPage')),
      },

      {
        path: 'coach/chat',
        handle: { title: 'Coach' },
        lazy: page(() => import('@/features/coach/CoachChatPage')),
      },

      {
        path: 'settings',
        handle: { title: 'Settings' },
        lazy: page(() => import('@/features/settings/SettingsPage')),
        children: [
          { index: true, loader: () => redirect('/settings/profile') },
          {
            path: 'profile',
            lazy: async () => ({
              Component: (await import('@/features/settings/sections')).ProfileSection,
            }),
          },
          {
            path: 'risk',
            lazy: async () => ({
              Component: (await import('@/features/settings/sections')).RiskSection,
            }),
          },
          {
            path: 'buckets',
            lazy: async () => ({
              Component: (await import('@/features/settings/sections')).BucketsSection,
            }),
          },
          {
            path: 'integrations',
            lazy: async () => ({
              Component: (await import('@/features/settings/sections')).IntegrationsSection,
            }),
          },
          {
            path: 'llm',
            lazy: async () => ({
              Component: (await import('@/features/settings/sections')).LlmSection,
            }),
          },
          {
            path: 'security',
            lazy: async () => ({
              Component: (await import('@/features/settings/sections')).SecuritySection,
            }),
          },
          {
            path: 'export',
            lazy: async () => ({
              Component: (await import('@/features/settings/sections')).ExportSection,
            }),
          },
        ],
      },

      // Hidden dev-only playground proving the chart wrappers work.
      {
        path: 'dev/charts',
        handle: { title: 'Charts dev' },
        lazy: page(() => import('@/features/dev/ChartsDevPage')),
      },

      { path: '*', element: <NotFound /> },
    ],
  },
  { path: '/login', lazy: page(() => import('@/features/auth/LoginPage')) },
  { path: '/register', lazy: page(() => import('@/features/auth/RegisterPage')) },
  { path: '/totp', lazy: page(() => import('@/features/auth/TotpPage')) },
])
