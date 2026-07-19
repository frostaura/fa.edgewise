import type { AllocationSlice } from '@/api/portfolioApi'

/**
 * Allocation ring math: turns value slices into normalized segments with start/end
 * angles and a CSS conic-gradient, independent of the API's own pct fields so the
 * ring always closes even with rounding drift.
 */

/** Categorical ring palette (works on light and dark surfaces). */
export const RING_COLORS = [
  '#2f9e8f',
  '#5b8def',
  '#e0a63d',
  '#c76fd1',
  '#e2704a',
  '#4fb06d',
  '#8290a5',
] as const

export interface RingSegment {
  key: string
  label: string
  valueMinor: number
  /** Normalized share of the ring, 0..1. */
  share: number
  startDeg: number
  endDeg: number
  color: string
}

/** Normalizes slices (non-positive values dropped) into ring segments. */
export function buildRingSegments(slices: AllocationSlice[]): RingSegment[] {
  const positive = slices.filter((s) => s.valueMinor > 0)
  const total = positive.reduce((sum, s) => sum + s.valueMinor, 0)
  if (total <= 0) return []

  let angle = 0
  return positive.map((slice, index) => {
    const share = slice.valueMinor / total
    const startDeg = angle
    const endDeg = index === positive.length - 1 ? 360 : angle + share * 360
    angle = endDeg
    return {
      key: slice.key,
      label: slice.label,
      valueMinor: slice.valueMinor,
      share,
      startDeg,
      endDeg,
      color: RING_COLORS[index % RING_COLORS.length],
    }
  })
}

/** conic-gradient(...) covering the whole ring; empty string when no data. */
export function buildConicGradient(segments: RingSegment[]): string {
  if (segments.length === 0) return ''
  const stops = segments
    .map((s) => `${s.color} ${s.startDeg.toFixed(2)}deg ${s.endDeg.toFixed(2)}deg`)
    .join(', ')
  return `conic-gradient(${stops})`
}

/**
 * Drift check mirroring the backend rule: band = 25% relative of target,
 * flag when |actual - target| > 1.5 x band. Fractions in, boolean out.
 */
export function isDrifted(actualPct: number, targetPct: number): boolean {
  if (targetPct <= 0) return false
  return Math.abs(actualPct - targetPct) > 1.5 * (0.25 * targetPct)
}
