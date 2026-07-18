import { BarChart3Icon } from 'lucide-react'

import {
  useGetAdherenceTrendQuery,
  useGetBiasCardsQuery,
  useGetCalendarHeatmapQuery,
  useGetExpectancyQuery,
  useGetPtrQuery,
  useGetRDistributionQuery,
  useGetTiltQuery,
  type BiasCard,
  type ExpectancyGroupBy,
} from '@/api/analyticsApi'
import { EmptyState } from '@/components/domain/EmptyState'
import { RValue } from '@/components/domain/RValue'
import { StatCard } from '@/components/domain/StatCard'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { cn } from '@/lib/utils'
import * as React from 'react'

const BIAS_LABELS: Record<string, { title: string; hint: string }> = {
  DISPOSITION: {
    title: 'Disposition effect',
    hint: 'Holding winners vs losers (ratio of average hold times).',
  },
  REVENGE_ENTRIES: {
    title: 'Revenge entries',
    hint: 'Entries within 30 minutes of a loss, last 30 days.',
  },
  SIZE_CREEP: {
    title: 'Size creep',
    hint: 'Risk taken after a 3-win streak vs baseline.',
  },
  FOMO_CHASE: {
    title: 'FOMO chase',
    hint: 'Entries filled well past the trigger price, last 30 days.',
  },
}

/** Simple CSS bar sparkline over a series of 0..1 values. */
function Sparkline({ values, tone = 'bg-primary' }: { values: number[]; tone?: string }) {
  if (values.length === 0) {
    return <span className="text-xs text-muted-foreground">no data</span>
  }
  return (
    <div className="flex h-10 items-end gap-px" role="img" aria-label="trend sparkline">
      {values.map((v, i) => (
        <div
          key={i}
          className={cn('w-1.5 flex-1 rounded-sm', tone)}
          style={{ height: `${Math.max(8, Math.round(v * 100))}%`, opacity: 0.4 + 0.6 * v }}
        />
      ))}
    </div>
  )
}

function BiasCardView({ card }: { card: BiasCard }) {
  const meta = BIAS_LABELS[card.code] ?? { title: card.code, hint: '' }
  return (
    <Card className={cn('gap-2 py-4', card.breached && 'border-warning')}>
      <CardHeader className="px-4">
        <CardTitle className="flex items-center justify-between text-sm">
          {meta.title}
          <Badge variant={card.breached ? 'warning' : 'secondary'}>
            {card.breached ? 'flagged' : 'ok'}
          </Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-1 px-4">
        <span className="text-xl font-semibold tabular-nums">
          {card.metric.toFixed(2)}
          <span className="ml-1 text-xs font-normal text-muted-foreground">
            / threshold {card.threshold.toFixed(2)}
          </span>
        </span>
        <p className="text-xs text-muted-foreground">{meta.hint}</p>
        {card.evidenceKeys.length > 0 && (
          <p className="text-xs text-muted-foreground">{card.evidenceKeys.length} trade(s) as evidence</p>
        )}
      </CardContent>
    </Card>
  )
}

/** The Journal analytics tab: expectancy, R-distribution, trends, heatmap, tilt, biases. */
export function AnalyticsSection({ isPaper }: { isPaper?: boolean }) {
  const [groupBy, setGroupBy] = React.useState<ExpectancyGroupBy>('setup')
  const filters = isPaper === undefined ? {} : { isPaper }

  const { data: expectancy } = useGetExpectancyQuery({ ...filters, groupBy })
  const { data: rDist } = useGetRDistributionQuery(filters)
  const { data: ptr } = useGetPtrQuery(filters)
  const { data: adherenceTrend } = useGetAdherenceTrendQuery(filters)
  const { data: heatmap } = useGetCalendarHeatmapQuery(filters)
  const { data: tilt } = useGetTiltQuery(filters)
  const { data: biases } = useGetBiasCardsQuery(filters)

  const hasData = (expectancy?.groups.length ?? 0) > 0 || (rDist?.n ?? 0) > 0

  if (!hasData) {
    return (
      <EmptyState
        icon={BarChart3Icon}
        title="No closed trades to analyse yet"
        hint="Close a few trades and expectancy by setup, your R distribution and behavioural bias cards will appear here."
      />
    )
  }

  const maxBin = Math.max(1, ...(rDist?.bins.map((b) => b.count) ?? [1]))
  const latestPtr = ptr && ptr.length > 0 ? ptr[ptr.length - 1] : undefined
  const latestAdh =
    adherenceTrend && adherenceTrend.length > 0
      ? adherenceTrend[adherenceTrend.length - 1]
      : undefined
  const recentHeatmap = (heatmap ?? []).slice(-84) // last ~12 weeks of active days

  return (
    <div className="flex flex-col gap-6">
      {/* Trend stat cards */}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        <StatCard
          label="Plan-then-trade rate"
          value={latestPtr ? `${Math.round(latestPtr.plannedRate * 100)}%` : '—'}
          sparkline={<Sparkline values={(ptr ?? []).map((w) => w.plannedRate)} />}
        />
        <StatCard
          label="Adherence trend"
          value={latestAdh ? Math.round(latestAdh.avgAdherence) : '—'}
          sparkline={
            <Sparkline
              values={(adherenceTrend ?? []).map((w) => w.avgAdherence / 100)}
              tone="bg-success"
            />
          }
        />
        <StatCard
          label="Tilt after losses"
          value={
            tilt && tilt.nAfterLoss > 0 ? (
              <RValue value={tilt.deltaR} className="text-2xl" />
            ) : (
              '—'
            )
          }
          delta={
            tilt && tilt.isSignificant ? { value: -1, label: 'significant' } : undefined
          }
        />
      </div>

      {/* Expectancy table */}
      <Card className="gap-3 py-4">
        <CardHeader className="flex-row items-center justify-between px-4">
          <CardTitle className="text-sm">Expectancy</CardTitle>
          <Select value={groupBy} onValueChange={(v) => setGroupBy(v as ExpectancyGroupBy)}>
            <SelectTrigger className="h-8 text-xs">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="setup">By setup</SelectItem>
              <SelectItem value="instrument">By instrument</SelectItem>
              <SelectItem value="emotion">By emotion</SelectItem>
              <SelectItem value="dayOfWeek">By weekday</SelectItem>
              <SelectItem value="hourOfDay">By hour</SelectItem>
            </SelectContent>
          </Select>
        </CardHeader>
        <CardContent className="overflow-x-auto px-4">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Group</TableHead>
                <TableHead className="text-right">n</TableHead>
                <TableHead className="text-right">Win rate</TableHead>
                <TableHead className="text-right">Expectancy</TableHead>
                <TableHead className="text-right">95% CI</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(expectancy?.groups ?? []).map((g) => (
                <TableRow key={g.key}>
                  <TableCell className="font-medium">{g.key}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    {g.n}
                    {g.lowSample && (
                      <Badge variant="outline" className="ml-1.5">
                        low n
                      </Badge>
                    )}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {Math.round(g.winRate * 100)}%
                  </TableCell>
                  <TableCell className="text-right">
                    <RValue value={g.expectancyR} precision={2} />
                  </TableCell>
                  <TableCell className="text-right text-xs text-muted-foreground tabular-nums">
                    {Math.round(g.wilson95Lo * 100)}–{Math.round(g.wilson95Hi * 100)}%
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        {/* R distribution */}
        <Card className="gap-3 py-4">
          <CardHeader className="px-4">
            <CardTitle className="text-sm">R distribution ({rDist?.n ?? 0} trades)</CardTitle>
          </CardHeader>
          <CardContent className="px-4">
            {rDist && rDist.bins.length > 0 ? (
              <div className="flex h-32 items-end gap-1">
                {rDist.bins.map((bin) => (
                  <div key={bin.from} className="flex flex-1 flex-col items-center gap-1">
                    <div
                      className={cn(
                        'w-full rounded-t-sm',
                        bin.from < 0 ? 'bg-destructive/70' : 'bg-success/70',
                      )}
                      style={{ height: `${Math.max(4, (bin.count / maxBin) * 100)}%` }}
                      title={`${bin.from}R to ${bin.to}R: ${bin.count}`}
                    />
                    <span className="text-[10px] text-muted-foreground tabular-nums">
                      {bin.from}
                    </span>
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-sm text-muted-foreground">No realised R values yet.</p>
            )}
          </CardContent>
        </Card>

        {/* Calendar heatmap */}
        <Card className="gap-3 py-4">
          <CardHeader className="px-4">
            <CardTitle className="text-sm">Daily R heatmap</CardTitle>
          </CardHeader>
          <CardContent className="px-4">
            {recentHeatmap.length > 0 ? (
              <div className="grid grid-cols-14 gap-1 sm:grid-cols-14">
                {recentHeatmap.map((cell) => (
                  <div
                    key={cell.date}
                    className={cn(
                      'aspect-square rounded-sm',
                      cell.sumR > 0
                        ? 'bg-success'
                        : cell.sumR < 0
                          ? 'bg-destructive'
                          : 'bg-muted',
                    )}
                    style={{ opacity: Math.min(1, 0.25 + Math.abs(cell.sumR) / 3) }}
                    title={`${cell.date}: ${cell.sumR.toFixed(1)}R over ${cell.n} trade(s)`}
                  />
                ))}
              </div>
            ) : (
              <p className="text-sm text-muted-foreground">No trading days recorded yet.</p>
            )}
          </CardContent>
        </Card>
      </div>

      {/* Bias cards */}
      {biases && (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
          {biases.cards.map((card) => (
            <BiasCardView key={card.code} card={card} />
          ))}
        </div>
      )}
    </div>
  )
}
