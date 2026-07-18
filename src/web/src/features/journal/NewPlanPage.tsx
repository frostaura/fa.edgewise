import * as React from 'react'
import { useNavigate } from 'react-router'
import { CheckIcon, ChevronsUpDownIcon, PlusIcon, TriangleAlertIcon } from 'lucide-react'
import { toast } from 'sonner'

import {
  useCreateInstrumentMutation,
  useCreatePlanMutation,
  useGetPlanLookupsQuery,
  useGetSizePreviewQuery,
  useSearchInstrumentsQuery,
  type InstrumentSummary,
  type PlanTemplate,
  type TradeDirection,
} from '@/api/journalApi'
import { getApiErrorMessage } from '@/api/types'
import { PageHeader } from '@/components/domain/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from '@/components/ui/command'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'
import { formatMinor, isSizeOverride } from '@/features/journal/sizing'

interface ChecklistItem {
  id: string
  label: string
}

interface TemplatePrefill {
  direction?: TradeDirection
  setupTag?: string
  triggerText?: string
  targetRule?: unknown
}

function parseChecklist(json?: string): ChecklistItem[] {
  if (!json) return []
  try {
    const parsed = JSON.parse(json) as ChecklistItem[]
    return Array.isArray(parsed) ? parsed.filter((i) => i.id && i.label) : []
  } catch {
    return []
  }
}

function useDebounced<T>(value: T, delay = 400): T {
  const [debounced, setDebounced] = React.useState(value)
  React.useEffect(() => {
    const handle = setTimeout(() => setDebounced(value), delay)
    return () => clearTimeout(handle)
  }, [value, delay])
  return debounced
}

/** Instrument picker: local lookups + defensive market search + free-text create. */
function InstrumentPicker({
  instruments,
  value,
  onChange,
}: {
  instruments: InstrumentSummary[]
  value: InstrumentSummary | null
  onChange: (instrument: InstrumentSummary) => void
}) {
  const [open, setOpen] = React.useState(false)
  const [query, setQuery] = React.useState('')
  const debouncedQuery = useDebounced(query, 300)
  const [createInstrument, { isLoading: creating }] = useCreateInstrumentMutation()

  // The market vertical's search endpoint, wired defensively: errors mean
  // "endpoint not there yet" and we silently fall back to the local catalogue.
  const { data: searchResults } = useSearchInstrumentsQuery(debouncedQuery, {
    skip: debouncedQuery.trim().length < 2,
  })

  const lower = query.trim().toLowerCase()
  const local = instruments.filter(
    (i) =>
      lower.length === 0 ||
      i.symbol.toLowerCase().includes(lower) ||
      i.name.toLowerCase().includes(lower),
  )
  const merged = [
    ...local,
    ...(searchResults ?? []).filter((r) => !local.some((l) => l.id === r.id)),
  ].slice(0, 30)

  const createFromQuery = async () => {
    const symbol = query.trim().toUpperCase()
    if (!symbol) return
    try {
      const instrument = await createInstrument({ symbol }).unwrap()
      onChange(instrument)
      setOpen(false)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not create the instrument'))
    }
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          role="combobox"
          aria-expanded={open}
          className="w-full justify-between font-normal"
        >
          {value ? (
            <span>
              <span className="font-medium">{value.symbol}</span>
              <span className="ml-2 text-muted-foreground">{value.name}</span>
            </span>
          ) : (
            <span className="text-muted-foreground">Search instrument…</span>
          )}
          <ChevronsUpDownIcon className="size-4 opacity-50" aria-hidden />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-72 p-0" align="start">
        <Command shouldFilter={false}>
          <CommandInput
            placeholder="Symbol or name…"
            value={query}
            onValueChange={setQuery}
          />
          <CommandList>
            <CommandEmpty>
              {query.trim() ? (
                <Button
                  variant="ghost"
                  size="sm"
                  className="w-full"
                  disabled={creating}
                  onClick={() => void createFromQuery()}
                >
                  <PlusIcon aria-hidden />
                  Create “{query.trim().toUpperCase()}”
                </Button>
              ) : (
                'Type to search.'
              )}
            </CommandEmpty>
            <CommandGroup>
              {merged.map((instrument) => (
                <CommandItem
                  key={instrument.id}
                  value={instrument.id}
                  onSelect={() => {
                    onChange(instrument)
                    setOpen(false)
                  }}
                >
                  <CheckIcon
                    className={cn(
                      'size-4',
                      value?.id === instrument.id ? 'opacity-100' : 'opacity-0',
                    )}
                    aria-hidden
                  />
                  <span className="font-medium">{instrument.symbol}</span>
                  <span className="truncate text-muted-foreground">{instrument.name}</span>
                </CommandItem>
              ))}
            </CommandGroup>
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  )
}

export default function NewPlanPage() {
  const navigate = useNavigate()
  const { data: lookups, isLoading } = useGetPlanLookupsQuery()
  const [createPlan, { isLoading: submitting }] = useCreatePlanMutation()

  const [template, setTemplate] = React.useState<PlanTemplate | null>(null)
  const [instrument, setInstrument] = React.useState<InstrumentSummary | null>(null)
  const [bucketId, setBucketId] = React.useState('')
  const [riskProfileId, setRiskProfileId] = React.useState('')
  const [direction, setDirection] = React.useState<TradeDirection>('long')
  const [setupTag, setSetupTag] = React.useState('')
  const [triggerText, setTriggerText] = React.useState('')
  const [entryPrice, setEntryPrice] = React.useState('')
  const [stopPrice, setStopPrice] = React.useState('')
  const [targetRule, setTargetRule] = React.useState('')
  const [invalidationNote, setInvalidationNote] = React.useState('')
  const [sizeQty, setSizeQty] = React.useState('')
  const [sizeTouched, setSizeTouched] = React.useState(false)
  const [isPaper, setIsPaper] = React.useState(false)
  const [checked, setChecked] = React.useState<Record<string, boolean>>({})

  // Sensible defaults once lookups land.
  React.useEffect(() => {
    if (!lookups) return
    if (!bucketId) {
      const trading = lookups.buckets.find((b) => b.kind === 'trading') ?? lookups.buckets[0]
      if (trading) setBucketId(trading.id)
    }
    if (!riskProfileId) {
      const active = lookups.riskProfiles.find((p) => p.isActive) ?? lookups.riskProfiles[0]
      if (active) setRiskProfileId(active.id)
    }
  }, [lookups, bucketId, riskProfileId])

  const applyTemplate = (t: PlanTemplate) => {
    setTemplate(t)
    setChecked({})
    try {
      const prefill = JSON.parse(t.prefillJson ?? '{}') as TemplatePrefill
      if (prefill.direction === 'long' || prefill.direction === 'short') {
        setDirection(prefill.direction)
      }
      if (prefill.setupTag) setSetupTag(prefill.setupTag)
      if (prefill.triggerText) setTriggerText(prefill.triggerText)
      if (prefill.targetRule) setTargetRule(JSON.stringify(prefill.targetRule))
    } catch {
      // A template without prefill is still usable.
    }
  }

  // Live size auto-calc via the server preview (debounced).
  const debouncedEntry = useDebounced(entryPrice)
  const debouncedStop = useDebounced(stopPrice)
  const previewArgs =
    bucketId && Number(debouncedEntry) > 0 && Number(debouncedStop) > 0
      ? {
          bucketId,
          entryPrice: Number(debouncedEntry),
          stopPrice: Number(debouncedStop),
          riskProfileId: riskProfileId || undefined,
        }
      : null
  const { data: preview } = useGetSizePreviewQuery(previewArgs ?? { bucketId: '', entryPrice: 0, stopPrice: 0 }, {
    skip: previewArgs === null,
  })

  const suggested = previewArgs !== null ? preview : undefined
  const effectiveQty = sizeTouched ? Number(sizeQty || '0') : (suggested?.suggestedQty ?? 0)
  const overridden =
    sizeTouched && suggested !== undefined && isSizeOverride(effectiveQty, suggested.suggestedQty)

  const checklist = parseChecklist(template?.checklistJson)
  const allConfirmed = checklist.length === 0 || checklist.every((item) => checked[item.id])

  const canSubmit =
    instrument !== null &&
    bucketId !== '' &&
    Number(stopPrice) > 0 &&
    effectiveQty > 0 &&
    allConfirmed &&
    !submitting

  const submit = async () => {
    if (!instrument) return
    try {
      const plan = await createPlan({
        templateId: template?.id,
        instrumentId: instrument.id,
        bucketId,
        riskProfileId: riskProfileId || undefined,
        direction,
        setupTag: setupTag || undefined,
        triggerText: triggerText || undefined,
        stopPrice: Number(stopPrice),
        targetRuleJson: targetRule || undefined,
        sizeQty: effectiveQty,
        sizeOverridden: overridden,
        invalidationNote: invalidationNote || undefined,
        isPaper,
        checklistConfirmedJson:
          checklist.length > 0
            ? JSON.stringify(Object.fromEntries(checklist.map((i) => [i.id, Boolean(checked[i.id])])))
            : undefined,
        entryPrice: Number(entryPrice) > 0 ? Number(entryPrice) : undefined,
      }).unwrap()
      toast.success(`Plan for ${instrument.symbol} is live`, {
        description: 'Promote it from the inbox when your entry fills.',
      })
      void navigate(`/journal?plan=${plan.id}`)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not create the plan'))
    }
  }

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6">
      <PageHeader
        title="New Plan"
        description="Commit to entry, stop, size and invalidation before you touch the buy button."
      />

      {/* 1 — Template picker */}
      <section className="flex flex-col gap-2">
        <Label>Playbook</Label>
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          {(lookups?.templates ?? []).map((t) => (
            <button
              key={t.id}
              type="button"
              onClick={() => applyTemplate(t)}
              className={cn(
                'flex flex-col gap-1 rounded-xl border p-3 text-left transition-colors hover:bg-muted/50',
                template?.id === t.id && 'border-primary bg-primary/5',
              )}
            >
              <span className="text-sm font-medium">{t.name}</span>
              <span className="line-clamp-2 text-xs text-muted-foreground">{t.description}</span>
              {!t.shipped && (
                <Badge variant="outline" className="mt-1 self-start">
                  mine
                </Badge>
              )}
            </button>
          ))}
          {isLoading && <div className="col-span-2 h-20 animate-pulse rounded-xl bg-muted sm:col-span-4" />}
        </div>
      </section>

      {/* 2 — Instrument, bucket, direction */}
      <section className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <Label>Instrument</Label>
          <InstrumentPicker
            instruments={lookups?.instruments ?? []}
            value={instrument}
            onChange={setInstrument}
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label>Direction</Label>
          <div className="flex gap-1">
            <Button
              type="button"
              variant={direction === 'long' ? 'default' : 'outline'}
              className="flex-1"
              onClick={() => setDirection('long')}
            >
              Long
            </Button>
            <Button
              type="button"
              variant={direction === 'short' ? 'default' : 'outline'}
              className="flex-1"
              onClick={() => setDirection('short')}
            >
              Short
            </Button>
          </div>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label>Bucket</Label>
          <Select value={bucketId} onValueChange={setBucketId}>
            <SelectTrigger className="w-full">
              <SelectValue placeholder="Bucket" />
            </SelectTrigger>
            <SelectContent>
              {(lookups?.buckets ?? []).map((b) => (
                <SelectItem key={b.id} value={b.id}>
                  {b.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label>Risk profile</Label>
          <Select value={riskProfileId} onValueChange={setRiskProfileId}>
            <SelectTrigger className="w-full">
              <SelectValue placeholder="Risk profile" />
            </SelectTrigger>
            <SelectContent>
              {(lookups?.riskProfiles ?? []).map((p) => (
                <SelectItem key={p.id} value={p.id}>
                  {p.name} ({p.riskPct}%)
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </section>

      {/* 3 — The six fields with live sizing */}
      <section className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="np-setup">Setup tag</Label>
          <Input id="np-setup" value={setupTag} onChange={(e) => setSetupTag(e.target.value)} />
        </div>
        <div className="flex flex-col gap-1.5 sm:col-span-1">
          <Label htmlFor="np-entry">Planned entry price</Label>
          <Input
            id="np-entry"
            type="number"
            inputMode="decimal"
            step="any"
            min="0"
            value={entryPrice}
            onChange={(e) => setEntryPrice(e.target.value)}
          />
        </div>
        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Label htmlFor="np-trigger">Trigger</Label>
          <Textarea
            id="np-trigger"
            rows={2}
            value={triggerText}
            onChange={(e) => setTriggerText(e.target.value)}
            placeholder="What exactly must print before you enter?"
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="np-stop">Stop price</Label>
          <Input
            id="np-stop"
            type="number"
            inputMode="decimal"
            step="any"
            min="0"
            value={stopPrice}
            onChange={(e) => setStopPrice(e.target.value)}
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="np-size">
            Size (qty)
            {overridden && (
              <Badge variant="warning" className="ml-1.5">
                <TriangleAlertIcon aria-hidden />
                override
              </Badge>
            )}
          </Label>
          <Input
            id="np-size"
            type="number"
            inputMode="decimal"
            step="any"
            min="0"
            value={sizeTouched ? sizeQty : String(suggested?.suggestedQty ?? '')}
            onChange={(e) => {
              setSizeQty(e.target.value)
              setSizeTouched(true)
            }}
            placeholder="auto"
          />
        </div>

        {/* Live sizing readout */}
        <Card className="gap-0 py-3 sm:col-span-2">
          <CardContent className="flex flex-wrap items-center gap-x-6 gap-y-1 px-4 text-sm">
            {suggested ? (
              <>
                <span>
                  <span className="text-muted-foreground">Suggested qty </span>
                  <span className="font-mono font-medium tabular-nums">
                    {suggested.suggestedQty}
                  </span>
                </span>
                <span>
                  <span className="text-muted-foreground">1R = </span>
                  <span className="font-mono font-medium tabular-nums">
                    {formatMinor(suggested.rValueMinor)}
                  </span>
                </span>
                <span>
                  <span className="text-muted-foreground">Notional </span>
                  <span className="font-mono font-medium tabular-nums">
                    {formatMinor(suggested.notionalMinor)}
                  </span>
                </span>
                {sizeTouched && (
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => {
                      setSizeTouched(false)
                      setSizeQty('')
                    }}
                  >
                    Use suggested
                  </Button>
                )}
              </>
            ) : (
              <span className="text-muted-foreground">
                Enter planned entry and stop prices to auto-size the position off your risk
                profile.
              </span>
            )}
          </CardContent>
        </Card>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="np-target">Target rule (JSON or free text)</Label>
          <Input
            id="np-target"
            value={targetRule}
            onChange={(e) => setTargetRule(e.target.value)}
            className="font-mono text-xs"
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="np-invalidation">Invalidation</Label>
          <Input
            id="np-invalidation"
            value={invalidationNote}
            onChange={(e) => setInvalidationNote(e.target.value)}
            placeholder="What kills this idea?"
          />
        </div>
      </section>

      {/* 4 — Checklist confirm */}
      {checklist.length > 0 && (
        <section className="flex flex-col gap-2 rounded-xl border p-4">
          <Label>Entry checklist — confirm every line before the plan goes live</Label>
          {checklist.map((item) => (
            <label key={item.id} className="flex items-start gap-2 text-sm">
              <Checkbox
                checked={Boolean(checked[item.id])}
                onCheckedChange={(v) => setChecked((c) => ({ ...c, [item.id]: v === true }))}
                className="mt-0.5"
              />
              {item.label}
            </label>
          ))}
        </section>
      )}

      {/* 5 — Submit */}
      <section className="flex items-center justify-between gap-4 pb-8">
        <Label className="flex items-center gap-2 text-sm text-muted-foreground">
          <Switch checked={isPaper} onCheckedChange={setIsPaper} />
          Paper trade
        </Label>
        <div className="flex items-center gap-2">
          <Button variant="outline" onClick={() => void navigate('/journal')}>
            Discard
          </Button>
          <Button onClick={() => void submit()} disabled={!canSubmit}>
            {submitting ? 'Creating…' : 'Create plan'}
          </Button>
        </div>
      </section>
    </div>
  )
}
