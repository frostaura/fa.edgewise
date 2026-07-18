import { useState } from 'react'
import { Link } from 'react-router'
import {
  AlertTriangleIcon,
  CameraIcon,
  PieChartIcon,
  TrendingUpIcon,
} from 'lucide-react'
import { toast } from 'sonner'

import {
  useAcceptRatchetMutation,
  useGetAllocationQuery,
  useGetHoldingsQuery,
  useGetNetworthQuery,
  useGetPerformanceQuery,
  useGetPortfolioSummaryQuery,
  useGetRatchetSuggestionQuery,
  useRunSnapshotMutation,
  type BucketSummary,
  type Holding,
} from '@/api/portfolioApi'
import { getApiErrorMessage } from '@/api/types'
import { EquityCurveChart } from '@/components/charts/EquityCurveChart'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { StaleBadge } from '@/components/domain/StaleBadge'
import { StatCard } from '@/components/domain/StatCard'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { cn } from '@/lib/utils'
import { buildConicGradient, buildRingSegments } from './lib/allocation'
import { ladderBadge } from './lib/ladder'
import { formatMinor, formatMinorSigned, formatPct, formatPctSigned, signTone } from './lib/money'

export default function PortfolioPage() {
  const { data: summary, isLoading } = useGetPortfolioSummaryQuery()
  const { data: holdings } = useGetHoldingsQuery()
  const [runSnapshot, { isLoading: snapshotRunning }] = useRunSnapshotMutation()

  const currency = summary?.baseCurrency ?? 'ZAR'
  const ladder = ladderBadge(summary?.ladderState.state)

  const takeSnapshot = async () => {
    try {
      const result = await runSnapshot().unwrap()
      toast.success(`Snapshot written (${result.rowsWritten} rows).`)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Snapshot failed.'))
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Portfolio"
        description="Holdings, buckets and exposure across all your accounts."
        actions={
          <>
            <Button variant="outline" size="sm" onClick={takeSnapshot} disabled={snapshotRunning}>
              <CameraIcon className="size-4" aria-hidden />
              Snapshot
            </Button>
            <Button asChild size="sm">
              <Link to="/portfolio/forecast">
                <TrendingUpIcon className="size-4" aria-hidden />
                Forecast
              </Link>
            </Button>
          </>
        }
      />

      {/* Stat row */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {isLoading || !summary ? (
          Array.from({ length: 4 }).map((_, i) => <Skeleton key={i} className="h-24 rounded-xl" />)
        ) : (
          <>
            <StatCard label="Total value" value={formatMinor(summary.totalValueMinor, currency)} />
            <StatCard
              label="Unrealised P&L"
              value={
                <span className={signTone(summary.unrealisedPnlMinor)}>
                  {formatMinorSigned(summary.unrealisedPnlMinor, currency)}
                </span>
              }
              delta={
                summary.totalCostMinor > 0
                  ? {
                      value: summary.unrealisedPnlMinor,
                      label: formatPctSigned(summary.unrealisedPnlMinor / summary.totalCostMinor),
                    }
                  : undefined
              }
            />
            <StatCard
              label="Today"
              value={
                summary.todayChangeMinor != null ? (
                  <span className={signTone(summary.todayChangeMinor)}>
                    {formatMinorSigned(summary.todayChangeMinor, currency)}
                  </span>
                ) : (
                  '—'
                )
              }
            />
            <StatCard
              label="Trading ladder"
              value={
                <Badge variant={ladder.variant} title={ladder.description}>
                  {ladder.label}
                </Badge>
              }
              delta={
                summary.ladderState.drawdownPct > 0
                  ? { value: -1, label: `-${formatPct(summary.ladderState.drawdownPct)} DD` }
                  : undefined
              }
            />
          </>
        )}
      </div>

      <RatchetBanner currency={currency} />

      {/* Buckets strip */}
      {summary && summary.perBucket.length > 0 && (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
          {summary.perBucket.map((bucket) => (
            <BucketCard key={bucket.id} bucket={bucket} currency={currency} />
          ))}
        </div>
      )}

      <HoldingsSection holdings={holdings} currency={currency} />

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <AllocationSection currency={currency} />
        <PerformanceSection />
      </div>

      <NetworthSection currency={currency} />
    </div>
  )
}

function RatchetBanner({ currency }: { currency: string }) {
  const { data: suggestion } = useGetRatchetSuggestionQuery()
  const [accept, { isLoading }] = useAcceptRatchetMutation()
  const [confirmOpen, setConfirmOpen] = useState(false)

  if (!suggestion) return null

  const doAccept = async () => {
    try {
      await accept({ amountMinor: suggestion.suggestedAmountMinor }).unwrap()
      toast.success('Profit ratcheted into Long-term. High-water mark updated.')
      setConfirmOpen(false)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Ratchet failed.'))
    }
  }

  return (
    <Card className="border-success/40 bg-success/5 py-4">
      <CardContent className="flex flex-wrap items-center justify-between gap-3 px-4">
        <div className="flex flex-col gap-0.5">
          <span className="text-sm font-semibold">
            Quarterly ratchet: {formatMinor(suggestion.suggestedAmountMinor, currency)} of trading
            profit above the high-water mark
          </span>
          <span className="text-xs text-muted-foreground">
            Move it to Long-term to lock the gain (HWM {formatMinor(suggestion.hwmMinor, currency)} →
            current {formatMinor(suggestion.currentMinor, currency)}).
          </span>
        </div>
        <Button size="sm" onClick={() => setConfirmOpen(true)}>
          Ratchet profit
        </Button>
      </CardContent>

      <Dialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Ratchet trading profit?</DialogTitle>
            <DialogDescription>
              {formatMinor(suggestion.suggestedAmountMinor, currency)} moves from Trading to
              Long-term as paired cash flows, and the trading high-water mark resets to the
              remaining equity. This cannot be undone from the UI.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmOpen(false)}>
              Cancel
            </Button>
            <Button onClick={doAccept} disabled={isLoading}>
              Confirm ratchet
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}

function BucketCard({ bucket, currency }: { bucket: BucketSummary; currency: string }) {
  const actual = Math.min(1, bucket.actualAllocPct)
  const target = Math.min(1, bucket.targetAllocPct)
  return (
    <Card className="gap-0 py-3">
      <CardContent className="flex flex-col gap-2 px-4">
        <div className="flex items-center justify-between gap-2">
          <span className="text-sm font-medium">{bucket.name}</span>
          <div className="flex items-center gap-1.5">
            {bucket.stale && <StaleBadge updatedAt={new Date(Date.now() - 86_400_000)} />}
            {bucket.driftFlag && (
              <span title="Allocation has drifted more than 1.5x the band from target">
                <AlertTriangleIcon className="size-4 text-warning" aria-hidden />
              </span>
            )}
          </div>
        </div>
        <span className="text-lg font-semibold tabular-nums">
          {formatMinor(bucket.valueMinor, currency)}
        </span>
        <div className="flex flex-col gap-1">
          <div className="flex justify-between text-xs text-muted-foreground">
            <span>actual {formatPct(bucket.actualAllocPct, 0)}</span>
            <span>target {formatPct(bucket.targetAllocPct, 0)}</span>
          </div>
          <div className="relative h-1.5 w-full overflow-hidden rounded-full bg-muted">
            <div
              className={cn('h-full rounded-full', bucket.driftFlag ? 'bg-warning' : 'bg-primary')}
              style={{ width: `${actual * 100}%` }}
            />
            <div
              className="absolute top-0 h-full w-0.5 bg-foreground/60"
              style={{ left: `${target * 100}%` }}
              title={`Target ${formatPct(bucket.targetAllocPct, 0)}`}
            />
          </div>
        </div>
      </CardContent>
    </Card>
  )
}

function HoldingsSection({ holdings, currency }: { holdings?: Holding[]; currency: string }) {
  if (!holdings || holdings.length === 0) {
    return (
      <EmptyState
        icon={PieChartIcon}
        title="No holdings yet"
        hint="Log positions (holdings + lots) to see live valuations, allocation and performance here."
      />
    )
  }

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
        Holdings
      </h2>

      {/* Desktop table */}
      <div className="hidden overflow-x-auto rounded-xl border md:block">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Instrument</TableHead>
              <TableHead className="text-right">Qty</TableHead>
              <TableHead className="text-right">Avg cost</TableHead>
              <TableHead className="text-right">Value</TableHead>
              <TableHead className="text-right">uP&L</TableHead>
              <TableHead className="text-right">Weight</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {holdings.map((h) => (
              <TableRow key={h.id}>
                <TableCell>
                  <Link
                    to={`/portfolio/assets/${h.instrument.id}`}
                    className="flex flex-col hover:underline"
                  >
                    <span className="flex items-center gap-2 font-medium">
                      {h.instrument.symbol}
                      {h.stale && <StaleBadge updatedAt={h.priceAsOf ?? new Date(Date.now() - 86_400_000)} />}
                    </span>
                    <span className="text-xs text-muted-foreground">{h.instrument.name}</span>
                  </Link>
                </TableCell>
                <TableCell className="text-right tabular-nums">{h.qty.toLocaleString()}</TableCell>
                <TableCell className="text-right tabular-nums">
                  {h.avgCostMinor != null ? formatMinor(h.avgCostMinor, currency) : '—'}
                </TableCell>
                <TableCell className="text-right font-medium tabular-nums">
                  {formatMinor(h.valueMinor, currency)}
                </TableCell>
                <TableCell className={cn('text-right tabular-nums', signTone(h.unrealisedPnlMinor))}>
                  {formatMinorSigned(h.unrealisedPnlMinor, currency)}
                  <span className="ml-1 text-xs">({formatPctSigned(h.unrealisedPnlPct)})</span>
                </TableCell>
                <TableCell className="text-right tabular-nums">
                  {formatPct(h.totalWeightPct)}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>

      {/* Mobile cards */}
      <div className="flex flex-col gap-2 md:hidden">
        {holdings.map((h) => (
          <Link key={h.id} to={`/portfolio/assets/${h.instrument.id}`}>
            <Card className="gap-0 py-3">
              <CardContent className="flex flex-col gap-1 px-4">
                <div className="flex items-center justify-between">
                  <span className="flex items-center gap-2 font-medium">
                    {h.instrument.symbol}
                    {h.stale && (
                      <StaleBadge updatedAt={h.priceAsOf ?? new Date(Date.now() - 86_400_000)} />
                    )}
                  </span>
                  <span className="font-medium tabular-nums">{formatMinor(h.valueMinor, currency)}</span>
                </div>
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">
                    {h.qty.toLocaleString()} · {formatPct(h.totalWeightPct)} weight
                  </span>
                  <span className={cn('tabular-nums', signTone(h.unrealisedPnlMinor))}>
                    {formatMinorSigned(h.unrealisedPnlMinor, currency)} (
                    {formatPctSigned(h.unrealisedPnlPct)})
                  </span>
                </div>
              </CardContent>
            </Card>
          </Link>
        ))}
      </div>
    </section>
  )
}

function AllocationSection({ currency }: { currency: string }) {
  const { data: allocation } = useGetAllocationQuery()
  const [mode, setMode] = useState<'bucket' | 'assetClass'>('bucket')

  const slices = mode === 'bucket' ? allocation?.perBucket : allocation?.perAssetClass
  const segments = buildRingSegments(slices ?? [])
  const gradient = buildConicGradient(segments)

  return (
    <Card className="py-4">
      <CardContent className="flex flex-col gap-4 px-4">
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
            Allocation
          </h2>
          <Tabs value={mode} onValueChange={(v) => setMode(v as 'bucket' | 'assetClass')}>
            <TabsList>
              <TabsTrigger value="bucket">Buckets</TabsTrigger>
              <TabsTrigger value="assetClass">Asset class</TabsTrigger>
            </TabsList>
          </Tabs>
        </div>

        {segments.length === 0 ? (
          <p className="text-sm text-muted-foreground">Nothing to allocate yet.</p>
        ) : (
          <div className="flex flex-wrap items-center gap-6">
            <div
              role="img"
              aria-label={`Allocation by ${mode === 'bucket' ? 'bucket' : 'asset class'}`}
              className="relative size-40 shrink-0 rounded-full"
              style={{ background: gradient }}
            >
              <div className="absolute inset-4 flex items-center justify-center rounded-full bg-card text-center">
                <span className="text-xs text-muted-foreground">
                  {formatMinor(allocation?.totalValueMinor ?? 0, currency)}
                </span>
              </div>
            </div>
            <ul className="flex min-w-40 flex-1 flex-col gap-1.5">
              {segments.map((s) => (
                <li key={s.key} className="flex items-center justify-between gap-2 text-sm">
                  <span className="flex items-center gap-2">
                    <span
                      className="size-2.5 rounded-full"
                      style={{ backgroundColor: s.color }}
                      aria-hidden
                    />
                    {s.label}
                  </span>
                  <span className="tabular-nums text-muted-foreground">{formatPct(s.share)}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function PerformanceSection() {
  const [basis, setBasis] = useState<'twr' | 'mwr'>('twr')
  const { data: performance } = useGetPerformanceQuery({ basis })

  const points = (performance?.points ?? []).map((p) => ({
    time: p.date,
    value: p.cumulativeReturn * 100,
  }))

  return (
    <Card className="py-4">
      <CardContent className="flex flex-col gap-4 px-4">
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
            Performance
          </h2>
          <Tabs value={basis} onValueChange={(v) => setBasis(v as 'twr' | 'mwr')}>
            <TabsList>
              <TabsTrigger value="twr">TWR</TabsTrigger>
              <TabsTrigger value="mwr">MWR</TabsTrigger>
            </TabsList>
          </Tabs>
        </div>

        {basis === 'twr' ? (
          points.length >= 1 ? (
            <>
              <div className="flex flex-wrap gap-4 text-sm">
                <span>
                  Total{' '}
                  <strong className={signTone(performance?.totalReturn ?? 0)}>
                    {formatPctSigned(performance?.totalReturn)}
                  </strong>
                </span>
                <span>
                  Annualised{' '}
                  <strong className={signTone(performance?.annualizedReturn ?? 0)}>
                    {formatPctSigned(performance?.annualizedReturn)}
                  </strong>
                </span>
                <span>
                  Max drawdown <strong>{formatPct(performance?.maxDrawdown)}</strong>
                </span>
              </div>
              <EquityCurveChart points={points} className="h-48" />
              <p className="text-xs text-muted-foreground">
                Cumulative time-weighted return (%), flows stripped out. Run snapshots daily to
                extend the series.
              </p>
            </>
          ) : (
            <p className="text-sm text-muted-foreground">
              Not enough snapshots yet — the daily job (or the Snapshot button) builds this series.
            </p>
          )
        ) : (
          <div className="flex flex-col gap-1 py-4">
            <span className="text-3xl font-semibold tabular-nums">
              <span className={signTone(performance?.xirr ?? 0)}>
                {formatPctSigned(performance?.xirr, 2)}
              </span>
            </span>
            <span className="text-xs text-muted-foreground">
              Money-weighted return (XIRR) of your deposits and withdrawals against today's value.
            </span>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function NetworthSection({ currency }: { currency: string }) {
  const { data: series } = useGetNetworthQuery()

  if (!series || series.length < 2) return null

  const points = series.map((p) => ({ time: p.date, value: p.equityMinor / 100 }))
  const flows = series.filter((p) => p.netFlowMinor !== 0)

  return (
    <Card className="py-4">
      <CardContent className="flex flex-col gap-3 px-4">
        <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
          Net worth
        </h2>
        <EquityCurveChart points={points} className="h-56" />
        {flows.length > 0 && (
          <p className="text-xs text-muted-foreground">
            Net flows:{' '}
            {flows
              .slice(-4)
              .map((f) => `${f.date} ${formatMinorSigned(f.netFlowMinor, currency)}`)
              .join(' · ')}
          </p>
        )}
      </CardContent>
    </Card>
  )
}
