import * as React from 'react'
import { useNavigate } from 'react-router'
import { ClipboardPenIcon, PlusIcon, ZapIcon } from 'lucide-react'
import { toast } from 'sonner'

import { useAppDispatch, useAppSelector } from '@/app/hooks'
import { selectPaletteOpen, setPaletteOpen, togglePalette } from '@/app/uiSlice'
import { primaryNav, secondaryNav } from '@/components/layout/nav'
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandSeparator,
  CommandShortcut,
} from '@/components/ui/command'

/** Global ⌘K / Ctrl+K palette: navigation plus quick actions. */
export function CommandPalette() {
  const dispatch = useAppDispatch()
  const open = useAppSelector(selectPaletteOpen)
  const navigate = useNavigate()

  React.useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'k' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault()
        dispatch(togglePalette())
      }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [dispatch])

  const close = () => dispatch(setPaletteOpen(false))
  const go = (to: string) => {
    close()
    void navigate(to)
  }
  const stub = (name: string) => {
    close()
    toast(name, { description: 'This action is on its way — hang tight.' })
  }

  return (
    <CommandDialog open={open} onOpenChange={(next) => dispatch(setPaletteOpen(next))}>
      <CommandInput placeholder="Type a command or search…" />
      <CommandList>
        <CommandEmpty>No results found.</CommandEmpty>
        <CommandGroup heading="Actions">
          <CommandItem onSelect={() => go('/journal/plans/new')}>
            <PlusIcon aria-hidden />
            New Plan
            <CommandShortcut>P</CommandShortcut>
          </CommandItem>
          <CommandItem onSelect={() => stub('Quick Log')}>
            <ZapIcon aria-hidden />
            Quick Log
          </CommandItem>
          <CommandItem onSelect={() => stub('Log Decision')}>
            <ClipboardPenIcon aria-hidden />
            Log Decision
          </CommandItem>
        </CommandGroup>
        <CommandSeparator />
        <CommandGroup heading="Go to">
          {[...primaryNav, ...secondaryNav].map((item) => {
            const Icon = item.icon
            return (
              <CommandItem key={item.to} onSelect={() => go(item.to)}>
                <Icon aria-hidden />
                {item.label}
              </CommandItem>
            )
          })}
        </CommandGroup>
      </CommandList>
    </CommandDialog>
  )
}
