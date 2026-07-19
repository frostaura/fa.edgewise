import { useMatches, useNavigate } from 'react-router'
import { LogOutIcon, SearchIcon, UserIcon } from 'lucide-react'

import { useLogoutMutation } from '@/api/authApi'
import { useAppDispatch, useAppSelector } from '@/app/hooks'
import { selectCurrentUser } from '@/features/auth/authSlice'
import { togglePalette } from '@/app/uiSlice'
import { LogoMark } from '@/components/brand/LogoMark'
import { NotificationsBell } from '@/components/layout/NotificationsBell'
import { ThemeToggle } from '@/components/layout/ThemeToggle'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'

interface TitleHandle {
  title?: string
}

/** Sticky top bar: page title, palette trigger, theme, notifications, user. */
export function TopBar() {
  const matches = useMatches()
  const titledMatch = [...matches]
    .reverse()
    .find((m) => (m.handle as TitleHandle | undefined)?.title)
  const title = (titledMatch?.handle as TitleHandle | undefined)?.title

  const dispatch = useAppDispatch()
  const navigate = useNavigate()
  const user = useAppSelector(selectCurrentUser)
  const [logout, { isLoading: loggingOut }] = useLogoutMutation()

  const initial = user?.email?.charAt(0).toUpperCase() ?? '?'

  return (
    <header className="sticky top-0 z-30 flex h-14 items-center gap-2 border-b bg-background/90 px-4 backdrop-blur">
      <div className="flex items-center gap-2 md:hidden">
        <LogoMark className="size-7" />
      </div>
      <h1 className="truncate text-base font-semibold">{title ?? 'Edgewise'}</h1>
      <div className="ml-auto flex items-center gap-1">
        <Button
          variant="outline"
          size="sm"
          className="hidden gap-2 text-muted-foreground sm:inline-flex"
          onClick={() => dispatch(togglePalette())}
        >
          <SearchIcon aria-hidden />
          <span>Search…</span>
          <kbd className="rounded border bg-muted px-1.5 text-xs">⌘K</kbd>
        </Button>
        <Button
          variant="ghost"
          size="icon"
          className="sm:hidden"
          aria-label="Open command palette"
          onClick={() => dispatch(togglePalette())}
        >
          <SearchIcon aria-hidden />
        </Button>
        <ThemeToggle />
        <NotificationsBell />
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label="Account menu" className="rounded-full">
              <Avatar>
                <AvatarFallback>{initial}</AvatarFallback>
              </Avatar>
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="w-56">
            <DropdownMenuLabel className="flex flex-col gap-0.5">
              <span className="text-sm font-medium text-foreground">Signed in</span>
              <span className="truncate font-normal">{user?.email ?? 'Unknown user'}</span>
            </DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => void navigate('/settings/profile')}>
              <UserIcon aria-hidden />
              Profile
            </DropdownMenuItem>
            <DropdownMenuItem
              variant="destructive"
              disabled={loggingOut}
              onSelect={() => void logout()}
            >
              <LogOutIcon aria-hidden />
              Sign out
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </header>
  )
}
