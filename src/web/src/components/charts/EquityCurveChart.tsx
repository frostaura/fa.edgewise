import * as React from 'react'
import {
  AreaSeries,
  ColorType,
  createChart,
  type IChartApi,
  type ISeriesApi,
  type Time,
} from 'lightweight-charts'

import { cn } from '@/lib/utils'
import { readChartTheme, watchThemeClass, withAlpha } from '@/components/charts/chartTheme'

export interface EquityPoint {
  time: number | string
  value: number
}

export interface EquityCurveChartProps {
  points: EquityPoint[]
  /** Minimal mode for StatCard slots: no axes, grid or interaction. */
  sparkline?: boolean
  className?: string
}

/** Area equity curve on the same lifecycle/theming contract as CandleChart. */
export function EquityCurveChart({ points, sparkline = false, className }: EquityCurveChartProps) {
  const containerRef = React.useRef<HTMLDivElement>(null)
  const chartRef = React.useRef<IChartApi | null>(null)
  const seriesRef = React.useRef<ISeriesApi<'Area'> | null>(null)
  const [themeTick, setThemeTick] = React.useState(0)

  React.useEffect(() => {
    const container = containerRef.current
    if (!container) return

    const chart = createChart(container, {
      width: container.clientWidth,
      height: container.clientHeight,
      layout: {
        background: { type: ColorType.Solid, color: 'transparent' },
        attributionLogo: false,
      },
      ...(sparkline
        ? {
            rightPriceScale: { visible: false },
            timeScale: { visible: false },
            grid: { vertLines: { visible: false }, horzLines: { visible: false } },
            crosshair: {
              vertLine: { visible: false, labelVisible: false },
              horzLine: { visible: false, labelVisible: false },
            },
            handleScroll: false,
            handleScale: false,
          }
        : { timeScale: { timeVisible: false } }),
    })
    chartRef.current = chart
    seriesRef.current = chart.addSeries(AreaSeries, {
      lineWidth: 2,
      priceLineVisible: false,
      lastValueVisible: !sparkline,
    })

    const resizeObserver = new ResizeObserver((entries) => {
      for (const entry of entries) {
        const { width, height } = entry.contentRect
        if (width > 0 && height > 0) chart.applyOptions({ width, height })
      }
    })
    resizeObserver.observe(container)

    const unwatchTheme = watchThemeClass(() => setThemeTick((t) => t + 1))

    return () => {
      unwatchTheme()
      resizeObserver.disconnect()
      seriesRef.current = null
      chartRef.current = null
      chart.remove()
    }
  }, [sparkline])

  React.useEffect(() => {
    const chart = chartRef.current
    const series = seriesRef.current
    if (!chart || !series) return

    const theme = readChartTheme()
    chart.applyOptions({
      layout: {
        background: { type: ColorType.Solid, color: 'transparent' },
        textColor: theme.text,
      },
      ...(sparkline
        ? {}
        : {
            grid: { vertLines: { color: theme.grid }, horzLines: { color: theme.grid } },
            timeScale: { borderColor: theme.border },
            rightPriceScale: { borderColor: theme.border },
          }),
    })
    series.applyOptions({
      lineColor: theme.up,
      topColor: withAlpha(theme.up, 0.28),
      bottomColor: withAlpha(theme.up, 0.02),
    })
    series.setData(points.map((p) => ({ time: p.time as Time, value: p.value })))
    if (sparkline) chart.timeScale().fitContent()
  }, [points, sparkline, themeTick])

  return (
    <div ref={containerRef} className={cn(sparkline ? 'h-10 w-full' : 'h-64 w-full', className)} />
  )
}
