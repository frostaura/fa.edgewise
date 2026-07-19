import { describe, expect, it } from 'vitest'

import { paramsSummary, validateAlertParams } from '@/features/radar/alertForms'

describe('alert param form validation', () => {
  it('accepts a valid priceCross', () => {
    const result = validateAlertParams('priceCross', { level: '65000', direction: 'above' })
    expect(result.success).toBe(true)
    expect(result.params).toEqual({ level: 65000, direction: 'above' })
  })

  it('rejects priceCross without a positive level', () => {
    expect(validateAlertParams('priceCross', { level: '0', direction: 'above' }).success).toBe(
      false,
    )
    expect(validateAlertParams('priceCross', { direction: 'sideways' }).success).toBe(false)
  })

  it('accepts pctMove and coerces numbers', () => {
    const result = validateAlertParams('pctMove', { pct: '5', windowMinutes: '60' })
    expect(result.success).toBe(true)
    expect(result.params).toEqual({ pct: 5, windowMinutes: 60 })
  })

  it('rejects pctMove with a non-positive window', () => {
    const result = validateAlertParams('pctMove', { pct: '5', windowMinutes: '0' })
    expect(result.success).toBe(false)
    expect(result.errors.length).toBeGreaterThan(0)
  })

  it('requires a uuid for zoneTouch', () => {
    expect(validateAlertParams('zoneTouch', { zoneId: 'nope' }).success).toBe(false)
    expect(
      validateAlertParams('zoneTouch', { zoneId: '7f3c94a2-4b1d-4a5e-9a7c-2f8e6d1b0c9a' }).success,
    ).toBe(true)
  })

  it('enforces min < max on fgExtreme', () => {
    expect(validateAlertParams('fgExtreme', { min: '20', max: '80' }).success).toBe(true)
    expect(validateAlertParams('fgExtreme', { min: '80', max: '20' }).success).toBe(false)
    expect(validateAlertParams('fgExtreme', { min: '-1', max: '80' }).success).toBe(false)
  })

  it('accepts empty params for catalystT24', () => {
    expect(validateAlertParams('catalystT24', {}).success).toBe(true)
  })
})

describe('paramsSummary', () => {
  it('summarises each kind', () => {
    expect(paramsSummary({ kind: 'priceCross', params: { level: 65000, direction: 'below' } })).toBe(
      'Below 65000',
    )
    expect(paramsSummary({ kind: 'pctMove', params: { pct: 5, windowMinutes: 60 } })).toBe(
      '±5% in 60m',
    )
    expect(paramsSummary({ kind: 'fgExtreme', params: { min: 20, max: 80 } })).toBe(
      'F&G ≤ 20 or ≥ 80',
    )
    expect(paramsSummary({ kind: 'catalystT24', params: {} })).toBe('Red catalyst within 24h')
  })
})
