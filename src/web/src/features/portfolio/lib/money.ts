/** Money helpers — API money is minor units (cents), percentages are fractions. */

const formatters = new Map<string, Intl.NumberFormat>()

function currencyFormatter(currency: string): Intl.NumberFormat {
  let formatter = formatters.get(currency)
  if (!formatter) {
    try {
      formatter = new Intl.NumberFormat('en-ZA', {
        style: 'currency',
        currency,
        maximumFractionDigits: 2,
        minimumFractionDigits: 2,
      })
    } catch {
      formatter = new Intl.NumberFormat('en-ZA', { maximumFractionDigits: 2 })
    }
    formatters.set(currency, formatter)
  }
  return formatter
}

/** R 12 345.67 from 1234567 minor units. */
export function formatMinor(minor: number, currency = 'ZAR'): string {
  return currencyFormatter(currency).format(minor / 100)
}

/** Signed variant: +R 120.00 / -R 45.00 (0 stays unsigned). */
export function formatMinorSigned(minor: number, currency = 'ZAR'): string {
  const abs = formatMinor(Math.abs(minor), currency)
  return minor > 0 ? `+${abs}` : minor < 0 ? `-${abs}` : abs
}

/** 0.1234 → "12.3%" (fraction in, human percent out). */
export function formatPct(fraction: number | null | undefined, digits = 1): string {
  if (fraction === null || fraction === undefined || Number.isNaN(fraction)) return '—'
  return `${(fraction * 100).toFixed(digits)}%`
}

/** Signed percent: +12.3% / -4.5%. */
export function formatPctSigned(fraction: number | null | undefined, digits = 1): string {
  if (fraction === null || fraction === undefined || Number.isNaN(fraction)) return '—'
  const text = formatPct(Math.abs(fraction), digits)
  return fraction > 0 ? `+${text}` : fraction < 0 ? `-${text}` : text
}

/** Tone class for signed money/percent values (RValue-style coloring). */
export function signTone(value: number | null | undefined): string {
  if (!value) return 'text-muted-foreground'
  return value > 0 ? 'text-success' : 'text-destructive'
}
