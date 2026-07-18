import { useEffect, useMemo, useState } from 'react'
import { CalendarCheckIcon, CheckIcon, ChevronLeftIcon, ChevronRightIcon, FlameIcon } from 'lucide-react'
import { addDays, format, parseISO, startOfWeek } from 'date-fns'

import { useGetWeeklyReviewQuery, useUpdateWeeklyReviewMutation } from '@/api/coachApi'
import { EmptyState } from '@/components/domain/EmptyState'
import { InsightCard } from '@/components/domain/InsightCard'
import { PageHeader } from '@/components/domain/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'

function mondayOf(date: Date): string {
  return format(startOfWeek(date, { weekStartsOn: 1 }), 'yyyy-MM-dd')
}

/**
 * The weekly review flow: coach-drafted pack (InsightCard), the trader's own notes
 * (markdown), a single focus commitment, completion + streak.
 */
export default function WeeklyReviewPage() {
  const [weekStart, setWeekStart] = useState(() => mondayOf(new Date()))
  const { data, isLoading, isFetching } = useGetWeeklyReviewQuery(weekStart)
  const [updateReview, { isLoading: saving }] = useUpdateWeeklyReviewMutation()

  const [notes, setNotes] = useState('')
  const [focus, setFocus] = useState('')

  useEffect(() => {
    setNotes(data?.review.userEditsMd ?? '')
    setFocus(data?.review.focusCommitment ?? '')
  }, [data?.review.id, data?.review.userEditsMd, data?.review.focusCommitment])

  const weekLabel = useMemo(() => {
    const start = parseISO(weekStart)
    return `${format(start, 'd MMM')} – ${format(addDays(start, 6), 'd MMM yyyy')}`
  }, [weekStart])

  const completed = Boolean(data?.review.completedAt)

  const save = (complete: boolean) =>
    void updateReview({ weekStart, userEditsMd: notes, focusCommitment: focus, complete })

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Weekly Review"
        description="A guided retro over last week's process, not just its P&L."
        actions={
          <div className="flex items-center gap-2">
            <Button
              variant="outline"
              size="icon"
              aria-label="Previous week"
              onClick={() => setWeekStart(format(addDays(parseISO(weekStart), -7), 'yyyy-MM-dd'))}
            >
              <ChevronLeftIcon className="size-4" aria-hidden />
            </Button>
            <span className="min-w-40 text-center text-sm font-medium">{weekLabel}</span>
            <Button
              variant="outline"
              size="icon"
              aria-label="Next week"
              disabled={weekStart >= mondayOf(new Date())}
              onClick={() => setWeekStart(format(addDays(parseISO(weekStart), 7), 'yyyy-MM-dd'))}
            >
              <ChevronRightIcon className="size-4" aria-hidden />
            </Button>
          </div>
        }
      />

      {isLoading || isFetching ? (
        <div className="flex flex-col gap-4">
          <Skeleton className="h-48 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : !data ? (
        <EmptyState
          icon={CalendarCheckIcon}
          title="No review available"
          hint="The coach could not assemble a pack for this week."
        />
      ) : (
        <div className="grid gap-6 lg:grid-cols-[2fr_1fr]">
          <div className="flex flex-col gap-4">
            {data.pack ? (
              <InsightCard insight={data.pack} />
            ) : (
              <EmptyState
                icon={CalendarCheckIcon}
                title="No pack for this week"
                hint="The coach drafts a pack from the week's closed trades every Sunday night."
                className="min-h-40"
              />
            )}

            <Card>
              <CardHeader>
                <CardTitle className="text-base">Your notes</CardTitle>
              </CardHeader>
              <CardContent className="flex flex-col gap-3">
                <Textarea
                  value={notes}
                  onChange={(e) => setNotes(e.target.value)}
                  placeholder="What actually happened this week? Markdown welcome."
                  rows={8}
                  disabled={completed}
                  aria-label="Weekly review notes"
                />
                <Input
                  value={focus}
                  onChange={(e) => setFocus(e.target.value)}
                  placeholder="One focus commitment for next week…"
                  disabled={completed}
                  aria-label="Focus commitment"
                />
                <div className="flex items-center justify-between">
                  {completed ? (
                    <Badge variant="success">
                      <CheckIcon aria-hidden />
                      Completed {format(parseISO(data.review.completedAt!), 'd MMM HH:mm')}
                    </Badge>
                  ) : (
                    <Button variant="outline" size="sm" disabled={saving} onClick={() => save(false)}>
                      Save draft
                    </Button>
                  )}
                  {!completed && (
                    <Button size="sm" disabled={saving} onClick={() => save(true)}>
                      <CheckIcon aria-hidden />
                      Complete review
                    </Button>
                  )}
                </div>
              </CardContent>
            </Card>
          </div>

          <div className="flex flex-col gap-4">
            <Card>
              <CardContent className="flex items-center gap-3">
                <FlameIcon
                  className={data.review.streakCount > 0 ? 'size-8 text-warning' : 'size-8 text-muted-foreground'}
                  aria-hidden
                />
                <div>
                  <p className="text-2xl font-semibold tabular-nums">{data.review.streakCount}</p>
                  <p className="text-xs text-muted-foreground">week streak</p>
                </div>
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle className="text-base">Past reviews</CardTitle>
              </CardHeader>
              <CardContent className="flex flex-col gap-1.5">
                {data.pastReviews.length === 0 && (
                  <p className="text-sm text-muted-foreground">No earlier reviews yet.</p>
                )}
                {data.pastReviews.map((review) => (
                  <button
                    key={review.id}
                    type="button"
                    onClick={() => setWeekStart(review.weekStartDate)}
                    className="flex items-center justify-between rounded-md px-2 py-1.5 text-left text-sm transition-colors hover:bg-muted"
                  >
                    <span>{format(parseISO(review.weekStartDate), 'd MMM yyyy')}</span>
                    {review.completedAt ? (
                      <Badge variant="success">done</Badge>
                    ) : (
                      <Badge variant="outline">draft</Badge>
                    )}
                  </button>
                ))}
              </CardContent>
            </Card>
          </div>
        </div>
      )}
    </div>
  )
}
