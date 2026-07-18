import { GaugeIcon } from 'lucide-react'

import { useGetSentimentQuery, type SentimentReading } from '@/api/radarApi'
import { EmptyState } from '@/components/domain/EmptyState'
import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'

/**
 * Sentiment tiles: Crypto Fear & Greed headline value + 30-day sparkline.
 * Defensive: GET /api/market/sentiment belongs to another vertical — degrades
 * to an empty state on error.
 */
export function SentimentSection() {
  const { data, isLoading, isError } = useGetSentimentQuery()

  if (isLoading) {
    return <Skeleton className="h-40 rounded-xl" />
  }

  const readings = (data ?? [])
    .filter((r) => (r.kind ?? '').toLowerCase().includes('crypto'))
    .sort((a, b) => a.date.localeCompare(b.date))

  if (isError || readings.length === 0) {
    return (
      <EmptyState
        icon={GaugeIcon}
        title={isError ? 'Sentiment feed not available yet' : 'No sentiment readings'}
        hint="The Crypto Fear & Greed gauge appears here once the market data feed is online."
      />
    )
  }

  const latest = readings[readings.length - 1]
  const last30 = readings.slice(-30)

  return (
    <div className="grid gap-4 sm:grid-cols-2">
      <Card className="py-4">
        <CardContent className="flex flex-col gap-1 px-4">
          <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
            Crypto Fear &amp; Greed
          </span>
          <div className="flex items-baseline gap-3">
            <span className="text-4xl font-semibold tabular-nums">{latest.value}</span>
            <span className="text-sm text-muted-foreground">{latest.label}</span>
          </div>
          <FgMeter value={latest.value} />
        </CardContent>
      </Card>
      <Card className="py-4">
        <CardContent className="flex flex-col gap-1 px-4">
          <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
            Last 30 days
          </span>
          <Sparkline readings={last30} />
        </CardContent>
      </Card>
    </div>
  )
}

/** 0–100 meter with the extreme bands marked; needle at the current value. */
function FgMeter({ value }: { value: number }) {
  const clamped = Math.max(0, Math.min(100, value))
  return (
    <div
      className="relative mt-2 h-2 rounded-full bg-muted"
      role="meter"
      aria-label="Fear and greed index"
      aria-valuenow={clamped}
      aria-valuemin={0}
      aria-valuemax={100}
    >
      {/* extreme-fear and extreme-greed zones */}
      <div className="absolute inset-y-0 left-0 w-1/5 rounded-l-full bg-destructive/25" />
      <div className="absolute inset-y-0 right-0 w-1/5 rounded-r-full bg-warning/40" />
      <div
        className="absolute top-1/2 size-3 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-background bg-foreground"
        style={{ left: `${clamped}%` }}
      />
      <div className="absolute -bottom-4 left-0 text-xs text-muted-foreground">Fear</div>
      <div className="absolute right-0 -bottom-4 text-xs text-muted-foreground">Greed</div>
    </div>
  )
}

/** Single-series 30-day sparkline (theme primary stroke, recessive midline). */
function Sparkline({ readings }: { readings: SentimentReading[] }) {
  const width = 240
  const height = 56
  const pad = 4
  if (readings.length < 2) {
    return <p className="text-sm text-muted-foreground">Not enough history yet.</p>
  }

  const points = readings
    .map((r, i) => {
      const x = pad + (i / (readings.length - 1)) * (width - pad * 2)
      const y = pad + ((100 - r.value) / 100) * (height - pad * 2)
      return `${x.toFixed(1)},${y.toFixed(1)}`
    })
    .join(' ')
  const first = readings[0]
  const last = readings[readings.length - 1]

  return (
    <svg
      viewBox={`0 0 ${width} ${height}`}
      className="h-14 w-full"
      role="img"
      aria-label={`Fear and greed, ${first.value} on ${first.date} to ${last.value} on ${last.date}`}
    >
      {/* recessive 50-line */}
      <line
        x1={pad}
        x2={width - pad}
        y1={height / 2}
        y2={height / 2}
        stroke="currentColor"
        className="text-border"
        strokeDasharray="2 3"
        strokeWidth={1}
      />
      <polyline
        points={points}
        fill="none"
        stroke="var(--primary)"
        strokeWidth={2}
        strokeLinejoin="round"
        strokeLinecap="round"
      />
    </svg>
  )
}
