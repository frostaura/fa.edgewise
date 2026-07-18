import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import type { SeriesMarker, Time } from 'lightweight-charts'
import { ArchiveIcon, FlaskConicalIcon, PlusIcon, StarIcon, Trash2Icon } from 'lucide-react'
import { toast } from 'sonner'

import type { LabInstrument, LabTimeframe } from '@/api/labApi'
import {
  useCreateZoneMutation,
  useDeleteZoneMutation,
  useGetInstrumentTradesQuery,
  useGetLabBarsQuery,
  useGetZonesQuery,
  useToggleZoneArchiveMutation,
} from '@/api/labApi'
import { getApiErrorMessage } from '@/api/types'
import { CandleChart, type OverlayLine } from '@/components/charts/CandleChart'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { bollinger, sma, toCandleBar } from '@/features/lab/chartIndicators'
import { InstrumentPicker } from '@/features/lab/components/InstrumentPicker'

const TIMEFRAMES: { value: LabTimeframe; label: string }[] = [
  { value: 'd1', label: '1D' },
  { value: 'h4', label: '4H' },
  { value: 'h1', label: '1H' },
  { value: 'w1', label: '1W' },
]

const MA_COLORS: Record<string, string> = {
  ma20: '#38bdf8',
  ma50: '#a78bfa',
  ma200: '#f59e0b',
}

const ZONE_COLOR = 'rgba(56, 189, 248, 0.55)'

function Stars({ strength }: { strength: number }) {
  return (
    <span className="inline-flex" aria-label={`strength ${strength} of 5`}>
      {[1, 2, 3, 4, 5].map((i) => (
        <StarIcon
          key={i}
          className={i <= strength ? 'size-3 text-warning' : 'size-3 text-muted-foreground/40'}
          fill={i <= strength ? 'currentColor' : 'none'}
          aria-hidden
        />
      ))}
    </span>
  )
}

export default function LabPage() {
  const [instrument, setInstrument] = useState<LabInstrument | null>(null)
  const [timeframe, setTimeframe] = useState<LabTimeframe>('d1')
  const [showMa, setShowMa] = useState<Record<'ma20' | 'ma50' | 'ma200', boolean>>({
    ma20: true,
    ma50: true,
    ma200: false,
  })
  const [showBollinger, setShowBollinger] = useState(false)
  const [showTrades, setShowTrades] = useState(false)
  const [addZoneOpen, setAddZoneOpen] = useState(false)
  const [zoneLow, setZoneLow] = useState('')
  const [zoneHigh, setZoneHigh] = useState('')
  const [zoneStrength, setZoneStrength] = useState('3')

  const instrumentId = instrument?.id
  const { data: apiBars = [], isFetching: barsLoading } = useGetLabBarsQuery(
    { instrumentId: instrumentId ?? '', timeframe, limit: 500 },
    { skip: !instrumentId },
  )
  const { data: zones = [] } = useGetZonesQuery(
    { instrumentId: instrumentId ?? '' },
    { skip: !instrumentId },
  )
  const { data: trades = [] } = useGetInstrumentTradesQuery(instrumentId ?? '', {
    skip: !instrumentId || !showTrades,
  })

  const [createZone, { isLoading: creatingZone }] = useCreateZoneMutation()
  const [toggleArchive] = useToggleZoneArchiveMutation()
  const [deleteZone] = useDeleteZoneMutation()

  const bars = useMemo(() => apiBars.map(toCandleBar), [apiBars])

  const overlays = useMemo(() => {
    const lines: OverlayLine[] = []
    if (bars.length === 0) return lines
    for (const [key, period] of [
      ['ma20', 20],
      ['ma50', 50],
      ['ma200', 200],
    ] as const) {
      if (showMa[key] && bars.length >= period) {
        lines.push({
          id: key,
          data: sma(bars, period),
          color: MA_COLORS[key],
          title: key.toUpperCase(),
        })
      }
    }
    if (showBollinger && bars.length >= 20) {
      const bb = bollinger(bars)
      lines.push({ id: 'bb-upper', data: bb.upper, color: 'rgba(148,163,184,0.7)', title: 'BB↑' })
      lines.push({ id: 'bb-lower', data: bb.lower, color: 'rgba(148,163,184,0.7)', title: 'BB↓' })
    }
    // Zones as translucent horizontal bands: a top and bottom boundary line per
    // zone spanning the loaded range (pragmatic band rendering on the shared
    // CandleChart overlay contract).
    if (bars.length > 1) {
      const first = bars[0].time
      const last = bars[bars.length - 1].time
      for (const zone of zones) {
        lines.push({
          id: `zone-${zone.id}-high`,
          data: [
            { time: first, value: zone.priceHigh },
            { time: last, value: zone.priceHigh },
          ],
          color: ZONE_COLOR,
          title: `zone ${zone.strength}★`,
        })
        lines.push({
          id: `zone-${zone.id}-low`,
          data: [
            { time: first, value: zone.priceLow },
            { time: last, value: zone.priceLow },
          ],
          color: ZONE_COLOR,
        })
      }
    }
    return lines
  }, [bars, showMa, showBollinger, zones])

  const markers = useMemo<SeriesMarker<Time>[]>(() => {
    if (!showTrades || bars.length === 0) return []
    const out: SeriesMarker<Time>[] = []
    for (const trade of trades) {
      if (trade.openedAt) {
        out.push({
          time: Math.floor(new Date(trade.openedAt).getTime() / 1000) as Time,
          position: trade.direction === 'short' ? 'aboveBar' : 'belowBar',
          shape: trade.direction === 'short' ? 'arrowDown' : 'arrowUp',
          color: '#38bdf8',
          text: `${trade.isPaper ? 'paper ' : ''}entry`,
        })
      }
      if (trade.closedAt) {
        out.push({
          time: Math.floor(new Date(trade.closedAt).getTime() / 1000) as Time,
          position: 'aboveBar',
          shape: 'circle',
          color: '#f59e0b',
          text: 'exit',
        })
      }
    }
    return out.sort((a, b) => (a.time as number) - (b.time as number))
  }, [trades, showTrades, bars])

  const submitZone = async () => {
    if (!instrumentId) return
    try {
      await createZone({
        instrumentId,
        priceLow: Number(zoneLow),
        priceHigh: Number(zoneHigh),
        strength: Number(zoneStrength),
        sourceTimeframe: timeframe,
      }).unwrap()
      toast.success('Zone added')
      setAddZoneOpen(false)
      setZoneLow('')
      setZoneHigh('')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not create the zone'))
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Lab"
        description="Chart workspace — draw zones, inspect your trades, design and test strategies."
        actions={
          <Button asChild variant="outline" size="sm">
            <Link to="/lab/strategies">Strategies</Link>
          </Button>
        }
      />

      <div className="flex flex-wrap items-center gap-3">
        <InstrumentPicker value={instrument} onChange={setInstrument} />
        <Tabs value={timeframe} onValueChange={(v) => setTimeframe(v as LabTimeframe)}>
          <TabsList>
            {TIMEFRAMES.map((tf) => (
              <TabsTrigger key={tf.value} value={tf.value}>
                {tf.label}
              </TabsTrigger>
            ))}
          </TabsList>
        </Tabs>
      </div>

      {!instrument ? (
        <EmptyState
          icon={FlaskConicalIcon}
          title="Pick an instrument"
          hint="Search an instrument to open its chart, draw support/resistance zones and overlay your trades."
        />
      ) : (
        <div className="grid gap-4 lg:grid-cols-[1fr_18rem]">
          <div className="flex flex-col gap-3">
            {/* Indicator + marker toggles */}
            <div className="flex flex-wrap items-center gap-4 text-sm">
              {(['ma20', 'ma50', 'ma200'] as const).map((key) => (
                <label key={key} className="flex items-center gap-1.5">
                  <Switch
                    checked={showMa[key]}
                    onCheckedChange={(v) => setShowMa((s) => ({ ...s, [key]: v }))}
                  />
                  {key.toUpperCase()}
                </label>
              ))}
              <label className="flex items-center gap-1.5">
                <Switch checked={showBollinger} onCheckedChange={setShowBollinger} />
                Bollinger
              </label>
              <label className="flex items-center gap-1.5">
                <Switch checked={showTrades} onCheckedChange={setShowTrades} />
                My trades
              </label>
            </div>

            {bars.length === 0 ? (
              <EmptyState
                title={barsLoading ? 'Loading bars…' : 'No stored bars'}
                hint={
                  barsLoading
                    ? undefined
                    : `No ${timeframe.toUpperCase()} history is stored for ${instrument.symbol} yet.`
                }
              />
            ) : (
              <CandleChart bars={bars} overlays={overlays} markers={markers} className="h-96" />
            )}
          </div>

          {/* Zone sidebar */}
          <Card className="h-fit">
            <CardHeader>
              <div className="flex items-center justify-between gap-2">
                <CardTitle>Zones</CardTitle>
                <Button size="sm" variant="outline" onClick={() => setAddZoneOpen((v) => !v)}>
                  <PlusIcon className="size-4" aria-hidden />
                  Add zone
                </Button>
              </div>
            </CardHeader>
            <CardContent className="flex flex-col gap-3">
              {addZoneOpen && (
                <div className="flex flex-col gap-2 rounded-lg border p-3">
                  <div className="grid grid-cols-2 gap-2">
                    <div className="flex flex-col gap-1">
                      <Label htmlFor="zone-low">Low</Label>
                      <Input
                        id="zone-low"
                        inputMode="decimal"
                        value={zoneLow}
                        onChange={(e) => setZoneLow(e.target.value)}
                      />
                    </div>
                    <div className="flex flex-col gap-1">
                      <Label htmlFor="zone-high">High</Label>
                      <Input
                        id="zone-high"
                        inputMode="decimal"
                        value={zoneHigh}
                        onChange={(e) => setZoneHigh(e.target.value)}
                      />
                    </div>
                  </div>
                  <div className="flex items-end gap-2">
                    <div className="flex flex-1 flex-col gap-1">
                      <Label>Strength</Label>
                      <Select value={zoneStrength} onValueChange={setZoneStrength}>
                        <SelectTrigger>
                          <SelectValue />
                        </SelectTrigger>
                        <SelectContent>
                          {[1, 2, 3, 4, 5].map((s) => (
                            <SelectItem key={s} value={String(s)}>
                              {s} star{s === 1 ? '' : 's'}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    </div>
                    <Button
                      size="sm"
                      onClick={submitZone}
                      disabled={creatingZone || zoneLow === '' || zoneHigh === ''}
                    >
                      Save
                    </Button>
                  </div>
                  <p className="text-xs text-muted-foreground">
                    Tagged {timeframe.toUpperCase()} — zones render as bands on the chart.
                  </p>
                </div>
              )}

              {zones.length === 0 && !addZoneOpen && (
                <p className="text-sm text-muted-foreground">
                  No zones yet. Add support/resistance bands to reuse in charts and alerts.
                </p>
              )}

              <ul className="flex flex-col gap-2">
                {zones.map((zone) => (
                  <li
                    key={zone.id}
                    className="flex items-center justify-between gap-2 rounded-lg border p-2 text-sm"
                  >
                    <div className="flex flex-col">
                      <span className="font-medium tabular-nums">
                        {zone.priceLow} – {zone.priceHigh}
                      </span>
                      <span className="flex items-center gap-1 text-xs text-muted-foreground">
                        <Stars strength={zone.strength} />
                        <Badge variant="outline">{zone.sourceTimeframe.toUpperCase()}</Badge>
                      </span>
                    </div>
                    <div className="flex items-center gap-1">
                      <Button
                        size="icon"
                        variant="ghost"
                        aria-label="Archive zone"
                        onClick={() => toggleArchive(zone.id)}
                      >
                        <ArchiveIcon className="size-4" aria-hidden />
                      </Button>
                      <Button
                        size="icon"
                        variant="ghost"
                        aria-label="Delete zone"
                        onClick={() => deleteZone(zone.id)}
                      >
                        <Trash2Icon className="size-4" aria-hidden />
                      </Button>
                    </div>
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>
        </div>
      )}
    </div>
  )
}
