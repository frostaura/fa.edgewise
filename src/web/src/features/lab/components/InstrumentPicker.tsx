import { useState } from 'react'
import { SearchIcon } from 'lucide-react'

import type { LabInstrument } from '@/api/labApi'
import { useSearchLabInstrumentsQuery } from '@/api/labApi'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from '@/components/ui/popover'

export interface InstrumentPickerProps {
  value: LabInstrument | null
  onChange: (instrument: LabInstrument) => void
  placeholder?: string
}

/**
 * Instrument search picker. Uses the market vertical's search endpoint when
 * available and transparently falls back to the lab-owned instrument search.
 */
export function InstrumentPicker({ value, onChange, placeholder }: InstrumentPickerProps) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const { data: results = [], isFetching } = useSearchLabInstrumentsQuery(query, {
    skip: !open,
  })

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="justify-start gap-2 font-normal">
          <SearchIcon className="size-4 text-muted-foreground" aria-hidden />
          {value ? (
            <span>
              <span className="font-medium">{value.symbol}</span>
              <span className="ml-1 hidden text-muted-foreground sm:inline">{value.name}</span>
            </span>
          ) : (
            <span className="text-muted-foreground">{placeholder ?? 'Pick instrument…'}</span>
          )}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-72 p-2">
        <Input
          autoFocus
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search symbol or name…"
          className="mb-2"
        />
        <div className="flex max-h-56 flex-col gap-0.5 overflow-y-auto">
          {isFetching && <p className="p-2 text-xs text-muted-foreground">Searching…</p>}
          {!isFetching && results.length === 0 && (
            <p className="p-2 text-xs text-muted-foreground">No instruments found.</p>
          )}
          {results.map((instrument) => (
            <button
              key={instrument.id}
              type="button"
              className="flex items-center justify-between rounded-md px-2 py-1.5 text-left text-sm hover:bg-muted"
              onClick={() => {
                onChange(instrument)
                setOpen(false)
              }}
            >
              <span className="font-medium">{instrument.symbol}</span>
              <span className="max-w-40 truncate text-xs text-muted-foreground">
                {instrument.name}
              </span>
            </button>
          ))}
        </div>
      </PopoverContent>
    </Popover>
  )
}
