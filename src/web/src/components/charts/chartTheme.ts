/** Reads chart colors from the live CSS design tokens. */
export interface ChartTheme {
  up: string
  down: string
  grid: string
  text: string
  border: string
}

export function readChartTheme(): ChartTheme {
  const styles = getComputedStyle(document.documentElement)
  const v = (name: string, fallback: string) => styles.getPropertyValue(name).trim() || fallback
  return {
    up: v('--chart-up', '#26a69a'),
    down: v('--chart-down', '#ef5350'),
    grid: v('--chart-grid', '#e5e7eb'),
    text: v('--muted-foreground', '#6b7280'),
    border: v('--border', '#e5e7eb'),
  }
}

/** Best-effort alpha for a CSS color string (oklch/rgb modern syntax or hex). */
export function withAlpha(color: string, alpha: number): string {
  if (color.startsWith('#')) {
    const hex = Math.round(alpha * 255)
      .toString(16)
      .padStart(2, '0')
    return color.length === 7 ? `${color}${hex}` : color
  }
  if (/^(oklch|oklab|rgb|hsl)\(/.test(color) && !color.includes('/')) {
    return color.replace(/\)\s*$/, ` / ${alpha})`)
  }
  return color
}

/**
 * Invoke `onChange` whenever the .dark class flips on <html>.
 * Returns a cleanup function.
 */
export function watchThemeClass(onChange: () => void): () => void {
  const observer = new MutationObserver(onChange)
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })
  return () => observer.disconnect()
}
