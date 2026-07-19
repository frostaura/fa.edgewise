import { useMemo } from 'react'
import type { SeriesMarker, Time } from 'lightweight-charts'

import { CandleChart, type CandleBar } from '@/components/charts/CandleChart'
import { EquityCurveChart, type EquityPoint } from '@/components/charts/EquityCurveChart'
import { AdherenceChip } from '@/components/domain/AdherenceChip'
import { GradeRing } from '@/components/domain/GradeRing'
import { PageHeader } from '@/components/domain/PageHeader'
import { ProvenanceChip } from '@/components/domain/ProvenanceChip'
import { RValue } from '@/components/domain/RValue'
import { StaleBadge } from '@/components/domain/StaleBadge'
import { StatCard } from '@/components/domain/StatCard'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'

/** Deterministic PRNG so the demo data is stable across reloads. */
function mulberry32(seed: number) {
  return () => {
    seed |= 0
    seed = (seed + 0x6d2b79f5) | 0
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

function makeDemoData() {
  const rand = mulberry32(42)
  const bars: CandleBar[] = []
  const equity: EquityPoint[] = []
  const daySeconds = 86_400
  const start = Math.floor(Date.UTC(2026, 0, 5) / 1000)

  let close = 184
  let account = 10_000
  for (let i = 0; i < 120; i++) {
    const time = start + i * daySeconds
    const open = close
    const drift = (rand() - 0.48) * 4
    close = Math.max(20, open + drift)
    const high = Math.max(open, close) + rand() * 2
    const low = Math.min(open, close) - rand() * 2
    const volume = Math.round(1_000_000 + rand() * 4_000_000)
    bars.push({ time, open, high, low, close, volume })

    account += (rand() - 0.44) * 120
    equity.push({ time, value: Math.round(account * 100) / 100 })
  }

  const sma: { time: number; value: number }[] = []
  for (let i = 19; i < bars.length; i++) {
    const window = bars.slice(i - 19, i + 1)
    sma.push({
      time: bars[i].time as number,
      value: window.reduce((sum, b) => sum + b.close, 0) / window.length,
    })
  }

  const markers: SeriesMarker<Time>[] = [
    {
      time: bars[35].time as Time,
      position: 'belowBar',
      shape: 'arrowUp',
      color: '#2fd9a2',
      text: 'Entry',
    },
    {
      time: bars[52].time as Time,
      position: 'aboveBar',
      shape: 'arrowDown',
      color: '#f47069',
      text: 'Exit +1.8R',
    },
  ]

  return { bars, equity, sma, markers }
}

/** Fixed "last refreshed" moment for the StaleBadge demo (kept out of render). */
const DEMO_STALE_AT = new Date(Date.now() - 12 * 60_000)

/** Hidden dev route (/dev/charts) proving the chart wrappers work. */
export default function ChartsDevPage() {
  const { bars, equity, sma, markers } = useMemo(() => makeDemoData(), [])

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Charts dev"
        description="Internal playground for CandleChart, EquityCurveChart and the domain components."
      />

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <StatCard
          label="Equity"
          value="$11,482"
          delta={{ value: 3.2, label: '+3.2%' }}
          sparkline={<EquityCurveChart points={equity.slice(-30)} sparkline />}
        />
        <StatCard label="Win rate" value="54%" delta={{ value: -2, label: '-2pp' }} />
        <StatCard label="Best trade" value={<RValue value={1.8} />} />
        <StatCard label="Worst trade" value={<RValue value={-0.7} />} />
      </div>

      <Card>
        <CardHeader>
          <CardTitle>DEMO · Daily candles</CardTitle>
          <CardDescription className="flex flex-wrap items-center gap-2">
            Candlestick + volume with a 20-bar SMA overlay and trade markers.
            <StaleBadge updatedAt={DEMO_STALE_AT} />
            <ProvenanceChip basedOn="120 demo bars · seeded RNG" />
          </CardDescription>
        </CardHeader>
        <CardContent>
          <CandleChart
            bars={bars}
            overlays={[{ id: 'sma20', data: sma, title: 'SMA 20' }]}
            markers={markers}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>DEMO · Equity curve</CardTitle>
          <CardDescription>Area chart, theme-reactive like the candles.</CardDescription>
        </CardHeader>
        <CardContent>
          <EquityCurveChart points={equity} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Domain components</CardTitle>
          <CardDescription>The signature chips at a glance.</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-wrap items-center gap-4">
          <AdherenceChip score={95} />
          <AdherenceChip score={83} />
          <AdherenceChip score={71} />
          <AdherenceChip score={64} />
          <AdherenceChip score={30} />
          <GradeRing score={86} />
          <RValue value={1.8} />
          <RValue value={-0.7} />
          <RValue value={0} />
        </CardContent>
      </Card>
    </div>
  )
}
