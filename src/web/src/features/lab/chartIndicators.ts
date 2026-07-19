import type { CandleBar, OverlayLine } from '@/components/charts/CandleChart'
import type { LabBar } from '@/api/labApi'

/** API bar -> CandleChart bar (ISO ts -> unix seconds). */
export function toCandleBar(bar: LabBar): CandleBar {
  return {
    time: Math.floor(new Date(bar.ts).getTime() / 1000),
    open: bar.o,
    high: bar.h,
    low: bar.l,
    close: bar.c,
    volume: bar.v,
  }
}

export function sma(bars: CandleBar[], period: number): OverlayLine['data'] {
  const out: OverlayLine['data'] = []
  let sum = 0
  for (let i = 0; i < bars.length; i++) {
    sum += bars[i].close
    if (i >= period) sum -= bars[i - period].close
    if (i >= period - 1) out.push({ time: bars[i].time, value: sum / period })
  }
  return out
}

export interface BollingerLines {
  upper: OverlayLine['data']
  mid: OverlayLine['data']
  lower: OverlayLine['data']
}

export function bollinger(bars: CandleBar[], period = 20, sd = 2): BollingerLines {
  const upper: OverlayLine['data'] = []
  const mid: OverlayLine['data'] = []
  const lower: OverlayLine['data'] = []
  for (let i = period - 1; i < bars.length; i++) {
    const window = bars.slice(i - period + 1, i + 1)
    const mean = window.reduce((s, b) => s + b.close, 0) / period
    const variance = window.reduce((s, b) => s + (b.close - mean) ** 2, 0) / period
    const dev = Math.sqrt(variance) * sd
    const time = bars[i].time
    upper.push({ time, value: mean + dev })
    mid.push({ time, value: mean })
    lower.push({ time, value: mean - dev })
  }
  return { upper, mid, lower }
}
