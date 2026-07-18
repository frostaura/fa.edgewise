import { cn } from '@/lib/utils'

export interface RValueProps {
  /** Result expressed in risk multiples, e.g. 1.8 renders as "+1.8R". */
  value: number
  /** Decimal places (default 1). */
  precision?: number
  className?: string
}

/** Format a risk multiple: +1.8R / -0.7R / 0.0R. */
export function formatR(value: number, precision = 1): string {
  const fixed = value.toFixed(precision)
  const isPositive = value > 0 && Number(fixed) !== 0
  return `${isPositive ? '+' : ''}${fixed}R`
}

/**
 * A monospaced R-multiple: green when positive, red when negative,
 * muted at exactly zero.
 */
export function RValue({ value, precision = 1, className }: RValueProps) {
  const text = formatR(value, precision)
  const tone =
    value > 0 ? 'text-success' : value < 0 ? 'text-destructive' : 'text-muted-foreground'

  return (
    <span
      data-slot="r-value"
      className={cn('font-mono text-sm font-medium tabular-nums', tone, className)}
    >
      {text}
    </span>
  )
}
