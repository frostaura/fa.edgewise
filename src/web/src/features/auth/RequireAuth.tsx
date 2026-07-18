import type { ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router'

import { useAppSelector } from '@/app/hooks'
import { selectAuthStatus } from '@/features/auth/authSlice'
import { LogoMark } from '@/components/brand/LogoMark'

function Splash() {
  return (
    <div className="flex min-h-dvh items-center justify-center bg-background">
      <div className="flex flex-col items-center gap-3">
        <LogoMark className="size-12 animate-pulse" />
        <p className="text-sm text-muted-foreground">Restoring your session…</p>
      </div>
    </div>
  )
}

/**
 * Route guard for the authenticated shell. While a stored refresh token is
 * being exchanged we show a splash; unauthenticated visitors are redirected
 * to /login with their intended destination preserved.
 */
export function RequireAuth({ children }: { children: ReactNode }) {
  const status = useAppSelector(selectAuthStatus)
  const location = useLocation()

  if (status === 'restoring') return <Splash />
  if (status !== 'authenticated') {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  }
  return children
}
