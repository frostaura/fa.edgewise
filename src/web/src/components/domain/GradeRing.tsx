import { cn } from '@/lib/utils'
import { adherenceGrade, type AdherenceGrade } from '@/components/domain/AdherenceChip'

const gradeText: Record<AdherenceGrade, string> = {
  A: 'text-success',
  B: 'text-success',
  C: 'text-warning',
  D: 'text-warning',
  F: 'text-destructive',
}

export interface GradeRingProps {
  /** Score 0–100; the arc fills proportionally and the grade sits centered. */
  score: number
  /** Diameter preset. */
  size?: 'sm' | 'md' | 'lg'
  className?: string
}

const sizeClasses = { sm: 'size-10', md: 'size-14', lg: 'size-20' } as const

/** Circular progress ring with the letter grade in the middle. */
export function GradeRing({ score, size = 'md', className }: GradeRingProps) {
  const clamped = Math.max(0, Math.min(100, Math.round(score)))
  const grade = adherenceGrade(clamped)
  const radius = 42
  const circumference = 2 * Math.PI * radius
  const offset = circumference * (1 - clamped / 100)

  return (
    <div
      data-slot="grade-ring"
      data-grade={grade}
      role="img"
      aria-label={`Grade ${grade}, score ${clamped} out of 100`}
      className={cn('relative inline-flex', sizeClasses[size], gradeText[grade], className)}
    >
      <svg viewBox="0 0 100 100" className="size-full -rotate-90">
        <circle cx="50" cy="50" r={radius} fill="none" strokeWidth="8" className="stroke-muted" />
        <circle
          cx="50"
          cy="50"
          r={radius}
          fill="none"
          strokeWidth="8"
          strokeLinecap="round"
          stroke="currentColor"
          strokeDasharray={circumference}
          strokeDashoffset={offset}
        />
      </svg>
      <span
        aria-hidden
        className={cn(
          'absolute inset-0 flex items-center justify-center font-semibold',
          size === 'sm' ? 'text-sm' : size === 'md' ? 'text-lg' : 'text-2xl',
        )}
      >
        {grade}
      </span>
    </div>
  )
}
