import { describe, expect, it } from 'vitest'

import { computeSuggestedSize, formatMinor, isSizeOverride } from '@/features/journal/sizing'

describe('computeSuggestedSize', () => {
  it('sizes so that qty x stop distance equals the risk budget', () => {
    // R10,000.00 equity at 1% risk => R100.00 budget; 5.00 stop distance => qty 20.
    const s = computeSuggestedSize(1_000_000, 1, 100, 95)
    expect(s.rValueMinor).toBe(10_000)
    expect(s.suggestedQty).toBe(20)
    expect(s.notionalMinor).toBe(200_000)
  })

  it('handles fractional quantities (crypto)', () => {
    const s = computeSuggestedSize(1_000_000, 1, 60_000, 58_000)
    expect(s.suggestedQty).toBeCloseTo(0.05, 8)
  })

  it('supports short setups (stop above entry)', () => {
    const s = computeSuggestedSize(1_000_000, 2, 50, 55)
    expect(s.rValueMinor).toBe(20_000)
    expect(s.suggestedQty).toBe(40)
  })

  it('returns zero qty when prices are degenerate', () => {
    expect(computeSuggestedSize(1_000_000, 1, 100, 100).suggestedQty).toBe(0)
    expect(computeSuggestedSize(1_000_000, 1, 0, 95).suggestedQty).toBe(0)
  })
})

describe('isSizeOverride', () => {
  it('is not an override within the 2% band', () => {
    expect(isSizeOverride(20, 20)).toBe(false)
    expect(isSizeOverride(20.3, 20)).toBe(false) // 1.5%
  })

  it('flags deviations beyond 2%', () => {
    expect(isSizeOverride(21, 20)).toBe(true) // 5%
    expect(isSizeOverride(10, 20)).toBe(true)
  })

  it('treats any qty as override when there is no suggestion', () => {
    expect(isSizeOverride(5, 0)).toBe(true)
    expect(isSizeOverride(0, 0)).toBe(false)
  })
})

describe('formatMinor', () => {
  it('formats cents as major units', () => {
    expect(formatMinor(123_456, 'ZAR')).toMatch(/^R 1.234[.,]56$/)
    expect(formatMinor(-5_000)).toMatch(/-50[.,]00$/)
  })
})
