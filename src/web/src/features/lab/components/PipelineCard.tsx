import { useState } from 'react'
import { CheckCircle2Icon, CircleIcon } from 'lucide-react'
import { toast } from 'sonner'

import type { Pipeline, StrategyState } from '@/api/labApi'
import { useTransitionStrategyMutation } from '@/api/labApi'
import { getApiErrorMessage } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
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
import { Textarea } from '@/components/ui/textarea'
import { StateBadge } from '@/features/lab/components/StateBadge'
import { gateChecklist, nextForwardState, STATE_LABELS, STATE_ORDER } from '@/features/lab/ruleTree'

/**
 * Promotion pipeline card: current state, gate checklist with progress bars,
 * and forward/backward transition controls. Forcing past a gate requires a
 * reason and warns that it is permanently logged.
 */
export function PipelineCard({ pipeline }: { pipeline: Pipeline }) {
  const [transition, { isLoading }] = useTransitionStrategyMutation()
  const [confirmTarget, setConfirmTarget] = useState<StrategyState | null>(null)
  const [force, setForce] = useState(false)
  const [reason, setReason] = useState('')

  const gates = gateChecklist(pipeline)
  const forward = nextForwardState(pipeline.state)
  const allMet = gates.every((g) => g.met)
  const backStates = STATE_ORDER.slice(0, STATE_ORDER.indexOf(pipeline.state))

  const submit = async () => {
    if (!confirmTarget) return
    try {
      await transition({
        id: pipeline.strategyId,
        toState: confirmTarget,
        force: force || undefined,
        reason: force ? reason : undefined,
      }).unwrap()
      toast.success(`Strategy moved to ${STATE_LABELS[confirmTarget]}`)
      setConfirmTarget(null)
      setForce(false)
      setReason('')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Transition failed'))
    }
  }

  return (
    <Card data-testid="pipeline-card">
      <CardHeader>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <CardTitle>Promotion pipeline</CardTitle>
          <StateBadge state={pipeline.state} />
        </div>
        <CardDescription>
          Draft → Backtested → Paper → Tiny live → Live. Each step has evidence gates; moving
          backward is always allowed.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {/* Stage ramp */}
        <ol className="flex flex-wrap items-center gap-1 text-xs">
          {STATE_ORDER.map((state, i) => {
            const reached = STATE_ORDER.indexOf(pipeline.state) >= i
            return (
              <li key={state} className="flex items-center gap-1">
                {i > 0 && <span className="text-muted-foreground">→</span>}
                <Badge variant={reached ? 'default' : 'outline'}>{STATE_LABELS[state]}</Badge>
              </li>
            )
          })}
        </ol>

        {/* Gate checklist for the next promotion */}
        {forward && (
          <div className="flex flex-col gap-3" data-testid="gate-checklist">
            <p className="text-sm font-medium">
              Gates for {STATE_LABELS[pipeline.state]} → {STATE_LABELS[forward]}
            </p>
            {gates.map((gate) => (
              <div key={gate.key} className="flex flex-col gap-1">
                <div className="flex items-center gap-2 text-sm">
                  {gate.met ? (
                    <CheckCircle2Icon className="size-4 text-success" aria-hidden />
                  ) : (
                    <CircleIcon className="size-4 text-muted-foreground" aria-hidden />
                  )}
                  <span>{gate.label}</span>
                </div>
                <div className="h-1.5 w-full overflow-hidden rounded-full bg-muted">
                  <div
                    className="h-full rounded-full bg-primary"
                    style={{ width: `${Math.round(gate.progress * 100)}%` }}
                  />
                </div>
                <p className="text-xs text-muted-foreground">{gate.detail}</p>
              </div>
            ))}
          </div>
        )}

        <div className="flex flex-wrap items-center gap-2">
          {forward && (
            <Button
              size="sm"
              disabled={isLoading}
              variant={allMet ? 'default' : 'outline'}
              onClick={() => {
                setForce(!allMet)
                setConfirmTarget(forward)
              }}
            >
              {allMet ? `Promote to ${STATE_LABELS[forward]}` : `Force to ${STATE_LABELS[forward]}…`}
            </Button>
          )}
          {backStates.length > 0 && (
            <Select onValueChange={(v) => setConfirmTarget(v as StrategyState)}>
              <SelectTrigger size="sm" className="w-40">
                <SelectValue placeholder="Move back to…" />
              </SelectTrigger>
              <SelectContent>
                {backStates.map((state) => (
                  <SelectItem key={state} value={state}>
                    {STATE_LABELS[state]}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
        </div>

        {/* History */}
        {pipeline.history.length > 0 && (
          <div className="flex flex-col gap-1">
            <p className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
              History
            </p>
            <ul className="flex flex-col gap-1 text-xs text-muted-foreground">
              {pipeline.history.slice(0, 6).map((entry, i) => (
                <li key={i} className="flex flex-wrap items-center gap-1">
                  <span>{new Date(entry.at).toLocaleDateString()}</span>
                  <span>
                    {STATE_LABELS[entry.fromState]} → {STATE_LABELS[entry.toState]}
                  </span>
                  {(entry.evidence as { forced?: boolean } | undefined)?.forced && (
                    <Badge variant="warning">forced</Badge>
                  )}
                </li>
              ))}
            </ul>
          </div>
        )}
      </CardContent>

      {/* Confirm dialog */}
      <Dialog open={confirmTarget !== null} onOpenChange={(open) => !open && setConfirmTarget(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>
              Move to {confirmTarget ? STATE_LABELS[confirmTarget] : ''}?
            </DialogTitle>
            <DialogDescription>
              {confirmTarget && STATE_ORDER.indexOf(confirmTarget) < STATE_ORDER.indexOf(pipeline.state)
                ? 'Moving backward is always allowed and is recorded in the pipeline history.'
                : 'The gate evidence snapshot is recorded with this transition.'}
            </DialogDescription>
          </DialogHeader>

          {confirmTarget && STATE_ORDER.indexOf(confirmTarget) > STATE_ORDER.indexOf(pipeline.state) && (
            <div className="flex flex-col gap-3">
              <label className="flex items-center gap-2 text-sm">
                <Checkbox checked={force} onCheckedChange={(v) => setForce(v === true)} />
                Force past unmet gates
              </label>
              {force && (
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="force-reason">Reason (required, permanently logged)</Label>
                  <Textarea
                    id="force-reason"
                    value={reason}
                    onChange={(e) => setReason(e.target.value)}
                    placeholder="Why are you overriding the gate?"
                  />
                  <p className="text-xs text-warning">
                    Forced promotions are logged with your reason in the pipeline evidence.
                  </p>
                </div>
              )}
            </div>
          )}

          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmTarget(null)}>
              Cancel
            </Button>
            <Button onClick={submit} disabled={isLoading || (force && reason.trim() === '')}>
              Confirm
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}
