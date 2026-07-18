import { useReducer, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { format } from 'date-fns'
import {
  CalendarClockIcon,
  GaugeIcon,
  LockIcon,
  NotebookPenIcon,
  PlusIcon,
  ScaleIcon,
  ShieldAlertIcon,
} from 'lucide-react'
import { toast } from 'sonner'

import {
  useGetCockpitStatusQuery,
  useLogDecisionMutation,
  useOverrideCockpitMutation,
  useSetCockpitStateMutation,
  type SelfState,
} from '@/api/cockpitApi'
import { useGetInstrumentsQuery } from '@/api/radarApi'
import { getApiErrorMessage } from '@/api/types'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
} from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'
import { StatusCards } from '@/features/cockpit/StatusCards'
import {
  canSubmitOverride,
  initialOverrideFlow,
  overrideFlowReducer,
  planLock,
} from '@/features/cockpit/cockpitLock'
import { cn } from '@/lib/utils'

const SELF_STATES: { value: SelfState; label: string }[] = [
  { value: 'calm', label: 'Calm' },
  { value: 'tired', label: 'Tired' },
  { value: 'tilted', label: 'Tilted' },
  { value: 'rushed', label: 'Rushed' },
]

/** Home: the daily cockpit — five reads, plan gate, today strip, catalysts. */
export default function CockpitPage() {
  const navigate = useNavigate()
  const { data: status, isLoading } = useGetCockpitStatusQuery(undefined, {
    pollingInterval: 30_000,
  })
  const [setState] = useSetCockpitStateMutation()
  const [override] = useOverrideCockpitMutation()
  const [flow, dispatch] = useReducer(overrideFlowReducer, initialOverrideFlow)

  const lock = planLock(status)

  const declareState = async (state: SelfState) => {
    try {
      await setState({ state }).unwrap()
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not save your state.'))
    }
  }

  const submitOverride = async () => {
    if (!canSubmitOverride(flow)) return
    dispatch({ type: 'submit' })
    try {
      await override({ reason: flow.reason.trim() }).unwrap()
      dispatch({ type: 'succeeded' })
      toast.warning('Override logged. It will count against adherence.')
    } catch (err) {
      dispatch({ type: 'failed', error: getApiErrorMessage(err, 'Override failed.') })
    }
  }

  const ladder = status?.reads.ladder
  const calendar = status?.reads.calendar
  const daily = status?.reads.dailyPnl

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Cockpit"
        description="Your five reads before any trading decision."
        actions={
          status?.activeOverride && (
            <Badge variant="destructive" className="gap-1">
              <ShieldAlertIcon className="size-3" aria-hidden />
              Override active
            </Badge>
          )
        }
      />

      {/* Hero: the five reads */}
      {isLoading || !status ? (
        <div className="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-5">
          {Array.from({ length: 5 }).map((_, i) => (
            <Skeleton key={i} className="h-20 rounded-xl" />
          ))}
        </div>
      ) : (
        <StatusCards status={status} />
      )}

      {/* Big state row: plan gate + self-declared state */}
      <Card className="py-4">
        <CardContent className="flex flex-col gap-4 px-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex items-center gap-3">
            {status && !status.newPlanUnlocked ? (
              <>
                <Button size="lg" variant="secondary" disabled className="gap-2">
                  <LockIcon aria-hidden />
                  New Plan locked
                </Button>
                <Sheet>
                  <SheetTrigger asChild>
                    <Button variant="link" size="sm">
                      Why?
                    </Button>
                  </SheetTrigger>
                  <SheetContent side="right">
                    <SheetHeader>
                      <SheetTitle>Planning is locked</SheetTitle>
                      <SheetDescription>
                        A red read blocks new plans for the rest of the day. Fix the cause or, if
                        you must, override — the override is logged and costs adherence.
                      </SheetDescription>
                    </SheetHeader>
                    <ul className="flex flex-col gap-2 px-4 text-sm">
                      {lock.reasons.map((reason) => (
                        <li key={reason} className="rounded-md border border-destructive/40 p-2">
                          {reason}
                        </li>
                      ))}
                    </ul>
                    <div className="px-4 pb-4">
                      <Button
                        variant="destructive"
                        size="sm"
                        onClick={() => dispatch({ type: 'open' })}
                      >
                        Override anyway…
                      </Button>
                    </div>
                  </SheetContent>
                </Sheet>
              </>
            ) : (
              <Button asChild size="lg" className="gap-2">
                <Link to="/journal/plans/new">
                  <PlusIcon aria-hidden />
                  New Plan
                </Link>
              </Button>
            )}
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <span className="text-xs font-medium text-muted-foreground uppercase">
              How are you?
            </span>
            {SELF_STATES.map((s) => (
              <Button
                key={s.value}
                size="sm"
                variant={status?.reads.state.state === s.value ? 'default' : 'outline'}
                className="rounded-full"
                onClick={() => declareState(s.value)}
              >
                {s.label}
              </Button>
            ))}
          </div>
        </CardContent>
      </Card>

      {/* Ladder banner */}
      {ladder && ladder.rung !== 'full' && (
        <div
          className={cn(
            'flex items-center gap-3 rounded-lg border px-4 py-3 text-sm',
            ladder.locked
              ? 'border-destructive/60 bg-destructive/10'
              : 'border-warning/60 bg-warning/10',
          )}
        >
          <ScaleIcon className="size-4 shrink-0" aria-hidden />
          <span>{ladder.detail}</span>
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-3">
        {/* Today strip */}
        <Card className="py-4 lg:col-span-1">
          <CardContent className="flex flex-col gap-3 px-4">
            <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
              Today
            </span>
            <div className="flex items-baseline justify-between">
              <span className="text-2xl font-semibold tabular-nums">
                {status ? `${status.today.realisedR > 0 ? '+' : ''}${status.today.realisedR}R` : '—'}
              </span>
              <span className="text-sm text-muted-foreground">
                {status?.today.tradesCount ?? 0} trade{status?.today.tradesCount === 1 ? '' : 's'}
              </span>
            </div>
            <div className="flex flex-col gap-1">
              <div className="flex justify-between text-xs text-muted-foreground">
                <span>Daily stop</span>
                <span>{status ? `${status.today.dailyStopProgressPct}%` : '—'}</span>
              </div>
              <div
                className="h-2 overflow-hidden rounded-full bg-muted"
                role="progressbar"
                aria-label="Daily stop progress"
                aria-valuenow={status?.today.dailyStopProgressPct ?? 0}
                aria-valuemin={0}
                aria-valuemax={100}
              >
                <div
                  className={cn(
                    'h-full rounded-full transition-all',
                    daily?.tripped
                      ? 'bg-destructive'
                      : (status?.today.dailyStopProgressPct ?? 0) >= 60
                        ? 'bg-warning'
                        : 'bg-success',
                  )}
                  style={{ width: `${Math.min(100, status?.today.dailyStopProgressPct ?? 0)}%` }}
                />
              </div>
              {daily?.tripped && (
                <p className="text-xs text-destructive">Circuit breaker tripped — stand down.</p>
              )}
            </div>

            {/* Quick actions */}
            <div className="mt-1 flex flex-wrap gap-2">
              <Button size="sm" variant="outline" onClick={() => navigate('/journal')}>
                <NotebookPenIcon aria-hidden />
                Quick Log
              </Button>
              <LogDecisionDialog />
            </div>
          </CardContent>
        </Card>

        {/* Next-24h catalysts */}
        <Card className="py-4 lg:col-span-2">
          <CardContent className="flex flex-col gap-3 px-4">
            <div className="flex items-center justify-between">
              <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                Next 24h catalysts
              </span>
              <Button asChild variant="link" size="sm">
                <Link to="/radar">Open Radar</Link>
              </Button>
            </div>
            {!calendar || calendar.events.length === 0 ? (
              <EmptyState
                icon={CalendarClockIcon}
                title="No catalysts in the next 24 hours"
                className="min-h-32 py-6"
              />
            ) : (
              <ul className="flex flex-col divide-y">
                {calendar.events.map((event) => (
                  <li key={event.id} className="flex items-center gap-3 py-2 text-sm">
                    <span
                      className={cn(
                        'size-2 shrink-0 rounded-full',
                        event.severity === 'red' ? 'bg-destructive' : 'bg-warning',
                      )}
                      aria-label={event.severity}
                    />
                    <span className="w-14 shrink-0 font-mono text-xs text-muted-foreground">
                      {format(new Date(event.at), 'HH:mm')}
                    </span>
                    {event.symbol && (
                      <Badge variant="outline" className="shrink-0">
                        {event.symbol}
                      </Badge>
                    )}
                    <span className="truncate">{event.title}</span>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      </div>

      {!status && !isLoading && (
        <EmptyState
          icon={GaugeIcon}
          title="Cockpit unavailable"
          hint="The cockpit status could not be loaded. Check your connection and try again."
        />
      )}

      {/* Override dialog */}
      <Dialog
        open={flow.dialogOpen}
        onOpenChange={(open) => dispatch({ type: open ? 'open' : 'cancel' })}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Override the red gate</DialogTitle>
            <DialogDescription>
              This is logged to your override history and costs adherence on trades closed today.
              Write down why this trade is worth breaking process for.
            </DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-2">
            <Label htmlFor="override-reason">Reason</Label>
            <Textarea
              id="override-reason"
              value={flow.reason}
              onChange={(e) => dispatch({ type: 'setReason', reason: e.target.value })}
              placeholder="Why is overriding the right call, in one honest sentence?"
              rows={3}
            />
            {flow.error && <p className="text-sm text-destructive">{flow.error}</p>}
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => dispatch({ type: 'cancel' })}>
              Stand down
            </Button>
            <Button
              variant="destructive"
              disabled={!canSubmitOverride(flow)}
              onClick={submitOverride}
            >
              {flow.submitting ? 'Logging…' : 'Log override & unlock'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

/** Two-tap decision logger (kind + optional instrument + reason). Defensive: the
 * /api/decisions endpoint belongs to the Journal vertical and may not exist yet. */
function LogDecisionDialog() {
  const [open, setOpen] = useState(false)
  const [kind, setKind] = useState<'enter' | 'exit' | 'skip'>('skip')
  const [instrumentId, setInstrumentId] = useState<string>('')
  const [reason, setReason] = useState('')
  const [logDecision, { isLoading }] = useLogDecisionMutation()
  const { data: instruments, isError: instrumentsUnavailable } = useGetInstrumentsQuery()

  const submit = async () => {
    try {
      await logDecision({
        kind,
        instrumentId: instrumentId || undefined,
        reason: reason.trim(),
      }).unwrap()
      toast.success('Decision logged.')
      setOpen(false)
      setReason('')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Decision logging is not available yet.'))
    }
  }

  return (
    <>
      <Button size="sm" variant="outline" onClick={() => setOpen(true)}>
        Log Decision
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Log a decision</DialogTitle>
            <DialogDescription>
              Capture enter/exit/skip decisions in the moment — future-you grades them.
            </DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-3">
            <div className="flex gap-2">
              {(['enter', 'exit', 'skip'] as const).map((k) => (
                <Button
                  key={k}
                  size="sm"
                  variant={kind === k ? 'default' : 'outline'}
                  className="flex-1 capitalize"
                  onClick={() => setKind(k)}
                >
                  {k}
                </Button>
              ))}
            </div>
            {!instrumentsUnavailable && (instruments?.length ?? 0) > 0 && (
              <Select value={instrumentId} onValueChange={setInstrumentId}>
                <SelectTrigger>
                  <SelectValue placeholder="Instrument (optional)" />
                </SelectTrigger>
                <SelectContent>
                  {instruments!.map((i) => (
                    <SelectItem key={i.id} value={i.id}>
                      {i.symbol}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
            <Textarea
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder="Why?"
              rows={2}
            />
          </div>
          <DialogFooter>
            <Button disabled={reason.trim().length < 3 || isLoading} onClick={submit}>
              Log it
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )
}
