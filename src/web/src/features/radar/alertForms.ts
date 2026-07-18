import { z } from 'zod'

import type { Alert, AlertKind } from '@/api/radarApi'

/**
 * Kind-specific alert parameter schemas — shared by the create-alert dialog
 * and its unit tests.
 */

export const priceCrossSchema = z.object({
  level: z.coerce.number().positive('Level must be greater than 0'),
  direction: z.enum(['above', 'below']),
})

export const pctMoveSchema = z.object({
  pct: z.coerce.number().positive('Percent must be greater than 0'),
  windowMinutes: z.coerce.number().int().positive('Window must be a positive number of minutes'),
})

export const zoneTouchSchema = z.object({
  zoneId: z.uuid('A zone is required'),
})

export const fundingRateSchema = z.object({
  thresholdPct: z.coerce.number().positive('Threshold must be greater than 0'),
})

export const fgExtremeSchema = z
  .object({
    min: z.coerce.number().int().min(0, 'Min must be at least 0').max(100),
    max: z.coerce.number().int().min(0).max(100, 'Max must be at most 100'),
  })
  .refine((v) => v.min < v.max, { message: 'Min must be below max', path: ['min'] })

export const catalystT24Schema = z.object({})

const SCHEMAS: Record<AlertKind, z.ZodType> = {
  priceCross: priceCrossSchema,
  pctMove: pctMoveSchema,
  zoneTouch: zoneTouchSchema,
  fundingRate: fundingRateSchema,
  fgExtreme: fgExtremeSchema,
  catalystT24: catalystT24Schema,
}

/** Kinds that need an instrument selected. */
export const INSTRUMENT_KINDS: AlertKind[] = ['priceCross', 'pctMove', 'fundingRate']

export const KIND_LABELS: Record<AlertKind, string> = {
  priceCross: 'Price cross',
  pctMove: '% move',
  zoneTouch: 'Zone touch',
  fundingRate: 'Funding rate',
  fgExtreme: 'F&G extreme',
  catalystT24: 'Catalyst <24h',
}

export interface AlertValidation {
  success: boolean
  params?: Record<string, unknown>
  errors: string[]
}

/** Validates raw form params for a kind; returns canonical params on success. */
export function validateAlertParams(kind: AlertKind, raw: Record<string, unknown>): AlertValidation {
  const result = SCHEMAS[kind].safeParse(raw)
  if (result.success) {
    return { success: true, params: result.data as Record<string, unknown>, errors: [] }
  }
  return {
    success: false,
    errors: result.error.issues.map((issue) => issue.message),
  }
}

/** One-line params summary for the alert manager table. */
export function paramsSummary(alert: Pick<Alert, 'kind' | 'params'>): string {
  const p = alert.params ?? {}
  switch (alert.kind) {
    case 'priceCross':
      return `${p.direction === 'below' ? 'Below' : 'Above'} ${String(p.level ?? '?')}`
    case 'pctMove':
      return `±${String(p.pct ?? '?')}% in ${String(p.windowMinutes ?? '?')}m`
    case 'zoneTouch':
      return 'Price enters zone'
    case 'fundingRate':
      return `Funding ≥ ${String(p.thresholdPct ?? '?')}%`
    case 'fgExtreme':
      return `F&G ≤ ${String(p.min ?? '?')} or ≥ ${String(p.max ?? '?')}`
    case 'catalystT24':
      return 'Red catalyst within 24h'
    default:
      return ''
  }
}
