/**
 * Pure position-sizing math, mirroring the server's rule
 * (PlanService.ComputeSizePreview): risk budget = equity x riskPct
 * (riskPct in percent points, 1 = 1%), qty = budget / |entry - stop|.
 * The server result is authoritative; this exists for instant local feedback
 * and unit tests.
 */

export interface SizeSuggestion {
  suggestedQty: number
  rValueMinor: number
  notionalMinor: number
}

export const OVERRIDE_TOLERANCE = 0.02

export function computeSuggestedSize(
  equityMinor: number,
  riskPct: number,
  entryPrice: number,
  stopPrice: number,
): SizeSuggestion {
  const rValueMinor = Math.round((equityMinor * riskPct) / 100)
  const distance = Math.abs(entryPrice - stopPrice)
  const suggestedQty =
    distance <= 0 || entryPrice <= 0 || stopPrice <= 0
      ? 0
      : Number((rValueMinor / 100 / distance).toFixed(8))
  const notionalMinor = Math.round(suggestedQty * entryPrice * 100)
  return { suggestedQty, rValueMinor, notionalMinor }
}

/** True when a manually entered qty deviates from the suggestion by more than the tolerance. */
export function isSizeOverride(
  qty: number,
  suggestedQty: number,
  tolerance: number = OVERRIDE_TOLERANCE,
): boolean {
  if (suggestedQty <= 0) return qty > 0
  return Math.abs(qty - suggestedQty) / suggestedQty > tolerance
}

/** Format minor units (cents) as a money string, e.g. 123456 -> "R 1,234.56". */
export function formatMinor(minor: number, currency = ''): string {
  const major = minor / 100
  const formatted = major.toLocaleString('en-ZA', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })
  const symbol = currency === 'ZAR' ? 'R ' : currency ? `${currency} ` : ''
  return `${symbol}${formatted}`
}
