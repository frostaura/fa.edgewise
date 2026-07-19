import { AlertTriangleIcon, ShieldCheckIcon } from 'lucide-react'

import type { BacktestResult, HonestyReport } from '@/api/labApi'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'

const fmtR = (value: number | undefined | null) =>
  value == null ? '—' : `${value >= 0 ? '+' : ''}${value.toFixed(2)}R`

function SegmentStat({ label, result }: { label: string; result?: BacktestResult | null }) {
  return (
    <div className="flex flex-col gap-1 rounded-lg border bg-surface/50 p-3">
      <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
        {label}
      </span>
      <span className="text-lg font-semibold tabular-nums" data-testid={`expectancy-${label}`}>
        {fmtR(result?.expectancyR)}
      </span>
      <span className="text-xs text-muted-foreground">
        {result ? `${result.n} trades · ${(result.winRate * 100).toFixed(0)}% win` : 'not available'}
      </span>
    </div>
  )
}

const VERDICT_VARIANT = {
  adequate: 'success',
  thin: 'warning',
  exploratory: 'destructive',
} as const

/**
 * The honesty panel: in-sample vs out-of-sample expectancy, doubled-cost
 * stress, parameter wiggle sensitivity and the sample-size verdict. Shows the
 * PRD-mandated red banner whenever the run is exploratory.
 */
export function HonestyPanel({ honesty }: { honesty: HonestyReport }) {
  const verdict = honesty.sampleVerdict
  return (
    <Card data-testid="honesty-panel">
      <CardHeader>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <CardTitle className="flex items-center gap-2">
            <ShieldCheckIcon className="size-4 text-muted-foreground" aria-hidden />
            Honesty report
          </CardTitle>
          <Badge variant={VERDICT_VARIANT[verdict] ?? 'outline'} data-testid="verdict-badge">
            sample: {verdict}
          </Badge>
        </div>
        <CardDescription>
          Out-of-sample split, doubled costs and parameter wiggle — the checks that keep a backtest
          honest.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {honesty.isExploratory && (
          <div
            role="alert"
            data-testid="exploratory-banner"
            className="flex items-start gap-2 rounded-lg border border-destructive bg-destructive/10 p-3 text-sm text-destructive"
          >
            <AlertTriangleIcon className="mt-0.5 size-4 shrink-0" aria-hidden />
            <div>
              <p className="font-semibold">EXPLORATORY — no out-of-sample evidence</p>
              <p>
                This result cannot support promotion. Extend the data range or reduce trade
                filtering until an out-of-sample segment with enough trades exists.
              </p>
            </div>
          </div>
        )}

        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <SegmentStat label="in-sample" result={honesty.inSample} />
          <SegmentStat label="out-of-sample" result={honesty.outOfSample} />
          <SegmentStat label="2x costs" result={honesty.doubledCosts} />
          <div className="flex flex-col gap-1 rounded-lg border bg-surface/50 p-3">
            <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
              Wiggle max
            </span>
            <span className="text-lg font-semibold tabular-nums">
              ±{honesty.maxWiggleSensitivityR.toFixed(2)}R
            </span>
            <span className="text-xs text-muted-foreground">
              largest expectancy shift when any operand moves ±20%
            </span>
          </div>
        </div>

        {honesty.wiggles.length > 0 && (
          <p className="text-xs text-muted-foreground">
            {honesty.wiggles.length} wiggle rerun{honesty.wiggles.length === 1 ? '' : 's'} across{' '}
            enabled conditions. A strategy whose edge disappears under small parameter changes is
            curve-fit, not robust.
          </p>
        )}
      </CardContent>
    </Card>
  )
}
