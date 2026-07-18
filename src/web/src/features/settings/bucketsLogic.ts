// ---------------------------------------------------------------------------
// Bucket allocation helpers. Buckets carry FRACTIONS (0.6 = 60%) on the wire.
// ---------------------------------------------------------------------------

/** Sum of fractions as a percent, rounded to 2dp to swallow float noise. */
export function pctSum(fractions: number[]): number {
  return Math.round(fractions.reduce((total, f) => total + f, 0) * 10000) / 100
}

/** Warning line when a set of allocations does not sum to 100%, else null. */
export function allocationWarning(fractions: number[], label: string): string | null {
  if (fractions.length === 0) return null
  const sum = pctSum(fractions)
  if (Math.abs(sum - 100) < 0.005) return null
  return `${label} sum to ${sum}%, not 100%.`
}

/** Percent input string → fraction, or null when not a valid 0–100 number. */
export function parsePctInput(raw: string): number | null {
  const value = Number(raw)
  if (!Number.isFinite(value) || value < 0 || value > 100) return null
  return Math.round((value / 100) * 1e8) / 1e8
}
