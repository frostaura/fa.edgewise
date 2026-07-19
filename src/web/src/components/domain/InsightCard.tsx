import { useState } from 'react'
import {
  AlertTriangleIcon,
  EyeIcon,
  HelpCircleIcon,
  LinkIcon,
  OctagonAlertIcon,
  RefreshCwIcon,
  SparklesIcon,
  ThumbsDownIcon,
  ThumbsUpIcon,
} from 'lucide-react'
import { formatDistanceToNow } from 'date-fns'

import { useSubmitInsightFeedbackMutation } from '@/api/coachApi'
import { humanizeCitation, type Insight, type InsightItem } from '@/features/coach/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardFooter } from '@/components/ui/card'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'

export interface InsightCardProps {
  insight: Insight
  /** When provided, shows a "Regenerate" action. */
  onRegenerate?: () => void
  className?: string
}

interface SectionSpec {
  key: 'observations' | 'deviations' | 'riskFlags' | 'patternLinks'
  title: string
  icon: typeof EyeIcon
  tone?: string
}

const sections: SectionSpec[] = [
  { key: 'observations', title: 'Observations', icon: EyeIcon },
  {
    key: 'deviations',
    title: 'Deviations',
    icon: AlertTriangleIcon,
    tone: 'border-warning/40 bg-warning/10',
  },
  {
    key: 'riskFlags',
    title: 'Risk flags',
    icon: OctagonAlertIcon,
    tone: 'border-destructive/40 bg-destructive/10',
  },
  { key: 'patternLinks', title: 'Patterns', icon: LinkIcon },
]

function ItemRow({ item, tone }: { item: InsightItem; tone?: string }) {
  return (
    <li className={cn('rounded-md border border-transparent px-2 py-1.5 text-sm', tone)}>
      {item.text}
    </li>
  )
}

/**
 * Renders a coach insight: sectioned content, provenance chips resolved from the
 * citation refs, model tag + date footer, 👍/👎 feedback (reason popover on 👎)
 * and an optional regenerate action.
 */
export function InsightCard({ insight, onRegenerate, className }: InsightCardProps) {
  const [submitFeedback, { isLoading: feedbackSaving }] = useSubmitInsightFeedbackMutation()
  const [reason, setReason] = useState('')
  const [reasonOpen, setReasonOpen] = useState(false)

  const content = insight.content ?? {}

  const sendFeedback = (feedback: 'up' | 'down', reasonText?: string) => {
    void submitFeedback({ id: insight.id, feedback, reason: reasonText })
    setReasonOpen(false)
    setReason('')
  }

  return (
    <Card data-slot="insight-card" className={cn('gap-4 py-4', className)}>
      <CardContent className="flex flex-col gap-4 px-4">
        {sections.map(({ key, title, icon: Icon, tone }) => {
          const items = content[key]
          if (!items || items.length === 0) return null
          return (
            <section key={key} className="flex flex-col gap-1.5">
              <h3 className="flex items-center gap-1.5 text-xs font-semibold tracking-wide text-muted-foreground uppercase">
                <Icon className="size-3.5" aria-hidden />
                {title}
              </h3>
              <ul className="flex flex-col gap-1">
                {items.map((item, i) => (
                  <ItemRow key={i} item={item} tone={tone} />
                ))}
              </ul>
            </section>
          )
        })}

        {content.question && (
          <section className="rounded-lg border border-primary/40 bg-primary/5 px-3 py-2">
            <h3 className="flex items-center gap-1.5 text-xs font-semibold tracking-wide text-primary uppercase">
              <HelpCircleIcon className="size-3.5" aria-hidden />
              Question for you
            </h3>
            <p className="mt-1 text-sm font-medium">{content.question.text}</p>
          </section>
        )}

        {content.kudos && (
          <section className="rounded-lg border border-success/40 bg-success/10 px-3 py-2">
            <h3 className="flex items-center gap-1.5 text-xs font-semibold tracking-wide text-success uppercase">
              <SparklesIcon className="size-3.5" aria-hidden />
              Kudos
            </h3>
            <p className="mt-1 text-sm">{content.kudos.text}</p>
          </section>
        )}

        {insight.citations.length > 0 && (
          <div className="flex flex-wrap gap-1.5" aria-label="Provenance">
            {insight.citations.map((citation) => (
              <Badge key={citation.ref} variant="outline" title={citation.label}>
                {humanizeCitation(citation)}
              </Badge>
            ))}
          </div>
        )}
      </CardContent>

      <CardFooter className="flex items-center justify-between gap-2 border-t px-4 [.border-t]:pt-3">
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          <Badge variant={insight.modelTag === 'deterministic' ? 'secondary' : 'default'}>
            {insight.modelTag === 'deterministic' ? 'rules-based' : (insight.modelTag ?? 'coach')}
          </Badge>
          <span>
            {formatDistanceToNow(new Date(insight.createdAt), { addSuffix: true })}
            {insight.promptVersion ? ` · ${insight.promptVersion}` : ''}
          </span>
        </div>

        <div className="flex items-center gap-1">
          {onRegenerate && (
            <Button variant="ghost" size="sm" onClick={onRegenerate} aria-label="Regenerate">
              <RefreshCwIcon className="size-4" aria-hidden />
              Regenerate
            </Button>
          )}
          <Button
            variant="ghost"
            size="icon"
            aria-label="Helpful"
            disabled={feedbackSaving}
            className={cn(insight.feedback === 'up' && 'text-success')}
            onClick={() => sendFeedback('up')}
          >
            <ThumbsUpIcon className="size-4" aria-hidden />
          </Button>
          <Popover open={reasonOpen} onOpenChange={setReasonOpen}>
            <PopoverTrigger asChild>
              <Button
                variant="ghost"
                size="icon"
                aria-label="Not helpful"
                disabled={feedbackSaving}
                className={cn(insight.feedback === 'down' && 'text-destructive')}
              >
                <ThumbsDownIcon className="size-4" aria-hidden />
              </Button>
            </PopoverTrigger>
            <PopoverContent align="end" className="flex w-72 flex-col gap-2">
              <p className="text-sm font-medium">What was off?</p>
              <Textarea
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                placeholder="Optional — helps the coach improve"
                rows={3}
              />
              <Button size="sm" onClick={() => sendFeedback('down', reason || undefined)}>
                Send feedback
              </Button>
            </PopoverContent>
          </Popover>
        </div>
      </CardFooter>
    </Card>
  )
}
