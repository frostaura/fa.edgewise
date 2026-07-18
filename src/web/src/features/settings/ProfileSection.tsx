import { useMemo, useState } from 'react'
import { CheckIcon, ChevronDownIcon } from 'lucide-react'
import { toast } from 'sonner'

import { useGetMeQuery, useUpdateMeMutation, type MeUser } from '@/api/settingsApi'
import { getApiErrorMessage } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from '@/components/ui/command'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'

const currencies = [
  { code: 'ZAR', label: 'ZAR — South African rand' },
  { code: 'USD', label: 'USD — US dollar' },
  { code: 'EUR', label: 'EUR — Euro' },
  { code: 'GBP', label: 'GBP — British pound' },
]

const commonTimezones = [
  'Africa/Johannesburg',
  'Africa/Lagos',
  'Africa/Nairobi',
  'Europe/London',
  'Europe/Berlin',
  'Europe/Paris',
  'Europe/Zurich',
  'America/New_York',
  'America/Chicago',
  'America/Los_Angeles',
  'America/Sao_Paulo',
  'Asia/Dubai',
  'Asia/Singapore',
  'Asia/Hong_Kong',
  'Asia/Tokyo',
  'Australia/Sydney',
  'UTC',
]

function allTimezones(): string[] {
  try {
    const supported = Intl.supportedValuesOf('timeZone')
    const rest = supported.filter((tz) => !commonTimezones.includes(tz))
    return [...commonTimezones, ...rest]
  } catch {
    return commonTimezones
  }
}

function TimezoneCombobox({ value, onChange }: { value: string; onChange: (tz: string) => void }) {
  const [open, setOpen] = useState(false)
  const timezones = useMemo(() => allTimezones(), [])

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          role="combobox"
          aria-expanded={open}
          className="w-full justify-between font-normal sm:w-80"
        >
          {value || 'Select a timezone…'}
          <ChevronDownIcon className="size-4 opacity-50" aria-hidden />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-80 p-0" align="start">
        <Command>
          <CommandInput placeholder="Search timezones…" />
          <CommandList>
            <CommandEmpty>No timezone found.</CommandEmpty>
            <CommandGroup>
              {timezones.map((tz) => (
                <CommandItem
                  key={tz}
                  value={tz}
                  onSelect={(selected) => {
                    onChange(selected)
                    setOpen(false)
                  }}
                >
                  <CheckIcon className={cn('size-4', tz === value ? 'opacity-100' : 'opacity-0')} />
                  {tz}
                </CommandItem>
              ))}
            </CommandGroup>
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  )
}

function ProfileForm({ me }: { me: MeUser }) {
  const [baseCurrency, setBaseCurrency] = useState(me.baseCurrency)
  const [timezone, setTimezone] = useState(me.timezone || 'Africa/Johannesburg')
  const [updateMe, updateState] = useUpdateMeMutation()

  const dirty = baseCurrency !== me.baseCurrency || timezone !== me.timezone

  const save = async () => {
    try {
      await updateMe({ baseCurrency, timezone }).unwrap()
      toast.success('Profile saved.')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not save your profile.'))
    }
  }

  const memberSince = new Date(me.createdAt).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
  })

  return (
    <div className="flex flex-col gap-4">
      <Card>
        <CardHeader>
          <CardTitle>Account</CardTitle>
          <CardDescription>
            Signed in as <span className="font-medium text-foreground">{me.email}</span> · member
            since {memberSince}
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-5">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="base-currency">Base currency</Label>
            <Select value={baseCurrency} onValueChange={setBaseCurrency}>
              <SelectTrigger id="base-currency" className="w-full sm:w-80">
                <SelectValue placeholder="Select a currency" />
              </SelectTrigger>
              <SelectContent>
                {currencies.map((c) => (
                  <SelectItem key={c.code} value={c.code}>
                    {c.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <p className="text-xs text-muted-foreground">
              All portfolio values, stops and reports are shown in this currency.
            </p>
          </div>

          <div className="flex flex-col gap-1.5">
            <Label>Timezone</Label>
            <TimezoneCombobox value={timezone} onChange={setTimezone} />
            <p className="text-xs text-muted-foreground">
              Daily stops and weekly reviews roll over at midnight in this timezone.
            </p>
          </div>

          <Button
            onClick={() => void save()}
            disabled={!dirty || updateState.isLoading}
            className="self-start"
          >
            {updateState.isLoading ? 'Saving…' : 'Save changes'}
          </Button>
        </CardContent>
      </Card>
    </div>
  )
}

export function ProfileSection() {
  const { data: me, isLoading, isError } = useGetMeQuery()

  if (isLoading) return <Skeleton className="h-64 w-full" />
  if (isError || !me) {
    return <p className="text-sm text-destructive">Could not load your profile. Try refreshing.</p>
  }

  return <ProfileForm me={me} key={me.baseCurrency + me.timezone} />
}
