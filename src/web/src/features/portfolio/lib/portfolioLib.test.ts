import { describe, expect, it } from 'vitest'

import type { AllocationSlice } from '@/api/portfolioApi'
import { buildConicGradient, buildRingSegments, isDrifted } from './allocation'
import { forecastFormReducer, initialForecastForm, toAssumptions, validateForecastForm } from './forecastForm'
import { ladderBadge } from './ladder'
import { formatMinor, formatPct, formatPctSigned, signTone } from './money'

const slices: AllocationSlice[] = [
  { key: 'a', label: 'Long-term', valueMinor: 60000, pct: 0.6 },
  { key: 'b', label: 'Trading', valueMinor: 25000, pct: 0.25 },
  { key: 'c', label: 'Cash', valueMinor: 15000, pct: 0.15 },
]

describe('allocation ring math', () => {
  it('normalizes shares and closes the ring at 360deg', () => {
    const segments = buildRingSegments(slices)
    expect(segments).toHaveLength(3)
    expect(segments[0].share).toBeCloseTo(0.6, 5)
    expect(segments[0].startDeg).toBe(0)
    expect(segments[0].endDeg).toBeCloseTo(216, 3)
    expect(segments[1].startDeg).toBeCloseTo(216, 3)
    expect(segments[2].endDeg).toBe(360)
  })

  it('drops non-positive slices and handles empty input', () => {
    expect(buildRingSegments([])).toEqual([])
    expect(buildRingSegments([{ key: 'x', label: 'X', valueMinor: 0, pct: 0 }])).toEqual([])
    const segments = buildRingSegments([
      { key: 'x', label: 'X', valueMinor: -5, pct: 0 },
      { key: 'y', label: 'Y', valueMinor: 10, pct: 1 },
    ])
    expect(segments).toHaveLength(1)
    expect(segments[0].share).toBe(1)
  })

  it('renders a conic gradient string', () => {
    const gradient = buildConicGradient(buildRingSegments(slices))
    expect(gradient).toMatch(/^conic-gradient\(/)
    expect(gradient).toContain('216.00deg')
    expect(buildConicGradient([])).toBe('')
  })

  it('mirrors the backend drift rule (band = 25% relative, flag over 1.5x band)', () => {
    // target 25%: band 6.25 points, threshold 9.375 points.
    expect(isDrifted(0.25, 0.25)).toBe(false)
    expect(isDrifted(0.33, 0.25)).toBe(false)
    expect(isDrifted(0.35, 0.25)).toBe(true)
    expect(isDrifted(0.1, 0.25)).toBe(true)
    expect(isDrifted(0.9, 0)).toBe(false)
  })
})

describe('ladder badge mapping', () => {
  it('maps every state to a colored chip', () => {
    expect(ladderBadge('normal')).toMatchObject({ label: 'Normal', variant: 'success' })
    expect(ladderBadge('riskHalved')).toMatchObject({ label: 'Risk halved', variant: 'warning' })
    expect(ladderBadge('paused')).toMatchObject({ label: 'Paused', variant: 'destructive' })
    expect(ladderBadge('paperProposed')).toMatchObject({ label: 'Paper proposed', variant: 'secondary' })
    expect(ladderBadge(undefined).label).toBe('—')
  })
})

describe('money formatting', () => {
  it('formats minor units and fractions', () => {
    expect(formatMinor(1234567, 'ZAR')).toContain('12')
    expect(formatMinor(1234567, 'ZAR')).toContain('345')
    expect(formatPct(0.1234)).toBe('12.3%')
    expect(formatPct(null)).toBe('—')
    expect(formatPctSigned(0.05)).toBe('+5.0%')
    expect(formatPctSigned(-0.05)).toBe('-5.0%')
    expect(signTone(5)).toBe('text-success')
    expect(signTone(-5)).toBe('text-destructive')
    expect(signTone(0)).toBe('text-muted-foreground')
  })
})

describe('forecast assumptions form reducer', () => {
  it('adds, edits and removes assets with split bookkeeping', () => {
    let state = initialForecastForm
    state = forecastFormReducer(state, { type: 'addAsset', key: 'STX500', currentValueMinor: 100000 })
    state = forecastFormReducer(state, { type: 'addAsset', key: 'BTC' })
    state = forecastFormReducer(state, { type: 'addAsset', key: 'BTC' }) // duplicate ignored
    expect(state.assets).toHaveLength(2)

    state = forecastFormReducer(state, {
      type: 'setAssetField',
      key: 'BTC',
      field: 'annualGrowthPct',
      value: 0.2,
    })
    state = forecastFormReducer(state, { type: 'setSplit', key: 'BTC', value: 0.4 })
    expect(state.assets.find((a) => a.key === 'BTC')?.annualGrowthPct).toBe(0.2)
    expect(state.splits.BTC).toBe(0.4)
    expect(state.dirty).toBe(true)

    state = forecastFormReducer(state, { type: 'removeAsset', key: 'BTC' })
    expect(state.assets).toHaveLength(1)
    expect(state.splits.BTC).toBeUndefined()
  })

  it('clamps horizon, haircut and paths', () => {
    let state = forecastFormReducer(initialForecastForm, { type: 'setHorizon', value: 50 })
    expect(state.horizonYears).toBe(20)
    state = forecastFormReducer(state, { type: 'setHorizon', value: 0 })
    expect(state.horizonYears).toBe(1)
    state = forecastFormReducer(state, { type: 'setHaircut', value: 2 })
    expect(state.haircutPct).toBe(1)
    state = forecastFormReducer(state, { type: 'setPaths', value: 5 })
    expect(state.paths).toBe(100)
  })

  it('loads assumptions clean and seeds dirty', () => {
    const assumptions = {
      assets: [{ key: 'etf', currentValueMinor: 5000, annualGrowthPct: 0.1, annualVolPct: 0.15 }],
      contribution: { amountMinor: 1000, annualIncreasePct: 0.05, splits: { etf: 1 } },
      reinvest: true,
      horizonYears: 5,
      haircutPct: 0.2,
    }
    const loaded = forecastFormReducer(initialForecastForm, { type: 'load', assumptions })
    expect(loaded.dirty).toBe(false)
    expect(loaded.assets).toHaveLength(1)
    expect(loaded.horizonYears).toBe(5)

    const seeded = forecastFormReducer(initialForecastForm, { type: 'seed', assumptions })
    expect(seeded.dirty).toBe(true)

    const roundTrip = toAssumptions(loaded)
    expect(roundTrip.contribution.splits).toEqual({ etf: 1 })
    expect(roundTrip.horizonYears).toBe(5)
  })

  it('validates blocking problems', () => {
    expect(validateForecastForm(initialForecastForm)).toMatch(/at least one asset/i)
    let state = forecastFormReducer(initialForecastForm, { type: 'addAsset', key: 'x' })
    expect(validateForecastForm(state)).toBeNull()
    state = forecastFormReducer(state, {
      type: 'setAssetField',
      key: 'x',
      field: 'annualGrowthPct',
      value: 5,
    })
    expect(validateForecastForm(state)).toMatch(/growth/i)
  })
})
