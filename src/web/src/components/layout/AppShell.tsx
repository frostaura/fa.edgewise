import { Suspense } from 'react'
import { Outlet } from 'react-router'
import { WifiOffIcon } from 'lucide-react'

import { useAppSelector } from '@/app/hooks'
import { selectOnline } from '@/app/uiSlice'
import { CommandPalette } from '@/components/layout/CommandPalette'
import { LeftRail } from '@/components/layout/LeftRail'
import { MobileTabBar } from '@/components/layout/MobileTabBar'
import { TopBar } from '@/components/layout/TopBar'
import { Skeleton } from '@/components/ui/skeleton'

function OfflineBanner() {
  const online = useAppSelector(selectOnline)
  if (online) return null
  return (
    <div
      role="status"
      className="flex items-center justify-center gap-2 bg-warning px-4 py-1.5 text-sm font-medium text-warning-foreground"
    >
      <WifiOffIcon className="size-4" aria-hidden />
      You&rsquo;re offline — changes will sync when you reconnect.
    </div>
  )
}

function PageFallback() {
  return (
    <div className="flex flex-col gap-6" aria-busy>
      <div className="flex flex-col gap-2">
        <Skeleton className="h-7 w-48" />
        <Skeleton className="h-4 w-72" />
      </div>
      <Skeleton className="h-64 w-full rounded-xl" />
    </div>
  )
}

/** Authenticated layout: left rail (desktop), top bar, bottom tabs (mobile). */
export function AppShell() {
  return (
    <div className="min-h-dvh">
      <LeftRail />
      <div className="flex min-h-dvh flex-col pb-16 md:pb-0 md:pl-52">
        <TopBar />
        <OfflineBanner />
        <main className="mx-auto w-full max-w-6xl flex-1 px-4 py-6 sm:px-6">
          <Suspense fallback={<PageFallback />}>
            <Outlet />
          </Suspense>
        </main>
      </div>
      <MobileTabBar />
      <CommandPalette />
    </div>
  )
}
