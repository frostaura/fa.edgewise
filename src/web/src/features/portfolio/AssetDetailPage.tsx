import { useEffect, useMemo, useRef, useState } from 'react'
import { useParams } from 'react-router'
import type { SeriesMarker, Time } from 'lightweight-charts'
import { ExternalLinkIcon, LineChartIcon } from 'lucide-react'
import { toast } from 'sonner'

import {
  useGetAssetBarsQuery,
  useGetAssetCatalystsQuery,
  useGetAssetNewsQuery,
  useGetAssetTradesQuery,
  useGetAssetZonesQuery,
  useGetDividendsQuery,
  useGetDividendSummaryQuery,
  useGetHoldingQuery,
  useGetHoldingsQuery,
  useUpdateThesisMutation,
  type Holding,
} from '@/api/portfolioApi'
import { getApiErrorMessage } from '@/api/types'
import { CandleChart, type OverlayLine } from '@/components/charts/CandleChart'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { StaleBadge } from '@/components/domain/StaleBadge'
import { StatCard } from '@/components/domain/StatCard'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'
import { formatMinor, formatMinorSigned, formatPct, formatPctSigned, signTone } from './lib/money'

const barTime = (ts: string): number => Math.floor(Date.parse(ts) / 1000)
const markerTime = (ts: string): Time => barTime(ts) as Time

export default function AssetDetailPage() {
  const { instrumentId = '' } = useParams<{ instrumentId: string }>()
  const { data: holdings, isLoading } = useGetHoldingsQuery()

  const assetHoldings = useMemo(
    () => (holdings ?? []).filter((h) => h.instrument.id === instrumentId),
    [holdings, instrumentId],
  )
  const primary = assetHoldings.length
    ? assetHoldings.reduce((a, b) => (b.valueMinor > a.valueMinor ? b : a))
    : undefined

  if (isLoading) {
    return <Skeleton className="h-64 w-full rounded-xl" />
  }

  if (!primary) {
    return (
      <div className="flex flex-col gap-6">
        <PageHeader title="Asset" description="Position, zones and history for one instrument." />
        <EmptyState
          icon={LineChartIcon}
          title="You do not hold this instrument"
          hint="Add a holding with lots from the Portfolio page to see the position, thesis and dividends here."
        />
      </div>
    )
  }

  const totalValue = assetHoldings.reduce((sum, h) => sum + h.valueMinor, 0)
  const totalCost = assetHoldings.reduce((sum, h) => sum + h.costBasisMinor, 0)
  const totalPnl = totalValue - totalCost

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={primary.instrument.name}
        description={`${primary.instrument.symbol} · ${primary.instrument.assetClass}`}
        actions={
          <div className="flex items-center gap-2">
          {primary.price != null && (
            <span className="text-lg font-semibold tabular-nums">
              {primary.price.toLocaleString()} {primary.instrument.currency}
            </span>
          )}
          {primary.stale && (
            <StaleBadge updatedAt={primary.priceAsOf ?? new Date(Date.now() - 86_400_000)} />
          )}
          </div>
        }
      />

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <StatCard label="Quantity" value={assetHoldings.reduce((s, h) => s + h.qty, 0).toLocaleString()} />
        <StatCard
          label="Avg cost"
          value={primary.avgCostMinor != null ? formatMinor(primary.avgCostMinor) : '—'}
        />
        <StatCard label="Value" value={formatMinor(totalValue)} />
        <StatCard
          label="Unrealised P&L"
          value={<span className={signTone(totalPnl)}>{formatMinorSigned(totalPnl)}</span>}
          delta={totalCost > 0 ? { value: totalPnl, label: formatPctSigned(totalPnl / totalCost) } : undefined}
        />
      </div>

      <PriceSection instrumentId={instrumentId} />

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <PositionCard holding={primary} />
        <ThesisCard holding={primary} />
      </div>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <DividendsCard holding={primary} />
        <NewsAndCatalystsCard instrumentId={instrumentId} />
      </div>
    </div>
  )
}

/** Candles with the user's trades and zones overlaid — every input defensive. */
function PriceSection({ instrumentId }: { instrumentId: string }) {
  const { data: bars = [] } = useGetAssetBarsQuery({ instrumentId, timeframe: 'd1', limit: 240 })
  const { data: trades = [] } = useGetAssetTradesQuery(instrumentId)
  const { data: zones = [] } = useGetAssetZonesQuery(instrumentId)

  if (bars.length === 0) {
    return (
      <Card className="py-4">
        <CardContent className="px-4 text-sm text-muted-foreground">
          No price history available for this instrument yet.
        </CardContent>
      </Card>
    )
  }

  const chartBars = bars.map((b) => ({
    time: barTime(b.ts),
    open: b.o,
    high: b.h,
    low: b.l,
    close: b.c,
    volume: b.v,
  }))
  const first = chartBars[0].time
  const last = chartBars[chartBars.length - 1].time

  const markers: SeriesMarker<Time>[] = trades
    .flatMap((t) => {
      const list: SeriesMarker<Time>[] = [
        {
          time: markerTime(t.openedAt),
          position: t.direction === 'long' ? 'belowBar' : 'aboveBar',
          color: t.direction === 'long' ? '#2f9e8f' : '#e2704a',
          shape: t.direction === 'long' ? 'arrowUp' : 'arrowDown',
          text: `${t.direction === 'long' ? 'Buy' : 'Sell'} ${t.qty}`,
        },
      ]
      if (t.closedAt) {
        list.push({
          time: markerTime(t.closedAt),
          position: 'aboveBar',
          color: '#8290a5',
          shape: 'circle',
          text: 'Exit',
        })
      }
      return list
    })
    .sort((a, b) => Number(a.time) - Number(b.time))

  const overlays: OverlayLine[] = zones.flatMap((z) => [
    {
      id: `zone-${z.id}-low`,
      color: '#c76fd180',
      title: `Zone ${z.strength}`,
      data: [
        { time: first, value: z.priceLow },
        { time: last, value: z.priceLow },
      ],
    },
    {
      id: `zone-${z.id}-high`,
      color: '#c76fd180',
      data: [
        { time: first, value: z.priceHigh },
        { time: last, value: z.priceHigh },
      ],
    },
  ])

  return (
    <Card className="py-4">
      <CardContent className="flex flex-col gap-2 px-4">
        <CandleChart bars={chartBars} overlays={overlays} markers={markers} />
        <p className="text-xs text-muted-foreground">
          Daily candles{trades.length > 0 && ' · your trades marked'}
          {zones.length > 0 && ' · zones overlaid'}.
        </p>
      </CardContent>
    </Card>
  )
}

function PositionCard({ holding }: { holding: Holding }) {
  const { data: detail } = useGetHoldingQuery(holding.id)
  const lots = detail?.lots ?? []

  return (
    <Card className="py-4">
      <CardContent className="flex flex-col gap-3 px-4">
        <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">Lots</h2>
        {lots.length === 0 ? (
          <p className="text-sm text-muted-foreground">No lots recorded.</p>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Acquired</TableHead>
                  <TableHead className="text-right">Qty</TableHead>
                  <TableHead className="text-right">Cost</TableHead>
                  <TableHead>Source</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {lots.map((lot) => (
                  <TableRow key={lot.id}>
                    <TableCell className="tabular-nums">
                      {new Date(lot.acquiredAt).toISOString().slice(0, 10)}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">{lot.qty.toLocaleString()}</TableCell>
                    <TableCell className="text-right tabular-nums">
                      {formatMinor(lot.costMinor, lot.costCurrency)}
                    </TableCell>
                    <TableCell>
                      <Badge variant="outline">{lot.source ?? 'manual'}</Badge>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function ThesisCard({ holding }: { holding: Holding }) {
  const [updateThesis] = useUpdateThesisMutation()
  const [text, setText] = useState(holding.thesisNotesMd ?? '')
  const [savedAt, setSavedAt] = useState<Date | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const lastSaved = useRef(holding.thesisNotesMd ?? '')

  useEffect(() => () => {
    if (timer.current) clearTimeout(timer.current)
  }, [])

  const onChange = (value: string) => {
    setText(value)
    if (timer.current) clearTimeout(timer.current)
    timer.current = setTimeout(async () => {
      if (value === lastSaved.current) return
      try {
        await updateThesis({ id: holding.id, thesisNotesMd: value }).unwrap()
        lastSaved.current = value
        setSavedAt(new Date())
      } catch (err) {
        toast.error(getApiErrorMessage(err, 'Could not save thesis notes.'))
      }
    }, 1000)
  }

  return (
    <Card className="py-4">
      <CardContent className="flex h-full flex-col gap-3 px-4">
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
            Thesis
          </h2>
          {savedAt && (
            <span className="text-xs text-muted-foreground">
              saved {savedAt.toLocaleTimeString()}
            </span>
          )}
        </div>
        <Textarea
          value={text}
          onChange={(e) => onChange(e.target.value)}
          placeholder="Why do you hold this? Markdown supported. Autosaves as you type."
          className="min-h-40 flex-1 font-mono text-sm"
        />
      </CardContent>
    </Card>
  )
}

function DividendsCard({ holding }: { holding: Holding }) {
  const { data: dividends = [] } = useGetDividendsQuery({ holdingId: holding.id })
  const { data: summary } = useGetDividendSummaryQuery()
  const row = summary?.holdings.find((r) => r.holdingId === holding.id)

  return (
    <Card className="py-4">
      <CardContent className="flex flex-col gap-3 px-4">
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
            Dividends
          </h2>
          {row?.yieldOnCost != null && (
            <Badge variant="secondary">yield-on-cost {formatPct(row.yieldOnCost)}</Badge>
          )}
        </div>
        {dividends.length === 0 ? (
          <p className="text-sm text-muted-foreground">No dividends recorded for this holding.</p>
        ) : (
          <ul className="flex flex-col gap-1.5">
            {dividends.slice(0, 8).map((d) => (
              <li key={d.id} className="flex items-center justify-between text-sm">
                <span className="text-muted-foreground">
                  {d.payDate} {d.reinvested && <Badge variant="outline">DRIP</Badge>}
                </span>
                <span className="font-medium tabular-nums">{formatMinor(d.amountMinor, d.currency)}</span>
              </li>
            ))}
          </ul>
        )}
        {row && row.upcomingExDates.length > 0 && (
          <p className="text-xs text-muted-foreground">
            Upcoming ex-dates:{' '}
            {row.upcomingExDates.map((u) => new Date(u.at).toISOString().slice(0, 10)).join(', ')}
          </p>
        )}
      </CardContent>
    </Card>
  )
}

function NewsAndCatalystsCard({ instrumentId }: { instrumentId: string }) {
  const { data: news = [] } = useGetAssetNewsQuery(instrumentId)
  const { data: catalysts = [] } = useGetAssetCatalystsQuery(instrumentId)

  if (news.length === 0 && catalysts.length === 0) {
    return (
      <Card className="py-4">
        <CardContent className="px-4 text-sm text-muted-foreground">
          No news or catalysts for this instrument yet.
        </CardContent>
      </Card>
    )
  }

  return (
    <Card className="py-4">
      <CardContent className="flex flex-col gap-3 px-4">
        {catalysts.length > 0 && (
          <>
            <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
              Catalysts
            </h2>
            <ul className="flex flex-col gap-1.5">
              {catalysts.slice(0, 5).map((c) => (
                <li key={c.id} className="flex items-center justify-between text-sm">
                  <span>{c.title}</span>
                  <span
                    className={cn(
                      'text-xs tabular-nums',
                      c.severity === 'red' ? 'text-destructive' : 'text-muted-foreground',
                    )}
                  >
                    {new Date(c.at).toISOString().slice(0, 10)}
                  </span>
                </li>
              ))}
            </ul>
          </>
        )}
        {news.length > 0 && (
          <>
            <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
              News
            </h2>
            <ul className="flex flex-col gap-1.5">
              {news.slice(0, 6).map((n) => (
                <li key={n.id} className="text-sm">
                  <a
                    href={n.url}
                    target="_blank"
                    rel="noreferrer"
                    className="inline-flex items-center gap-1 hover:underline"
                  >
                    {n.title}
                    <ExternalLinkIcon className="size-3 text-muted-foreground" aria-hidden />
                  </a>
                  <span className="ml-2 text-xs text-muted-foreground">{n.source}</span>
                </li>
              ))}
            </ul>
          </>
        )}
      </CardContent>
    </Card>
  )
}
