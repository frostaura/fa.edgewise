import * as React from 'react'
import { Link, useParams } from 'react-router'
import {
  ArrowLeftIcon,
  CandlestickChartIcon,
  PaperclipIcon,
  RefreshCwIcon,
  SparklesIcon,
} from 'lucide-react'
import { toast } from 'sonner'

import {
  useCloseTradeMutation,
  useGetTradeQuery,
  usePatchTradeMutation,
  useRecomputeAdherenceMutation,
  useReopenTradeMutation,
  type AdherenceDeduction,
  type EmotionTag,
  type TradeDetail,
} from '@/api/journalApi'
import { getApiErrorMessage } from '@/api/types'
import { AdherenceChip } from '@/components/domain/AdherenceChip'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { RValue } from '@/components/domain/RValue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Separator } from '@/components/ui/separator'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'
import { formatMinor } from '@/features/journal/sizing'

const EMOTIONS: EmotionTag[] = ['none', 'calm', 'fomo', 'tilt', 'bored', 'rushed']

function formatDateTime(iso?: string) {
  if (!iso) return '—'
  return new Date(iso).toLocaleString(undefined, {
    day: '2-digit',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  })
}

function holdingLabel(seconds?: number) {
  if (seconds === undefined || seconds === null) return '—'
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`
  if (seconds < 86400) return `${(seconds / 3600).toFixed(1)}h`
  return `${(seconds / 86400).toFixed(1)}d`
}

function parseDeductions(json?: string): AdherenceDeduction[] {
  if (!json) return []
  try {
    const parsed = JSON.parse(json) as AdherenceDeduction[]
    return Array.isArray(parsed) ? parsed : []
  } catch {
    return []
  }
}

/** One row of the plan-vs-execution comparison. */
function CompareRow({
  label,
  planned,
  actual,
  diff,
}: {
  label: string
  planned: React.ReactNode
  actual: React.ReactNode
  diff?: boolean
}) {
  return (
    <div className="grid grid-cols-3 items-baseline gap-2 py-1.5 text-sm">
      <span className="text-muted-foreground">{label}</span>
      <span>{planned}</span>
      <span className={cn(diff && 'font-medium text-warning')}>{actual}</span>
    </div>
  )
}

function PlanVsExecution({ detail }: { detail: TradeDetail }) {
  const { trade, plan } = detail
  if (!plan) {
    return (
      <Card className="gap-2 py-4">
        <CardHeader className="px-4">
          <CardTitle className="text-sm">Plan vs execution</CardTitle>
        </CardHeader>
        <CardContent className="px-4">
          <p className="text-sm text-muted-foreground">
            Unplanned trade — there is no plan to compare against. The adherence score is capped
            at 60 for unplanned trades.
          </p>
        </CardContent>
      </Card>
    )
  }

  const stopMoved =
    detail.planVersions.length > 1 &&
    detail.planVersions.some((v) => {
      try {
        const fields = JSON.parse(v.fieldsJson) as { stopPrice?: number }
        return fields.stopPrice !== undefined && fields.stopPrice !== plan.stopPrice
      } catch {
        return false
      }
    })
  const sizeDiff = plan.sizeQty > 0 && Math.abs(trade.qty - plan.sizeQty) / plan.sizeQty > 0.02

  return (
    <Card className="gap-2 py-4">
      <CardHeader className="px-4">
        <CardTitle className="flex items-center justify-between text-sm">
          Plan vs execution
          {plan.sizeOverridden && <Badge variant="warning">size overridden</Badge>}
        </CardTitle>
      </CardHeader>
      <CardContent className="px-4">
        <div className="grid grid-cols-3 gap-2 border-b pb-1 text-xs font-medium tracking-wide text-muted-foreground uppercase">
          <span>Field</span>
          <span>Planned</span>
          <span>Actual</span>
        </div>
        <CompareRow
          label="Direction"
          planned={plan.direction}
          actual={trade.direction}
          diff={plan.direction !== trade.direction}
        />
        <CompareRow
          label="Trigger / entry"
          planned={<span className="text-xs">{plan.triggerText ?? '—'}</span>}
          actual={`entered @ ${trade.avgEntryPrice}`}
        />
        <CompareRow
          label="Stop"
          planned={plan.stopPrice}
          actual={
            trade.avgExitPrice !== undefined && trade.avgExitPrice !== null
              ? `exit @ ${trade.avgExitPrice}`
              : 'still open'
          }
          diff={stopMoved}
        />
        <CompareRow label="Size" planned={plan.sizeQty} actual={trade.qty} diff={sizeDiff} />
        <CompareRow
          label="Target rule"
          planned={<span className="font-mono text-xs break-all">{plan.targetRuleJson ?? '—'}</span>}
          actual={
            trade.rRealised !== undefined && trade.rRealised !== null ? (
              <RValue value={trade.rRealised} />
            ) : (
              '—'
            )
          }
        />
        <CompareRow
          label="Invalidation"
          planned={<span className="text-xs">{plan.invalidationNote ?? '—'}</span>}
          actual={trade.status}
        />
        {detail.planVersions.length > 1 && (
          <p className="mt-2 text-xs text-muted-foreground">
            {detail.planVersions.length} plan versions recorded — stop changes after entry are
            visible to the adherence rubric.
          </p>
        )}
      </CardContent>
    </Card>
  )
}

function CloseTradeDialog({
  tradeId,
  open,
  onOpenChange,
}: {
  tradeId: string
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const [closeTrade, { isLoading }] = useCloseTradeMutation()
  const [price, setPrice] = React.useState('')
  const [exitRule, setExitRule] = React.useState('matchedRule')

  const submit = async () => {
    try {
      await closeTrade({
        id: tradeId,
        avgExitPrice: Number(price),
        closedAt: new Date().toISOString(),
        exitRule,
      }).unwrap()
      toast.success('Trade closed — adherence scored')
      onOpenChange(false)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not close the trade'))
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-sm">
        <DialogHeader>
          <DialogTitle>Close trade</DialogTitle>
        </DialogHeader>
        <div className="flex flex-col gap-3">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="close-price">Exit price</Label>
            <Input
              id="close-price"
              type="number"
              inputMode="decimal"
              step="any"
              value={price}
              onChange={(e) => setPrice(e.target.value)}
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label>How did the exit happen?</Label>
            <Select value={exitRule} onValueChange={setExitRule}>
              <SelectTrigger className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="matchedRule">Matched my exit rule</SelectItem>
                <SelectItem value="earlyDiscretionary">Early / discretionary</SelectItem>
                <SelectItem value="stopHit">Stop hit</SelectItem>
                <SelectItem value="timeStop">Time stop</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button onClick={() => void submit()} disabled={Number(price) <= 0 || isLoading}>
            {isLoading ? 'Closing…' : 'Close trade'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

export default function TradeDetailPage() {
  const { id } = useParams<{ id: string }>()
  const { data: detail, isLoading } = useGetTradeQuery(id!, { skip: !id })
  const [patchTrade, { isLoading: saving }] = usePatchTradeMutation()
  const [reopenTrade] = useReopenTradeMutation()
  const [recompute, { isLoading: recomputing }] = useRecomputeAdherenceMutation()

  const [notes, setNotes] = React.useState('')
  const [tagsInput, setTagsInput] = React.useState('')
  const [tagsDirty, setTagsDirty] = React.useState(false)
  const [closeOpen, setCloseOpen] = React.useState(false)

  React.useEffect(() => {
    if (detail && !tagsDirty) setTagsInput(detail.tags.join(', '))
  }, [detail, tagsDirty])

  if (isLoading || !detail) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-64 w-full" />
      </div>
    )
  }

  const { trade } = detail
  const deductions = parseDeductions(detail.adherence?.deductionsJson)

  const saveNotes = async () => {
    try {
      await patchTrade({ id: trade.id, notes }).unwrap()
      setNotes('')
      toast.success('Note saved')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not save the note'))
    }
  }

  const saveTags = async () => {
    const tags = tagsInput
      .split(',')
      .map((t) => t.trim())
      .filter(Boolean)
    try {
      await patchTrade({ id: trade.id, tags }).unwrap()
      setTagsDirty(false)
      toast.success('Tags updated')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not update tags'))
    }
  }

  const setEmotion = async (emotionTag: EmotionTag) => {
    try {
      await patchTrade({ id: trade.id, emotionTag }).unwrap()
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not update the emotion tag'))
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Button asChild variant="ghost" size="sm" className="-ml-2 mb-2">
          <Link to="/journal">
            <ArrowLeftIcon aria-hidden />
            Journal
          </Link>
        </Button>
        <PageHeader
          title={detail.trade.instrument?.symbol ?? 'Trade'}
          description={`${trade.direction} · opened ${formatDateTime(trade.openedAt)}${
            trade.closedAt ? ` · closed ${formatDateTime(trade.closedAt)}` : ' · open'
          } · held ${holdingLabel(trade.holdingSeconds)}`}
          actions={
            <>
              {trade.status === 'open' ? (
                <Button size="sm" onClick={() => setCloseOpen(true)}>
                  Close trade
                </Button>
              ) : (
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => {
                    void reopenTrade(trade.id)
                      .unwrap()
                      .catch((err: unknown) =>
                        toast.error(getApiErrorMessage(err, 'Could not reopen')),
                      )
                  }}
                >
                  Reopen
                </Button>
              )}
              <Button
                size="sm"
                variant="outline"
                disabled={recomputing}
                onClick={() => {
                  void recompute(trade.id)
                    .unwrap()
                    .then(() => toast.success('Adherence recomputed'))
                    .catch((err: unknown) =>
                      toast.error(getApiErrorMessage(err, 'Could not recompute')),
                    )
                }}
              >
                <RefreshCwIcon aria-hidden />
                Rescore
              </Button>
            </>
          }
        />
      </div>

      {/* Headline stats */}
      <div className="flex flex-wrap items-center gap-x-6 gap-y-2">
        <span className="flex items-center gap-2">
          <Badge variant={trade.direction === 'long' ? 'success' : 'destructive'}>
            {trade.direction}
          </Badge>
          {trade.isPaper && <Badge variant="outline">paper</Badge>}
          <Badge variant="secondary">{trade.status}</Badge>
        </span>
        {trade.rRealised !== undefined && trade.rRealised !== null && (
          <span className="flex items-baseline gap-1.5">
            <RValue value={trade.rRealised} className="text-lg" />
            <span className="text-xs text-muted-foreground">realised</span>
          </span>
        )}
        <span
          className={cn(
            'font-mono text-lg tabular-nums',
            trade.realisedPnlMinor > 0
              ? 'text-success'
              : trade.realisedPnlMinor < 0
                ? 'text-destructive'
                : 'text-muted-foreground',
          )}
        >
          {formatMinor(trade.realisedPnlMinor, trade.currency)}
        </span>
        {detail.adherence && (
          <span className="flex items-center gap-2">
            <AdherenceChip score={detail.adherence.score} className="size-8 text-sm" />
            <span className="text-sm text-muted-foreground">
              adherence {detail.adherence.score}/100
            </span>
          </span>
        )}
        {trade.maePct !== undefined && trade.maePct !== null && (
          <span className="text-sm text-muted-foreground">
            MAE {trade.maePct.toFixed(1)}% · MFE {trade.mfePct?.toFixed(1) ?? '—'}%
          </span>
        )}
      </div>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <div className="flex flex-col gap-6">
          <PlanVsExecution detail={detail} />

          {/* Adherence deductions */}
          <Card className="gap-2 py-4">
            <CardHeader className="px-4">
              <CardTitle className="text-sm">Adherence deductions</CardTitle>
            </CardHeader>
            <CardContent className="px-4">
              {detail.adherence === undefined ? (
                <p className="text-sm text-muted-foreground">Scored when the trade closes.</p>
              ) : deductions.length === 0 ? (
                <p className="text-sm text-success">Clean execution — no deductions.</p>
              ) : (
                <ul className="flex flex-col gap-2">
                  {deductions.map((d) => (
                    <li key={d.code} className="rounded-lg border px-3 py-2">
                      <div className="flex items-center justify-between">
                        <span className="font-mono text-xs font-semibold">{d.code}</span>
                        <Badge variant="destructive">-{d.points}</Badge>
                      </div>
                      <p className="mt-1 text-xs break-words text-muted-foreground">{d.evidence}</p>
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>

          {/* Coach insight slot (rendered only when the API returns Insight rows) */}
          {detail.insights && detail.insights.length > 0 && (
            <Card className="gap-2 border-primary/40 py-4">
              <CardHeader className="px-4">
                <CardTitle className="flex items-center gap-1.5 text-sm">
                  <SparklesIcon className="size-4" aria-hidden />
                  Coach insight
                </CardTitle>
              </CardHeader>
              <CardContent className="flex flex-col gap-2 px-4">
                {detail.insights.map((insight) => {
                  let text = insight.contentJson
                  try {
                    const content = JSON.parse(insight.contentJson) as {
                      summary?: string
                      text?: string
                    }
                    text = content.summary ?? content.text ?? insight.contentJson
                  } catch {
                    // Render raw content when it is not structured.
                  }
                  return (
                    <p key={insight.id} className="text-sm break-words whitespace-pre-wrap">
                      {text}
                    </p>
                  )
                })}
              </CardContent>
            </Card>
          )}
        </div>

        <div className="flex flex-col gap-6">
          {/* Fills timeline */}
          <Card className="gap-2 py-4">
            <CardHeader className="px-4">
              <CardTitle className="text-sm">Fills</CardTitle>
            </CardHeader>
            <CardContent className="px-4">
              {detail.fills.length === 0 ? (
                <EmptyState
                  icon={CandlestickChartIcon}
                  title="No fill records"
                  hint="This trade was recorded without individual fills."
                  className="min-h-32"
                />
              ) : (
                <ol className="flex flex-col">
                  {detail.fills.map((fill, index) => (
                    <li key={fill.id} className="flex items-center gap-3 py-2">
                      <span
                        className={cn(
                          'size-2 shrink-0 rounded-full',
                          fill.side === 'buy' ? 'bg-success' : 'bg-destructive',
                        )}
                        aria-hidden
                      />
                      <span className="w-24 text-xs text-muted-foreground">
                        {formatDateTime(fill.at)}
                      </span>
                      <span className="text-sm font-medium">
                        {fill.side} {fill.qty} @ {fill.price}
                      </span>
                      <span className="ml-auto text-xs text-muted-foreground">
                        fee {formatMinor(fill.feeMinor, fill.feeCurrency)}
                      </span>
                      {index === 0 && <Badge variant="outline">first</Badge>}
                    </li>
                  ))}
                </ol>
              )}
            </CardContent>
          </Card>

          {/* Emotion + tags + notes */}
          <Card className="gap-2 py-4">
            <CardHeader className="px-4">
              <CardTitle className="text-sm">Review</CardTitle>
            </CardHeader>
            <CardContent className="flex flex-col gap-4 px-4">
              <div className="flex flex-col gap-1.5">
                <Label>Emotion at entry</Label>
                <Select
                  value={trade.emotionTag}
                  onValueChange={(v) => void setEmotion(v as EmotionTag)}
                >
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {EMOTIONS.map((e) => (
                      <SelectItem key={e} value={e}>
                        {e === 'none' ? 'not set' : e}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              <div className="flex flex-col gap-1.5">
                <Label htmlFor="trade-tags">Tags (comma separated)</Label>
                <div className="flex gap-2">
                  <Input
                    id="trade-tags"
                    value={tagsInput}
                    onChange={(e) => {
                      setTagsInput(e.target.value)
                      setTagsDirty(true)
                    }}
                    placeholder="breakout, a-plus, news-day"
                  />
                  <Button
                    variant="outline"
                    onClick={() => void saveTags()}
                    disabled={saving || !tagsDirty}
                  >
                    Save
                  </Button>
                </div>
              </div>

              <Separator />

              <div className="flex flex-col gap-1.5">
                <Label htmlFor="trade-notes">Journal note (markdown)</Label>
                <Textarea
                  id="trade-notes"
                  rows={4}
                  value={notes}
                  onChange={(e) => setNotes(e.target.value)}
                  placeholder="What did you see, what did you do, what would process-you have done?"
                />
                <Button
                  size="sm"
                  className="self-end"
                  onClick={() => void saveNotes()}
                  disabled={saving || notes.trim().length === 0}
                >
                  Add note
                </Button>
              </div>

              {detail.journalEntries.length > 0 && (
                <ul className="flex flex-col gap-2">
                  {detail.journalEntries.map((entry) => (
                    <li key={entry.id} className="rounded-lg bg-muted/50 px-3 py-2">
                      <p className="text-sm break-words whitespace-pre-wrap">{entry.notesMd}</p>
                      <p className="mt-1 text-xs text-muted-foreground">
                        {formatDateTime(entry.at)}
                      </p>
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>

          {/* Attachments placeholder */}
          <Card className="gap-2 py-4">
            <CardHeader className="px-4">
              <CardTitle className="flex items-center gap-1.5 text-sm">
                <PaperclipIcon className="size-4" aria-hidden />
                Attachments
              </CardTitle>
            </CardHeader>
            <CardContent className="px-4">
              <p className="text-sm text-muted-foreground">
                Chart screenshots and files will attach to journal notes here.
              </p>
            </CardContent>
          </Card>
        </div>
      </div>

      <CloseTradeDialog tradeId={trade.id} open={closeOpen} onOpenChange={setCloseOpen} />
    </div>
  )
}
