import { useMemo } from 'react'

import type { ForecastResult } from '@/api/portfolioApi'
import { formatMinor } from './lib/money'

interface Series {
  label: string
  color: string
  dash?: string
  values: number[] // minor units per year, index 0 = year 1
}

/**
 * Lightweight SVG band chart for forecast results: bear/base/bull (deterministic)
 * or P5..P95 (Monte Carlo) over the horizon in years. Pure SVG so the chart works
 * without extending the shared lightweight-charts wrappers.
 */
export function ForecastBandsChart({ result, currency }: { result: ForecastResult; currency: string }) {
  const series = useMemo<Series[]>(() => {
    if (result.monteCarlo && result.monteCarloBands?.length) {
      const bands = result.monteCarloBands
      return [
        { label: 'P95', color: '#4fb06d', dash: '4 3', values: bands.map((b) => b.p95Minor) },
        { label: 'P75', color: '#2f9e8f', dash: '2 2', values: bands.map((b) => b.p75Minor) },
        { label: 'P50', color: '#5b8def', values: bands.map((b) => b.p50Minor) },
        { label: 'P25', color: '#e0a63d', dash: '2 2', values: bands.map((b) => b.p25Minor) },
        { label: 'P5', color: '#e2704a', dash: '4 3', values: bands.map((b) => b.p5Minor) },
      ]
    }
    return [
      { label: 'Bull', color: '#4fb06d', dash: '4 3', values: result.deterministic.map((b) => b.bullMinor) },
      { label: 'Base', color: '#5b8def', values: result.deterministic.map((b) => b.baseMinor) },
      { label: 'Bear', color: '#e2704a', dash: '4 3', values: result.deterministic.map((b) => b.bearMinor) },
    ]
  }, [result])

  const years = series[0]?.values.length ?? 0
  if (years === 0) return null

  const width = 720
  const height = 280
  const pad = { left: 14, right: 14, top: 12, bottom: 26 }
  const max = Math.max(...series.flatMap((s) => s.values)) || 1
  const min = Math.min(0, ...series.flatMap((s) => s.values))

  const x = (yearIdx: number) =>
    pad.left + (years === 1 ? (width - pad.left - pad.right) / 2 : (yearIdx / (years - 1)) * (width - pad.left - pad.right))
  const y = (value: number) =>
    pad.top + (1 - (value - min) / (max - min)) * (height - pad.top - pad.bottom)

  const path = (values: number[]) =>
    values.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ')

  const yearTicks = years <= 8 ? Array.from({ length: years }, (_, i) => i) : [0, Math.floor(years / 2), years - 1]

  return (
    <figure className="flex flex-col gap-2">
      <div className="overflow-x-auto">
        <svg
          viewBox={`0 0 ${width} ${height}`}
          role="img"
          aria-label="Forecast bands by year"
          className="h-64 w-full min-w-96"
        >
          {/* gridlines */}
          {[0.25, 0.5, 0.75].map((f) => (
            <line
              key={f}
              x1={pad.left}
              x2={width - pad.right}
              y1={pad.top + f * (height - pad.top - pad.bottom)}
              y2={pad.top + f * (height - pad.top - pad.bottom)}
              stroke="currentColor"
              strokeOpacity={0.12}
            />
          ))}
          {series.map((s) => (
            <path
              key={s.label}
              d={path(s.values)}
              fill="none"
              stroke={s.color}
              strokeWidth={2}
              strokeDasharray={s.dash}
            />
          ))}
          {yearTicks.map((i) => (
            <text
              key={i}
              x={x(i)}
              y={height - 8}
              textAnchor="middle"
              className="fill-current text-xs opacity-60"
            >
              Y{i + 1}
            </text>
          ))}
        </svg>
      </div>
      <figcaption className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground">
        {series.map((s) => (
          <span key={s.label} className="inline-flex items-center gap-1.5">
            <span className="inline-block h-0.5 w-4" style={{ backgroundColor: s.color }} aria-hidden />
            {s.label}: {formatMinor(s.values[s.values.length - 1], currency)} at year {years}
          </span>
        ))}
      </figcaption>
    </figure>
  )
}
