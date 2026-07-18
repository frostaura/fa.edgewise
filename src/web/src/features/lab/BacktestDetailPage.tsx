import { useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import { HistoryIcon, Loader2Icon } from 'lucide-react'
import { toast } from 'sonner'

import type { LabTimeframe } from '@/api/labApi'
import {
  useGetBacktestQuery,
  useGetPipelineQuery,
  useTransitionStrategyMutation,
} from '@/api/labApi'
import { getApiErrorMessage } from '@/api/types'
import { EquityCurveChart } from '@/components/charts/EquityCurveChart'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { StatCard } from '@/components/domain/StatCard'
import { RValue } from '@/components/domain/RValue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { HonestyPanel } from '@/features/lab/components/HonestyPanel'

const TF_SECONDS: Record<LabTimeframe, number> = {
  h1: 3_600,
  h4: 14_400,
  d1: 86_400,
  w1: 604_800,
}

export default function BacktestDetailPage() {
  const { id = '' } = useParams<{ id: string }>()
  // Poll only while the run is still in flight.
  const [pollingInterval, setPollingInterval] = useState(2000)
  const { data: report, isLoading, error } = useGetBacktestQuery(id, {
    skip: !id,
    pollingInterval,
    skipPollingIfUnfocused: true,
  })
  const running = report?.status === 'pending' || report?.status === 'running'
  useEffect(() => {
    setPollingInterval(report && !running ? 0 : 2000)
  }, [report, running])

  const { data: pipeline } = useGetPipelineQuery(report?.strategyId ?? '', {
    skip: !report?.strategyId,
  })
  const [transition, { isLoading: promoting }] = useTransitionStrategyMutation()

  const equityPoints = useMemo(() => {
    if (!report?.result) return []
    const start = Math.floor(new Date(report.rangeStart).getTime() / 1000)
    const step = TF_SECONDS[report.timeframe] ?? 86_400
    return report.result.equityCurveMinor.map((value, i) => ({
      time: start + i * step,
      value,
    }))
  }, [report])

  if (isLoading || (!report && !error)) return <Skeleton className="h-64 w-full" />

  if (error || !report) {
    return (
      <EmptyState
        icon={HistoryIcon}
        title="Backtest not found"
        action={
          <Button asChild size="sm" variant="outline">
            <Link to="/lab/strategies">Back to strategies</Link>
          </Button>
        }
      />
    )
  }

  const promote = async () => {
    try {
      await transition({ id: report.strategyId, toState: 'backtested' }).unwrap()
      toast.success('Strategy promoted to Backtested')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Promotion failed'))
    }
  }

  const result = report.result
  const canPromote =
    report.status === 'done' &&
    report.honesty?.isExploratory === false &&
    pipeline?.state === 'draft'

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Backtest report"
        description={`${report.timeframe.toUpperCase()} · ${new Date(report.rangeStart).toLocaleDateString()} → ${new Date(report.rangeEnd).toLocaleDateString()} · strategy v${report.strategyVersion}`}
        actions={
          <div className="flex items-center gap-2">
            <Badge
              variant={
                report.status === 'done'
                  ? 'success'
                  : report.status === 'failed'
                    ? 'destructive'
                    : 'outline'
              }
            >
              {report.status}
            </Badge>
            <Button asChild size="sm" variant="outline">
              <Link to={`/lab/strategies/${report.strategyId}`}>Strategy</Link>
            </Button>
          </div>
        }
      />

      {running && (
        <EmptyState
          icon={Loader2Icon}
          title={`Backtest ${report.status}…`}
          hint="The run is executing in the background. This page refreshes automatically."
        />
      )}

      {report.status === 'failed' && (
        <div
          role="alert"
          className="rounded-lg border border-destructive bg-destructive/10 p-4 text-sm text-destructive"
        >
          <p className="font-semibold">Backtest failed</p>
          <p>{report.error ?? 'Unknown error.'}</p>
        </div>
      )}

      {report.status === 'done' && result && (
        <>
          {/* Stat row */}
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
            <StatCard label="Trades" value={result.n} />
            <StatCard label="Win rate" value={`${(result.winRate * 100).toFixed(1)}%`} />
            <StatCard label="Expectancy" value={<RValue value={result.expectancyR} />} />
            <StatCard label="Max drawdown" value={`${result.maxDrawdownPct.toFixed(1)}%`} />
            <StatCard label="Exposure" value={`${result.exposurePct.toFixed(0)}%`} />
          </div>

          {/* Equity curve */}
          <Card>
            <CardHeader>
              <CardTitle>Equity curve</CardTitle>
            </CardHeader>
            <CardContent>
              <EquityCurveChart points={equityPoints} />
            </CardContent>
          </Card>

          {/* Honesty panel */}
          {report.honesty && <HonestyPanel honesty={report.honesty} />}

          {/* Promotion hint */}
          {canPromote && (
            <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-success bg-success/10 p-4 text-sm">
              <p>
                This run has out-of-sample evidence — the strategy is eligible for promotion to{' '}
                <span className="font-semibold">Backtested</span>.
              </p>
              <Button size="sm" onClick={promote} disabled={promoting}>
                Promote to Backtested
              </Button>
            </div>
          )}

          {/* Trade list */}
          <Card>
            <CardHeader>
              <CardTitle>Trades ({result.trades.length})</CardTitle>
            </CardHeader>
            <CardContent className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Entry</TableHead>
                    <TableHead>Exit</TableHead>
                    <TableHead>Dir</TableHead>
                    <TableHead className="text-right">Entry px</TableHead>
                    <TableHead className="text-right">Exit px</TableHead>
                    <TableHead className="text-right">R</TableHead>
                    <TableHead className="text-right">PnL (minor)</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {result.trades.map((trade, i) => (
                    <TableRow key={i}>
                      <TableCell>{new Date(trade.entryTs).toLocaleDateString()}</TableCell>
                      <TableCell>{new Date(trade.exitTs).toLocaleDateString()}</TableCell>
                      <TableCell>{trade.direction}</TableCell>
                      <TableCell className="text-right tabular-nums">
                        {trade.entryPx.toFixed(2)}
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {trade.exitPx.toFixed(2)}
                      </TableCell>
                      <TableCell className="text-right">
                        <RValue value={trade.rMultiple} />
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {Math.round(trade.pnlMinor).toLocaleString()}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>

          {/* Cost model used (always visible, never hidden) */}
          {report.config && (
            <p className="text-xs text-muted-foreground">
              Costs ({report.config.venue}): commission {report.config.commissionPctPerSide}%/side ·
              spread {report.config.spreadPct}% · slippage {report.config.slippagePct}% · funding{' '}
              {report.config.fundingPctPer8h}%/8h · equity start{' '}
              {report.config.equityStartMinor.toLocaleString()} minor
            </p>
          )}
        </>
      )}
    </div>
  )
}
