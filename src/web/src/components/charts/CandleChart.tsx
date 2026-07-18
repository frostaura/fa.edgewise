import * as React from 'react'
import {
  CandlestickSeries,
  ColorType,
  createChart,
  createSeriesMarkers,
  HistogramSeries,
  LineSeries,
  type IChartApi,
  type ISeriesApi,
  type ISeriesMarkersPluginApi,
  type SeriesMarker,
  type Time,
} from 'lightweight-charts'

import { cn } from '@/lib/utils'
import { readChartTheme, watchThemeClass, withAlpha } from '@/components/charts/chartTheme'

export interface CandleBar {
  /** Unix timestamp in seconds or a 'yyyy-mm-dd' business day. */
  time: number | string
  open: number
  high: number
  low: number
  close: number
  volume?: number
}

export interface OverlayLine {
  id: string
  data: { time: number | string; value: number }[]
  color?: string
  title?: string
}

export interface CandleChartProps {
  bars: CandleBar[]
  /** Extra line series drawn over the candles (e.g. moving averages, plan levels). */
  overlays?: OverlayLine[]
  markers?: SeriesMarker<Time>[]
  className?: string
}

/**
 * lightweight-charts v5 candlestick + volume wrapper.
 * Handles create/resize(ResizeObserver)/dispose, and re-themes itself when
 * the .dark class on <html> flips (colors come from the CSS chart tokens).
 */
export function CandleChart({ bars, overlays, markers, className }: CandleChartProps) {
  const containerRef = React.useRef<HTMLDivElement>(null)
  const chartRef = React.useRef<IChartApi | null>(null)
  const candleRef = React.useRef<ISeriesApi<'Candlestick'> | null>(null)
  const volumeRef = React.useRef<ISeriesApi<'Histogram'> | null>(null)
  const overlayRefs = React.useRef<Map<string, ISeriesApi<'Line'>>>(new Map())
  const markersRef = React.useRef<ISeriesMarkersPluginApi<Time> | null>(null)
  const [themeTick, setThemeTick] = React.useState(0)

  // Create / dispose.
  React.useEffect(() => {
    const container = containerRef.current
    if (!container) return
    const overlayMap = overlayRefs.current

    const chart = createChart(container, {
      width: container.clientWidth,
      height: container.clientHeight,
      layout: {
        background: { type: ColorType.Solid, color: 'transparent' },
        attributionLogo: false,
      },
      timeScale: { timeVisible: true, secondsVisible: false },
      crosshair: { mode: 0 },
    })
    chartRef.current = chart

    candleRef.current = chart.addSeries(CandlestickSeries, { borderVisible: false })
    volumeRef.current = chart.addSeries(HistogramSeries, {
      priceFormat: { type: 'volume' },
      priceScaleId: 'volume',
      lastValueVisible: false,
      priceLineVisible: false,
    })
    chart.priceScale('volume').applyOptions({ scaleMargins: { top: 0.8, bottom: 0 } })
    markersRef.current = createSeriesMarkers(candleRef.current, [])

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
      markersRef.current = null
      overlayMap.clear()
      candleRef.current = null
      volumeRef.current = null
      chartRef.current = null
      chart.remove()
    }
  }, [])

  // Data + theme (volume bar colors depend on the theme, so combine them).
  React.useEffect(() => {
    const chart = chartRef.current
    const candles = candleRef.current
    const volume = volumeRef.current
    if (!chart || !candles || !volume) return

    const theme = readChartTheme()

    chart.applyOptions({
      layout: {
        background: { type: ColorType.Solid, color: 'transparent' },
        textColor: theme.text,
      },
      grid: {
        vertLines: { color: theme.grid },
        horzLines: { color: theme.grid },
      },
      timeScale: { borderColor: theme.border },
      rightPriceScale: { borderColor: theme.border },
    })

    candles.applyOptions({
      upColor: theme.up,
      downColor: theme.down,
      wickUpColor: theme.up,
      wickDownColor: theme.down,
    })

    candles.setData(
      bars.map((b) => ({
        time: b.time as Time,
        open: b.open,
        high: b.high,
        low: b.low,
        close: b.close,
      })),
    )
    volume.setData(
      bars.map((b) => ({
        time: b.time as Time,
        value: b.volume ?? 0,
        color: withAlpha(b.close >= b.open ? theme.up : theme.down, 0.45),
      })),
    )
  }, [bars, themeTick])

  // Overlay line series.
  React.useEffect(() => {
    const chart = chartRef.current
    if (!chart) return
    const existing = overlayRefs.current

    // Remove series that are gone.
    const wanted = new Set((overlays ?? []).map((o) => o.id))
    for (const [id, series] of existing) {
      if (!wanted.has(id)) {
        chart.removeSeries(series)
        existing.delete(id)
      }
    }

    const theme = readChartTheme()
    for (const overlay of overlays ?? []) {
      let series = existing.get(overlay.id)
      if (!series) {
        series = chart.addSeries(LineSeries, {
          lineWidth: 2,
          priceLineVisible: false,
          lastValueVisible: false,
        })
        existing.set(overlay.id, series)
      }
      series.applyOptions({ color: overlay.color ?? theme.text, title: overlay.title ?? '' })
      series.setData(overlay.data.map((p) => ({ time: p.time as Time, value: p.value })))
    }
  }, [overlays, themeTick])

  // Markers.
  React.useEffect(() => {
    markersRef.current?.setMarkers(markers ?? [])
  }, [markers])

  return <div ref={containerRef} className={cn('h-80 w-full', className)} />
}
