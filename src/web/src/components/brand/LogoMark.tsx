import { cn } from '@/lib/utils'

/**
 * The Edgewise mark: a minimal geometric "E" — three signal bars against a
 * deep-slate tile. Matches the PWA icons generated in scripts/generate-icons.mjs.
 */
export function LogoMark({ className, title = 'Edgewise' }: { className?: string; title?: string }) {
  return (
    <svg
      viewBox="0 0 64 64"
      role="img"
      aria-label={title}
      className={cn('size-8 shrink-0', className)}
    >
      <rect width="64" height="64" rx="14" fill="#0e1420" />
      <rect x="16" y="16" width="6" height="32" rx="2" fill="#2fd9a2" />
      <rect x="26" y="16" width="22" height="6" rx="2" fill="#2fd9a2" />
      <rect x="26" y="29" width="16" height="6" rx="2" fill="#2fd9a2" opacity="0.85" />
      <rect x="26" y="42" width="22" height="6" rx="2" fill="#2fd9a2" />
    </svg>
  )
}
