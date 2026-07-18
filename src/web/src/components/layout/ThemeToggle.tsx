import { MoonIcon, SunIcon } from 'lucide-react'

import { useAppDispatch, useAppSelector } from '@/app/hooks'
import { selectTheme, setTheme } from '@/app/uiSlice'
import { resolveIsDark } from '@/lib/theme'
import { Button } from '@/components/ui/button'

/** Sun/moon toggle. Persists the explicit choice via the store listener. */
export function ThemeToggle() {
  const dispatch = useAppDispatch()
  const theme = useAppSelector(selectTheme)
  const isDark = resolveIsDark(theme)

  return (
    <Button
      variant="ghost"
      size="icon"
      aria-label={isDark ? 'Switch to light theme' : 'Switch to dark theme'}
      onClick={() => dispatch(setTheme(isDark ? 'light' : 'dark'))}
    >
      {isDark ? <SunIcon aria-hidden /> : <MoonIcon aria-hidden />}
    </Button>
  )
}
