import { cn } from '@/lib/utils'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'

export type AdherenceGrade = 'A' | 'B' | 'C' | 'D' | 'F'

/** Map a 0–100 adherence score to a letter grade (rubric v1: A≥90 B≥75 C≥60 D≥40). */
export function adherenceGrade(score: number): AdherenceGrade {
  if (score >= 90) return 'A'
  if (score >= 75) return 'B'
  if (score >= 60) return 'C'
  if (score >= 40) return 'D'
  return 'F'
}

const gradeClasses: Record<AdherenceGrade, string> = {
  A: 'bg-success text-success-foreground',
  B: 'bg-success/80 text-success-foreground',
  C: 'bg-warning text-warning-foreground',
  D: 'bg-warning/80 text-warning-foreground',
  F: 'bg-destructive text-destructive-foreground',
}

export interface AdherenceChipProps {
  /** Adherence score, 0–100. */
  score: number
  className?: string
}

/**
 * Grade pill (A–F) on a success→destructive ramp. The exact score is shown
 * in a tooltip on hover/focus.
 */
export function AdherenceChip({ score, className }: AdherenceChipProps) {
  const clamped = Math.max(0, Math.min(100, Math.round(score)))
  const grade = adherenceGrade(clamped)

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          data-slot="adherence-chip"
          data-grade={grade}
          className={cn(
            'inline-flex size-6 cursor-default items-center justify-center rounded-full text-xs font-semibold',
            gradeClasses[grade],
            className,
          )}
          aria-label={`Plan adherence ${clamped} out of 100 (grade ${grade})`}
        >
          {grade}
        </span>
      </TooltipTrigger>
      <TooltipContent>Adherence {clamped} / 100</TooltipContent>
    </Tooltip>
  )
}
